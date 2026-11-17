using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CallFlowViewer
{
    internal enum ReferenceKind
    {
        /// <summary>The method is called here (directly, or inside a lambda).</summary>
        Call,

        /// <summary>The method is handed over as a delegate: Task.Run(Work), Func&lt;int&gt; f = Work, &amp;Work.</summary>
        Delegate,

        /// <summary>The method is subscribed to an event: button.Click += OnClick.</summary>
        EventSubscription,

        /// <summary>Not a call at all: nameof(Work), &lt;see cref="Work"/&gt;, button.Click -= OnClick.</summary>
        Ignore,
    }

    internal struct ReferenceInfo
    {
        public ReferenceKind Kind;

        /// <summary>
        /// Call: the method a surrounding lambda is passed to ("Task.Run"), or null.
        /// Delegate: the method the delegate is passed to ("Task.Run"), or null when it is stored.
        /// EventSubscription: the event ("Button.Click").
        /// </summary>
        public string Detail;

        public IEventSymbol Event;
    }

    /// <summary>Works out what a reference to a method means: a call, a delegate, an event subscription, or nothing.</summary>
    internal static class ReferenceClassifier
    {
        public static ReferenceInfo Classify(SyntaxNode root, SemanticModel model, Location location, ISymbol referenced, CancellationToken ct)
        {
            var node = root.FindNode(location.SourceSpan, findInsideTrivia: true, getInnermostNodeForTie: true);
            if (node == null) return Info(ReferenceKind.Call);

            // <see cref="Work"/> in a doc comment
            if (node.AncestorsAndSelf(ascendOutOfTrivia: false).Any(n => n is StructuredTriviaSyntax))
                return Info(ReferenceKind.Ignore);

            var expr = ClimbName(node);
            if (IsInsideNameof(expr, model, ct)) return Info(ReferenceKind.Ignore);

            // Constructors and properties: every reference runs them.
            var method = referenced as IMethodSymbol;
            if (method == null || method.MethodKind == MethodKind.Constructor || method.MethodKind == MethodKind.StaticConstructor)
                return Info(ReferenceKind.Call);

            // Work(), obj.Work(), obj?.Work()
            if (expr.Parent is InvocationExpressionSyntax invocation && invocation.Expression == expr)
                return CallInfo(invocation, model, ct);

            // From here on the method is used as a value (a method group), not called.
            SyntaxNode value = expr;

            // new EventHandler(OnClick)
            if (value.Parent is ArgumentSyntax wrappedArg && wrappedArg.Parent is ArgumentListSyntax wrappedList &&
                wrappedList.Parent is BaseObjectCreationExpressionSyntax creation &&
                model.GetTypeInfo(creation, ct).Type?.TypeKind == TypeKind.Delegate)
            {
                value = creation;
            }

            // &Work (function pointer)
            if (value.Parent is PrefixUnaryExpressionSyntax unary && unary.IsKind(SyntaxKind.AddressOfExpression))
                return Info(ReferenceKind.Delegate, "a function pointer");

            if (value.Parent is AssignmentExpressionSyntax assignment && assignment.Right == value)
            {
                if (assignment.IsKind(SyntaxKind.SubtractAssignmentExpression))
                    return Info(ReferenceKind.Ignore);   // unsubscribing does not run anything

                if (assignment.IsKind(SyntaxKind.AddAssignmentExpression) &&
                    model.GetSymbolInfo(assignment.Left, ct).Symbol is IEventSymbol evt)
                    return EventInfo(evt);
            }

            // Task.Run(Work), list.ForEach(Process), new Thread(Run)
            return Info(ReferenceKind.Delegate, ReceiverName(value, model, ct));
        }

        /// <summary>
        /// True when this reference to an event raises it:
        /// Changed(...), Changed.Invoke(...), Changed?.Invoke(...), or var handler = Changed;
        /// </summary>
        public static bool IsEventRaise(SyntaxNode root, Location location)
        {
            var node = root.FindNode(location.SourceSpan, getInnermostNodeForTie: true);
            if (node == null) return false;
            var expr = ClimbName(node);
            var parent = expr.Parent;

            if (parent is InvocationExpressionSyntax call && call.Expression == expr)
                return true;

            if (parent is MemberAccessExpressionSyntax access && access.Expression == expr &&
                access.Name.Identifier.ValueText == "Invoke" && access.Parent is InvocationExpressionSyntax)
                return true;

            if (parent is ConditionalAccessExpressionSyntax conditional && conditional.Expression == expr &&
                conditional.WhenNotNull is InvocationExpressionSyntax whenNotNull &&
                whenNotNull.Expression is MemberBindingExpressionSyntax binding &&
                binding.Name.Identifier.ValueText == "Invoke")
                return true;

            // The classic thread-safe pattern: var handler = Changed; handler?.Invoke(...)
            if (parent is EqualsValueClauseSyntax equals && equals.Parent is VariableDeclaratorSyntax)
                return true;

            return false;
        }

        /// <summary>
        /// For a reference to an event inside "evt += Handler" / "evt += new EventHandler(Handler)", the handler method.
        /// Lambdas are not returned: they are not methods of their own.
        /// </summary>
        public static IMethodSymbol SubscribedHandler(SyntaxNode root, SemanticModel model, Location location, CancellationToken ct)
        {
            var node = root.FindNode(location.SourceSpan, getInnermostNodeForTie: true);
            if (node == null) return null;
            var expr = ClimbName(node);
            if (!(expr.Parent is AssignmentExpressionSyntax assignment) || assignment.Left != expr ||
                !assignment.IsKind(SyntaxKind.AddAssignmentExpression))
                return null;

            ExpressionSyntax handler = assignment.Right;
            if (handler is BaseObjectCreationExpressionSyntax creation && creation.ArgumentList != null &&
                creation.ArgumentList.Arguments.Count == 1)
                handler = creation.ArgumentList.Arguments[0].Expression;   // new EventHandler(OnClick)
            if (handler is AnonymousFunctionExpressionSyntax) return null;

            var info = model.GetSymbolInfo(handler, ct);
            var method = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
            return method != null && method.MethodKind != MethodKind.AnonymousFunction ? method : null;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>From the name to the whole expression: Work -> obj.Work, obj?.Work, Class.Work, (Work).</summary>
        internal static SyntaxNode ClimbName(SyntaxNode node)
        {
            var expr = node;
            while (expr.Parent != null)
            {
                var parent = expr.Parent;
                if (parent is MemberAccessExpressionSyntax access && access.Name == expr) { expr = parent; continue; }
                if (parent is MemberBindingExpressionSyntax binding && binding.Name == expr) { expr = parent; continue; }
                if (parent is QualifiedNameSyntax qualified && qualified.Right == expr) { expr = parent; continue; }
                if (parent is ParenthesizedExpressionSyntax) { expr = parent; continue; }
                break;
            }
            return expr;
        }

        private static bool IsInsideNameof(SyntaxNode expr, SemanticModel model, CancellationToken ct)
        {
            if (expr.Parent is ArgumentSyntax arg && arg.Parent is ArgumentListSyntax list &&
                list.Parent is InvocationExpressionSyntax invocation &&
                invocation.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == "nameof")
            {
                // A real method called "nameof" would bind to a symbol; the operator does not.
                return model.GetSymbolInfo(invocation, ct).Symbol == null;
            }
            return false;
        }

        private static ReferenceInfo CallInfo(SyntaxNode call, SemanticModel model, CancellationToken ct)
        {
            var lambda = call.Ancestors()
                .TakeWhile(a => !(a is MemberDeclarationSyntax) && !(a is LocalFunctionStatementSyntax))
                .OfType<AnonymousFunctionExpressionSyntax>()
                .FirstOrDefault();
            if (lambda == null) return Info(ReferenceKind.Call);

            // button.Click += (s, e) => OnClick();  -> runs when the event fires
            if (lambda.Parent is AssignmentExpressionSyntax assignment && assignment.Right == lambda &&
                assignment.IsKind(SyntaxKind.AddAssignmentExpression) &&
                model.GetSymbolInfo(assignment.Left, ct).Symbol is IEventSymbol evt)
                return EventInfo(evt);

            // Task.Run(() => Work());  -> still a call, made when the lambda runs
            return Info(ReferenceKind.Call, ReceiverName(lambda, model, ct));
        }

        /// <summary>The method that receives the value as an argument: "Task.Run", "new Thread", "List.ForEach".</summary>
        private static string ReceiverName(SyntaxNode value, SemanticModel model, CancellationToken ct)
        {
            if (!(value.Parent is ArgumentSyntax arg) || !(arg.Parent is BaseArgumentListSyntax list) || list.Parent == null)
                return null;

            var owner = list.Parent;
            var info = model.GetSymbolInfo(owner, ct);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol is IMethodSymbol m)
            {
                if (m.MethodKind == MethodKind.Constructor) return "new " + m.ContainingType.Name;
                return (m.ContainingType != null ? m.ContainingType.Name + "." : "") + m.Name;
            }

            if (owner is InvocationExpressionSyntax invocation)
            {
                var text = invocation.Expression.ToString();
                return text.Length > 60 ? text.Substring(0, 57) + "..." : text;
            }
            return null;
        }

        private static ReferenceInfo EventInfo(IEventSymbol evt)
            => new ReferenceInfo
            {
                Kind = ReferenceKind.EventSubscription,
                Event = evt,
                Detail = evt.ContainingType != null ? evt.ContainingType.Name + "." + evt.Name : evt.Name,
            };

        private static ReferenceInfo Info(ReferenceKind kind, string detail = null)
            => new ReferenceInfo { Kind = kind, Detail = detail };
    }
}

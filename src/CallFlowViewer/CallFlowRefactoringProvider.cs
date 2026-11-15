using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CallFlowViewer
{
    /// <summary>
    /// Adds "Show call flow to this method" to the Ctrl+. (lightbulb) menu when the caret is on
    /// a method / constructor / property declaration, or on a call to one.
    /// </summary>
    [ExportCodeRefactoringProvider(Microsoft.CodeAnalysis.LanguageNames.CSharp, Name = nameof(CallFlowRefactoringProvider)), Shared]
    public sealed class CallFlowRefactoringProvider : CodeRefactoringProvider
    {
        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var document = context.Document;
            var ct = context.CancellationToken;

            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root == null) return;

            var node = root.FindNode(context.Span, getInnermostNodeForTie: true);
            var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (semanticModel == null) return;

            var symbol = GetDeclaredSymbolAtHeader(node, context.Span.Start, semanticModel, ct)
                         ?? GetReferencedSymbol(node, semanticModel, ct);
            if (symbol == null) return;

            symbol = symbol.OriginalDefinition;
            if (!symbol.Locations.Any(l => l.IsInSource)) return; // only methods that live in this solution

            context.RegisterRefactoring(new ShowCallFlowCodeAction(document.Project.Solution, symbol));
        }

        /// <summary>Caret is on the signature of a declaration (not inside its body).</summary>
        private static ISymbol GetDeclaredSymbolAtHeader(SyntaxNode node, int position, SemanticModel model, System.Threading.CancellationToken ct)
        {
            foreach (var ancestor in node.AncestorsAndSelf())
            {
                SyntaxNode bodyStart = null;
                switch (ancestor)
                {
                    case BaseMethodDeclarationSyntax m: // methods, constructors, operators, destructors
                        bodyStart = (SyntaxNode)m.Body ?? m.ExpressionBody;
                        break;
                    case PropertyDeclarationSyntax p:
                        bodyStart = (SyntaxNode)p.AccessorList ?? p.ExpressionBody;
                        break;
                    case LocalFunctionStatementSyntax _:
                    case AnonymousFunctionExpressionSyntax _:
                        return null; // flow is reported for the containing member instead
                    case BlockSyntax _:
                    case ArrowExpressionClauseSyntax _:
                    case AccessorListSyntax _:
                        return null; // caret is inside a body: only references count there
                    case TypeDeclarationSyntax _:
                        return null;
                    default:
                        continue;
                }

                if (bodyStart != null && position >= bodyStart.SpanStart) return null;
                var declared = model.GetDeclaredSymbol(ancestor, ct);
                return IsSupported(declared) ? declared : null;
            }
            return null;
        }

        /// <summary>Caret is on a call, e.g. _service.PlaceOrder(...) → flow to PlaceOrder.</summary>
        private static ISymbol GetReferencedSymbol(SyntaxNode node, SemanticModel model, System.Threading.CancellationToken ct)
        {
            var name = node as SimpleNameSyntax ?? node.FirstAncestorOrSelf<SimpleNameSyntax>();
            if (name == null) return null;
            var info = model.GetSymbolInfo(name, ct);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            return IsSupported(symbol) ? symbol : null;
        }

        private static bool IsSupported(ISymbol symbol)
        {
            switch (symbol)
            {
                case IMethodSymbol m:
                    return m.MethodKind == MethodKind.Ordinary
                        || m.MethodKind == MethodKind.Constructor
                        || m.MethodKind == MethodKind.ExplicitInterfaceImplementation
                        || m.MethodKind == MethodKind.UserDefinedOperator
                        || m.MethodKind == MethodKind.PropertyGet
                        || m.MethodKind == MethodKind.PropertySet;
                case IPropertySymbol _:
                    return true;
                default:
                    return false;
            }
        }
    }
}

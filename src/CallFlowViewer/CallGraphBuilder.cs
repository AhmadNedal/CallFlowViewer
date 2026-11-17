using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CallFlowViewer
{
    internal sealed class FlowNode
    {
        public string Id;
        public string Name;        // OrderService.PlaceOrder(Order)
        public string Namespace;
        public string Project;
        public string File;        // relative to the solution folder
        public int Line;           // 1-based
        public string EntryKind;   // null when the node has callers
        public string EntryDetail; // e.g. "POST api/orders"
        public bool Cut;           // callers were not explored (depth / size limit)

        // Details shown in the report
        public string Kind;           // Method / Constructor / Property / Operator
        public string Signature;      // public async Task<Guid> OrderService.PlaceOrderAsync(string customer)
        public string ContainingType; // Shop.Core.OrderService
        public string Accessibility;  // public / internal / ...
        public string ReturnType;
        public string Summary;        // XML doc <summary>
        public readonly List<string> Modifiers = new List<string>();
        public readonly List<string> Attributes = new List<string>();
        public int SourceStart;       // line number of Source[0]
        public bool SourceTruncated;
        public readonly List<string> Source = new List<string>(); // full code of the member
        public readonly List<KeyValuePair<string, string>> Parameters = new List<KeyValuePair<string, string>>(); // name -> type

        /// <summary>Events this method is subscribed to (button.Click += OnClick).</summary>
        public readonly List<EventSubscription> Subscriptions = new List<EventSubscription>();

        /// <summary>Callee graphs only: methods outside the solution that this method calls (DbContext.SaveChangesAsync...).</summary>
        public readonly List<string> External = new List<string>();

        /// <summary>DI registrations whose implementation is this method's class.</summary>
        public readonly List<DiRegistration> DiRegistrations = new List<DiRegistration>();
    }

    internal sealed class EventSubscription
    {
        public string Event;   // Button.Click
        public string By;      // MainForm.InitializeComponent()
        public string File;
        public int Line;
        public string Code;
    }

    internal sealed class CallSite
    {
        public string File;
        public int Line;
        public int Column;
        public string Code;
        public int ContextStart;      // line number of Context[0]
        public readonly List<string> Context = new List<string>(); // a few lines around the call

        /// <summary>call / delegate / event</summary>
        public string Kind;

        /// <summary>call: method a surrounding lambda is passed to; delegate: receiving method; event: event name.</summary>
        public string Detail;
    }

    internal sealed class FlowEdge
    {
        public string From;        // caller
        public string To;          // callee
        public string Via;         // "interface IOrderService" / "base Repository.Save" / null
        public ITypeSymbol ViaType; // the interface / base type of Via (not serialised)
        public bool InsideLambda;

        /// <summary>call / delegate / event: how the caller reaches the callee (the strongest kind among the sites).</summary>
        public string Kind = "call";
        public string Detail;

        /// <summary>DI check for calls made through an interface or base type; null when not checked.</summary>
        public DiCheck Di;

        public readonly List<CallSite> Sites = new List<CallSite>();
    }

    internal sealed class CallGraph
    {
        /// <summary>"callers": how the code reaches the target. "callees": what the target calls.</summary>
        public string Direction = "callers";
        public string TargetId;
        public string SolutionName;
        public DateTime GeneratedAt;
        public bool Truncated;
        public CallFlowSettings Settings;
        public string SettingsSource;
        public readonly List<string> ExcludedProjects = new List<string>();
        public int SkippedByNamespace;
        public bool DiScanned;
        public int DiRegistrationCount;
        public readonly Dictionary<string, FlowNode> Nodes = new Dictionary<string, FlowNode>();
        public readonly List<FlowEdge> Edges = new List<FlowEdge>();
    }

    /// <summary>
    /// Walks callers backwards from the target (breadth first). Follows calls made through interfaces and
    /// base classes, methods passed as delegates, and event handlers (back to the code that raises the event).
    /// </summary>
    internal sealed class CallGraphBuilder
    {
        private sealed class CallTarget
        {
            public ISymbol Symbol;
            public string Via;
            public ITypeSymbol ViaType;
        }

        private readonly Solution _solution;
        private readonly CallFlowSettings _settings;
        private readonly string _solutionDir;

        private readonly CallGraph _graph = new CallGraph();
        private readonly Dictionary<string, ISymbol> _symbols = new Dictionary<string, ISymbol>();
        private readonly Dictionary<(string, string), FlowEdge> _edgeIndex = new Dictionary<(string, string), FlowEdge>();
        private readonly HashSet<string> _hasCallers = new HashSet<string>();
        private readonly Dictionary<DocumentId, (SyntaxNode Root, SemanticModel Model)> _documents = new Dictionary<DocumentId, (SyntaxNode Root, SemanticModel Model)>();
        private readonly Dictionary<IEventSymbol, List<(ISymbol Caller, bool InsideLambda, Location Site)>> _eventRaises =
            new Dictionary<IEventSymbol, List<(ISymbol Caller, bool InsideLambda, Location Site)>>(SymbolEqualityComparer.Default);

        private readonly Queue<(ISymbol Symbol, int Depth)> _queue = new Queue<(ISymbol Symbol, int Depth)>();
        private readonly HashSet<string> _expanded = new HashSet<string>();

        /// <summary>Documents to search for callers; null = the whole solution.</summary>
        private IImmutableSet<Document> _scope;

        public CallGraphBuilder(Solution solution, CallFlowSettings settings)
        {
            _solution = solution;
            _settings = settings ?? CallFlowSettings.Default;
            _solutionDir = string.IsNullOrEmpty(solution.FilePath) ? null : Path.GetDirectoryName(solution.FilePath);
        }

        private void Start(ISymbol target, string direction)
        {
            _graph.Direction = direction;
            _graph.Settings = _settings;
            _graph.SettingsSource = _settings.Source == null || _settings.Source == ".editorconfig" ? _settings.Source : Relative(_settings.Source);
            _graph.GeneratedAt = DateTime.Now;
            _graph.SolutionName = string.IsNullOrEmpty(_solution.FilePath) ? null : Path.GetFileName(_solution.FilePath);
            _graph.TargetId = AddNode(target).Id;
            _scope = BuildSearchScope(target);
        }

        /// <summary>Who calls the target, all the way back to where the flow starts.</summary>
        public async Task<CallGraph> BuildAsync(ISymbol target, CancellationToken ct)
        {
            target = Normalize(target, out _);
            Start(target, "callers");

            _queue.Enqueue((target, 0));
            while (_queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (symbol, depth) = _queue.Dequeue();
                var id = KeyOf(symbol);
                if (!_expanded.Add(id)) continue;

                if (depth >= _settings.MaxDepth || _graph.Nodes.Count >= _settings.MaxNodes)
                {
                    _graph.Nodes[id].Cut = true;
                    _graph.Truncated = true;
                    continue;
                }

                await ExpandAsync(symbol, id, depth, ct).ConfigureAwait(false);
            }

            foreach (var edge in _graph.Edges) FinishEdge(edge);
            ClassifyStartingPoints();
            await CheckDependencyInjectionAsync(ct).ConfigureAwait(false);
            return _graph;
        }

        /// <summary>What the root method calls, and what those methods call, as deep as the limits allow.</summary>
        public async Task<CallGraph> BuildCalleesAsync(ISymbol root, CancellationToken ct)
        {
            root = Normalize(root, out _);
            Start(root, "callees");

            _queue.Enqueue((root, 0));
            while (_queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (symbol, depth) = _queue.Dequeue();
                var id = KeyOf(symbol);
                if (!_expanded.Add(id)) continue;

                if (depth >= _settings.MaxDepth || _graph.Nodes.Count >= _settings.MaxNodes)
                {
                    _graph.Nodes[id].Cut = true;
                    _graph.Truncated = true;
                    continue;
                }

                await ExpandCalleesAsync(symbol, id, depth, ct).ConfigureAwait(false);
            }

            foreach (var edge in _graph.Edges) FinishEdge(edge);
            await CheckDependencyInjectionAsync(ct).ConfigureAwait(false);
            return _graph;
        }

        // ------------------------------------------------------------------ search (callees)

        private const int MaxExternalPerMethod = 20;
        private const int MaxImplementationsPerCall = 12;

        private async Task ExpandCalleesAsync(ISymbol symbol, string id, int depth, CancellationToken ct)
        {
            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                var document = _solution.GetDocument(reference.SyntaxTree);
                if (document == null) continue;
                var (root, model) = await GetDocumentAsync(document, ct).ConfigureAwait(false);
                if (root == null || model == null) continue;

                var declaration = await reference.GetSyntaxAsync(ct).ConfigureAwait(false);
                foreach (var node in declaration.DescendantNodes())
                {
                    ct.ThrowIfCancellationRequested();
                    switch (node)
                    {
                        case SimpleNameSyntax name:
                            await VisitNameAsync(id, depth, name, root, model, ct).ConfigureAwait(false);
                            break;
                        case BaseObjectCreationExpressionSyntax _:
                        case ConstructorInitializerSyntax _:
                            if (model.GetSymbolInfo(node, ct).Symbol is IMethodSymbol ctor && !ctor.IsImplicitlyDeclared)
                                await AddCalleeAsync(id, depth, ctor, node.GetLocation(), "call", null, IsInsideLambda(node, declaration), ct).ConfigureAwait(false);
                            break;
                    }
                }
            }
        }

        private async Task VisitNameAsync(string callerId, int depth, SimpleNameSyntax name, SyntaxNode root, SemanticModel model, CancellationToken ct)
        {
            var info = model.GetSymbolInfo(name, ct);
            var symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
            switch (symbol)
            {
                case IMethodSymbol method:
                {
                    if (method.MethodKind == MethodKind.ReducedExtension && method.ReducedFrom != null) method = method.ReducedFrom;
                    if (method.MethodKind != MethodKind.Ordinary && method.MethodKind != MethodKind.ExplicitInterfaceImplementation) return;

                    var reference = ReferenceClassifier.Classify(root, model, name.GetLocation(), method, ct);
                    if (reference.Kind == ReferenceKind.Ignore || reference.Kind == ReferenceKind.EventSubscription) return;   // subscribing runs nothing now
                    var kind = reference.Kind == ReferenceKind.Delegate ? "delegate" : "call";
                    await AddCalleeAsync(callerId, depth, method, name.GetLocation(), kind, reference.Detail, IsInsideLambda(name, null), ct).ConfigureAwait(false);
                    break;
                }
                case IPropertySymbol property when HasBody(property):
                {
                    var reference = ReferenceClassifier.Classify(root, model, name.GetLocation(), property, ct);
                    if (reference.Kind == ReferenceKind.Ignore) return;
                    await AddCalleeAsync(callerId, depth, property, name.GetLocation(), "call", null, IsInsideLambda(name, null), ct).ConfigureAwait(false);
                    break;
                }
                case IEventSymbol evt when ReferenceClassifier.IsEventRaise(root, name.GetLocation()):
                {
                    var eventName = evt.ContainingType != null ? evt.ContainingType.Name + "." + evt.Name : evt.Name;
                    foreach (var handler in await FindEventHandlersAsync(evt, ct).ConfigureAwait(false))
                        await AddCalleeAsync(callerId, depth, handler, name.GetLocation(), "event", eventName, IsInsideLambda(name, null), ct).ConfigureAwait(false);
                    break;
                }
            }
        }

        private async Task AddCalleeAsync(string callerId, int depth, ISymbol callee, Location location, string kind, string detail, bool insideLambda, CancellationToken ct)
        {
            callee = Normalize(callee, out _);
            if (callee == null) return;
            var callerNode = _graph.Nodes[callerId];

            var targets = await ResolveDispatchAsync(callee, ct).ConfigureAwait(false);
            if (targets.Count == 0)
            {
                AddExternal(callerNode, callee, callee.IsAbstract ? " (no implementation found)" : "");
                return;
            }

            foreach (var target in targets)
            {
                var symbol = target.Symbol;
                if (!symbol.Locations.Any(l => l.IsInSource))
                {
                    AddExternal(callerNode, symbol, "");
                    continue;
                }
                if (IsExcluded(symbol)) continue;

                var id = KeyOf(symbol);
                if (id == callerId) continue;   // recursion, local functions
                if (!_graph.Nodes.ContainsKey(id) && _graph.Nodes.Count >= _settings.MaxNodes)
                {
                    callerNode.Cut = true;
                    _graph.Truncated = true;
                    continue;
                }

                var node = AddNode(symbol);
                var edge = GetOrAddEdge(callerId, node.Id, target.Via, target.ViaType);
                edge.InsideLambda |= insideLambda;
                AddSite(edge, location, kind, detail, ct);

                if (!_expanded.Contains(node.Id))
                    _queue.Enqueue((symbol, depth + 1));
            }
        }

        /// <summary>
        /// Where a call can land at runtime: the method itself (unless abstract), every implementation
        /// of an interface member, and every override of a virtual / abstract member.
        /// </summary>
        private async Task<List<CallTarget>> ResolveDispatchAsync(ISymbol callee, CancellationToken ct)
        {
            var result = new List<CallTarget>();

            // Outside the solution (a library interface like ILogger too): report the call as it is written.
            var type = callee.ContainingType;
            if (type == null || !callee.Locations.Any(l => l.IsInSource))
            {
                result.Add(new CallTarget { Symbol = callee });
                return result;
            }

            if (!callee.IsAbstract) result.Add(new CallTarget { Symbol = callee });

            IEnumerable<ISymbol> others = Enumerable.Empty<ISymbol>();
            string via = null;
            if (type.TypeKind == TypeKind.Interface)
            {
                others = await SymbolFinder.FindImplementationsAsync(callee, _solution, (IImmutableSet<Project>)null, ct).ConfigureAwait(false);
                via = "interface " + type.Name;
            }
            else if (callee.IsAbstract || callee.IsVirtual || callee.IsOverride)
            {
                others = await SymbolFinder.FindOverridesAsync(callee, _solution, (IImmutableSet<Project>)null, ct).ConfigureAwait(false);
                via = "base " + type.Name + "." + callee.Name;
            }

            foreach (var other in others.Take(MaxImplementationsPerCall))
            {
                var definition = Normalize(other, out _);
                if (definition == null || result.Any(r => SymbolEqualityComparer.Default.Equals(r.Symbol, definition))) continue;
                result.Add(new CallTarget { Symbol = definition, Via = via, ViaType = type });
            }
            return result;
        }

        /// <summary>Methods subscribed with "evt += Handler" anywhere in the searched projects.</summary>
        private async Task<List<IMethodSymbol>> FindEventHandlersAsync(IEventSymbol evt, CancellationToken ct)
        {
            var handlers = new List<IMethodSymbol>();
            if (!evt.Locations.Any(l => l.IsInSource)) return handlers;

            var references = await SymbolFinder.FindReferencesAsync(evt.OriginalDefinition, _solution, _scope, ct).ConfigureAwait(false);
            foreach (var referenced in references)
            {
                foreach (var location in referenced.Locations)
                {
                    if (!location.Location.IsInSource) continue;
                    var (root, model) = await GetDocumentAsync(location.Document, ct).ConfigureAwait(false);
                    if (root == null || model == null) continue;
                    var handler = ReferenceClassifier.SubscribedHandler(root, model, location.Location, ct);
                    if (handler != null && !handlers.Any(h => SymbolEqualityComparer.Default.Equals(h, handler.OriginalDefinition)))
                        handlers.Add((IMethodSymbol)handler.OriginalDefinition);
                }
            }
            return handlers;
        }

        private bool IsExcluded(ISymbol symbol)
        {
            if (_settings.IsNamespaceExcluded(symbol.ContainingNamespace))
            {
                _graph.SkippedByNamespace++;
                return true;
            }
            if (_scope == null) return false;
            var tree = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree;
            var document = tree != null ? _solution.GetDocument(tree) : null;
            return document != null && !_scope.Contains(document);
        }

        private static void AddExternal(FlowNode node, ISymbol symbol, string suffix)
        {
            if (node.External.Count >= MaxExternalPerMethod) return;
            var text = (symbol.ContainingType != null ? symbol.ContainingType.Name + "." : "") + symbol.Name + suffix;
            if (!node.External.Contains(text)) node.External.Add(text);
        }

        /// <summary>Properties with code in their accessors; auto-properties are just fields and add noise.</summary>
        private static bool HasBody(IPropertySymbol property)
        {
            foreach (var reference in property.DeclaringSyntaxReferences)
            {
                var syntax = reference.GetSyntax();
                if (syntax is PropertyDeclarationSyntax declaration)
                {
                    if (declaration.ExpressionBody != null) return true;
                    if (declaration.AccessorList != null &&
                        declaration.AccessorList.Accessors.Any(a => a.Body != null || a.ExpressionBody != null))
                        return true;
                }
                else if (syntax is IndexerDeclarationSyntax)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsInsideLambda(SyntaxNode node, SyntaxNode stopAt)
        {
            foreach (var ancestor in node.Ancestors())
            {
                if (ancestor == stopAt || ancestor is MemberDeclarationSyntax) return false;
                if (ancestor is AnonymousFunctionExpressionSyntax || ancestor is LocalFunctionStatementSyntax) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ search (callers)

        /// <summary>
        /// Projects excluded in .editorconfig are left out of the search entirely, which is what makes
        /// excluding test projects fast. The target's own project is always searched.
        /// </summary>
        private IImmutableSet<Document> BuildSearchScope(ISymbol target)
        {
            var targetProject = target.Locations
                .Where(l => l.IsInSource)
                .Select(l => _solution.GetDocument(l.SourceTree)?.Project.Id)
                .FirstOrDefault(p => p != null);

            var included = new List<Document>();
            var excludedAny = false;
            foreach (var project in _solution.Projects)
            {
                if (project.Id != targetProject && _settings.IsProjectExcluded(project))
                {
                    excludedAny = true;
                    if (!_graph.ExcludedProjects.Contains(project.Name)) _graph.ExcludedProjects.Add(project.Name);
                    continue;
                }
                included.AddRange(project.Documents);
            }
            return excludedAny ? ImmutableHashSet.CreateRange(included) : null;
        }

        private async Task ExpandAsync(ISymbol symbol, string id, int depth, CancellationToken ct)
        {
            var targets = await GetCallTargetsAsync(symbol, ct).ConfigureAwait(false);
            var searched = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var seenLocations = new HashSet<(string, int)>();

            foreach (var target in targets)
            {
                // A search on the method usually also returns the references to the interface / base
                // members it cascades to; only search those again if they did not come back.
                if (searched.Contains(target.Symbol)) continue;

                var references = await SymbolFinder.FindReferencesAsync(target.Symbol, _solution, _scope, ct).ConfigureAwait(false);
                foreach (var referenced in references)
                {
                    var definition = referenced.Definition.OriginalDefinition;
                    var match = targets.FirstOrDefault(t => SymbolEqualityComparer.Default.Equals(t.Symbol, definition));
                    if (match == null) continue;   // a sibling implementation or an override: it never runs this method
                    searched.Add(match.Symbol);

                    foreach (var location in referenced.Locations)
                    {
                        if (!location.Location.IsInSource) continue;
                        if (!seenLocations.Add((location.Location.SourceTree.FilePath, location.Location.SourceSpan.Start))) continue;
                        await AddReferenceAsync(id, depth, match, location, ct).ConfigureAwait(false);
                    }
                }
                searched.Add(target.Symbol);
            }
        }

        private async Task AddReferenceAsync(string calleeId, int depth, CallTarget target, ReferenceLocation location, CancellationToken ct)
        {
            var (root, model) = await GetDocumentAsync(location.Document, ct).ConfigureAwait(false);
            if (root == null || model == null) return;

            var info = ReferenceClassifier.Classify(root, model, location.Location, target.Symbol, ct);
            if (info.Kind == ReferenceKind.Ignore) return;

            var caller = Normalize(model.GetEnclosingSymbol(location.Location.SourceSpan.Start, ct), out var insideLambda);
            if (!IsCaller(caller)) return;
            if (_settings.IsNamespaceExcluded(caller.ContainingNamespace))
            {
                _graph.SkippedByNamespace++;
                return;
            }

            if (info.Kind == ReferenceKind.EventSubscription)
            {
                // Subscribing does not run the handler: whoever raises the event does.
                AddSubscription(calleeId, info.Detail, caller, location.Location, ct);
                if (info.Event != null && info.Event.Locations.Any(l => l.IsInSource))
                {
                    foreach (var raise in await FindEventRaisesAsync(info.Event, ct).ConfigureAwait(false))
                        AddCaller(raise.Caller, raise.InsideLambda, calleeId, depth, null, raise.Site, "event", info.Detail, ct);
                }
                return;
            }

            AddCaller(caller, insideLambda, calleeId, depth, target, location.Location,
                info.Kind == ReferenceKind.Delegate ? "delegate" : "call", info.Detail, ct);
        }

        private void AddCaller(ISymbol caller, bool insideLambda, string calleeId, int depth, CallTarget target,
                               Location location, string kind, string detail, CancellationToken ct)
        {
            var callerId = KeyOf(caller);
            if (!_graph.Nodes.ContainsKey(callerId) && _graph.Nodes.Count >= _settings.MaxNodes)
            {
                // More callers exist than the method limit allows.
                _graph.Nodes[calleeId].Cut = true;
                _graph.Truncated = true;
                return;
            }

            var callerNode = AddNode(caller);
            if (callerNode.Id != calleeId) _hasCallers.Add(calleeId); // plain recursion doesn't count as "has callers"

            var edge = GetOrAddEdge(callerNode.Id, calleeId, target?.Via, target?.ViaType);
            edge.InsideLambda |= insideLambda;
            AddSite(edge, location, kind, detail, ct);

            if (!_expanded.Contains(callerNode.Id))
                _queue.Enqueue((caller, depth + 1));
        }

        /// <summary>Methods that raise the event: Changed?.Invoke(...), Changed(...), var h = Changed.</summary>
        private async Task<List<(ISymbol Caller, bool InsideLambda, Location Site)>> FindEventRaisesAsync(IEventSymbol evt, CancellationToken ct)
        {
            evt = (IEventSymbol)evt.OriginalDefinition;
            if (_eventRaises.TryGetValue(evt, out var cached)) return cached;

            var list = new List<(ISymbol Caller, bool InsideLambda, Location Site)>();
            _eventRaises[evt] = list;

            var references = await SymbolFinder.FindReferencesAsync(evt, _solution, _scope, ct).ConfigureAwait(false);
            foreach (var referenced in references)
            {
                foreach (var location in referenced.Locations)
                {
                    if (!location.Location.IsInSource) continue;
                    var (root, model) = await GetDocumentAsync(location.Document, ct).ConfigureAwait(false);
                    if (root == null || model == null || !ReferenceClassifier.IsEventRaise(root, location.Location)) continue;

                    var caller = Normalize(model.GetEnclosingSymbol(location.Location.SourceSpan.Start, ct), out var insideLambda);
                    if (!IsCaller(caller) || _settings.IsNamespaceExcluded(caller.ContainingNamespace)) continue;
                    list.Add((caller, insideLambda, location.Location));
                }
            }
            return list;
        }

        private void AddSubscription(string handlerId, string eventName, ISymbol subscriber, Location location, CancellationToken ct)
        {
            var node = _graph.Nodes[handlerId];
            var span = location.GetLineSpan();
            var file = Relative(span.Path);
            var line = span.StartLinePosition.Line + 1;
            if (node.Subscriptions.Any(s => s.File == file && s.Line == line)) return;

            node.Subscriptions.Add(new EventSubscription
            {
                Event = eventName,
                By = SymbolText.Display(subscriber),
                File = file,
                Line = line,
                Code = LineText(location, span.StartLinePosition.Line, ct),
            });
        }

        private async Task<(SyntaxNode Root, SemanticModel Model)> GetDocumentAsync(Document document, CancellationToken ct)
        {
            if (document == null) return (null, null);
            if (_documents.TryGetValue(document.Id, out var cached)) return cached;

            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            var entry = (root, model);
            _documents[document.Id] = entry;
            return entry;
        }

        /// <summary>
        /// The symbol itself plus every interface member it implements and every base member it overrides:
        /// a call to IOrderService.PlaceOrder or BaseService.PlaceOrder may end up here at runtime.
        /// </summary>
        private async Task<List<CallTarget>> GetCallTargetsAsync(ISymbol symbol, CancellationToken ct)
        {
            var result = new List<CallTarget> { new CallTarget { Symbol = symbol } };
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { symbol };

            var interfaceMembers = await SymbolFinder.FindImplementedInterfaceMembersAsync(symbol, _solution, cancellationToken: ct).ConfigureAwait(false);
            foreach (var m in interfaceMembers)
            {
                var definition = m.OriginalDefinition;
                if (seen.Add(definition))
                    result.Add(new CallTarget { Symbol = definition, Via = "interface " + m.ContainingType.Name, ViaType = m.ContainingType });
            }

            for (var b = Overridden(symbol); b != null; b = Overridden(b))
            {
                var definition = b.OriginalDefinition;
                if (seen.Add(definition))
                    result.Add(new CallTarget { Symbol = definition, Via = "base " + b.ContainingType.Name + "." + b.Name, ViaType = b.ContainingType });
            }

            return result;
        }

        private static ISymbol Overridden(ISymbol s)
        {
            switch (s)
            {
                case IMethodSymbol m: return m.OverriddenMethod;
                case IPropertySymbol p: return p.OverriddenProperty;
                default: return null;
            }
        }

        /// <summary>Lambdas / local functions / accessors are reported as their containing member.</summary>
        private static ISymbol Normalize(ISymbol symbol, out bool insideLambda)
        {
            insideLambda = false;
            while (symbol is IMethodSymbol m &&
                   (m.MethodKind == MethodKind.AnonymousFunction || m.MethodKind == MethodKind.LocalFunction))
            {
                insideLambda = true;
                symbol = m.ContainingSymbol;
            }

            if (symbol is IMethodSymbol acc &&
                (acc.MethodKind == MethodKind.PropertyGet || acc.MethodKind == MethodKind.PropertySet) &&
                acc.AssociatedSymbol != null)
            {
                symbol = acc.AssociatedSymbol;
            }

            return symbol?.OriginalDefinition;
        }

        /// <summary>Only members can be callers: not types (attribute arguments), namespaces or event accessors.</summary>
        private static bool IsCaller(ISymbol symbol)
        {
            switch (symbol)
            {
                case IMethodSymbol m:
                    if (m.MethodKind == MethodKind.EventAdd || m.MethodKind == MethodKind.EventRemove) return false;
                    break;
                case IPropertySymbol _:
                case IFieldSymbol _:
                    break;
                default:
                    return false;
            }
            return symbol.Locations.Any(l => l.IsInSource);
        }

        // ------------------------------------------------------------------ graph

        private FlowNode AddNode(ISymbol symbol)
        {
            var id = KeyOf(symbol);
            if (_graph.Nodes.TryGetValue(id, out var existing)) return existing;

            var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            var span = loc?.GetLineSpan();
            var node = new FlowNode
            {
                Id = id,
                Name = SymbolText.Display(symbol),
                Namespace = symbol.ContainingNamespace?.IsGlobalNamespace == false ? symbol.ContainingNamespace.ToDisplayString() : "",
                Project = loc != null ? _solution.GetDocument(loc.SourceTree)?.Project.Name : null,
                File = span.HasValue ? Relative(span.Value.Path) : null,
                Line = span.HasValue ? span.Value.StartLinePosition.Line + 1 : 0,
            };
            SymbolDetails.Fill(node, symbol);
            FillSource(node, symbol);
            _graph.Nodes.Add(id, node);
            _symbols[id] = symbol;
            return node;
        }

        private const int MaxSourceLines = 300;

        /// <summary>Full source text of the member, shown when a box is double-clicked in the diagram.</summary>
        private static void FillSource(FlowNode node, ISymbol symbol)
        {
            try
            {
                var reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
                if (reference == null) return;

                var text = reference.SyntaxTree.GetText();
                var first = text.Lines.GetLineFromPosition(reference.Span.Start).LineNumber;
                var last = text.Lines.GetLineFromPosition(reference.Span.End).LineNumber;
                if (last - first + 1 > MaxSourceLines)
                {
                    last = first + MaxSourceLines - 1;
                    node.SourceTruncated = true;
                }

                node.SourceStart = first + 1;
                for (var i = first; i <= last; i++)
                {
                    var line = text.Lines[i].ToString().TrimEnd();
                    node.Source.Add(line.Length > 300 ? line.Substring(0, 297) + "..." : line);
                }
            }
            catch
            {
                // The code listing is a bonus: never fail the report because of it.
            }
        }

        private FlowEdge GetOrAddEdge(string from, string to, string via, ITypeSymbol viaType)
        {
            if (_edgeIndex.TryGetValue((from, to), out var edge))
            {
                if (edge.Via != null && via == null)
                {
                    // A direct call wins over a call through an interface.
                    edge.Via = null;
                    edge.ViaType = null;
                }
                return edge;
            }
            edge = new FlowEdge { From = from, To = to, Via = via, ViaType = viaType };
            _edgeIndex.Add((from, to), edge);
            _graph.Edges.Add(edge);
            return edge;
        }

        private void AddSite(FlowEdge edge, Location loc, string kind, string detail, CancellationToken ct)
        {
            var span = loc.GetLineSpan();
            var line = span.StartLinePosition.Line;
            var file = Relative(span.Path);
            if (edge.Sites.Any(s => s.File == file && s.Line == line + 1)) return;

            var text = loc.SourceTree.GetText(ct);
            var site = new CallSite
            {
                File = file,
                Line = line + 1,
                Column = span.StartLinePosition.Character + 1,
                Code = LineText(loc, line, ct),
                Kind = kind,
                Detail = detail,
            };
            var from = Math.Max(0, line - 2);
            var to = Math.Min(text.Lines.Count - 1, line + 1);
            site.ContextStart = from + 1;
            for (var i = from; i <= to; i++)
            {
                var l = text.Lines[i].ToString().TrimEnd();
                site.Context.Add(l.Length > 200 ? l.Substring(0, 197) + "..." : l);
            }
            edge.Sites.Add(site);
        }

        private static string LineText(Location loc, int line, CancellationToken ct)
        {
            var text = loc.SourceTree.GetText(ct);
            var code = line < text.Lines.Count ? text.Lines[line].ToString().Trim() : "";
            return code.Length > 160 ? code.Substring(0, 157) + "..." : code;
        }

        /// <summary>The edge takes the strongest kind among its sites: a real call beats an event, an event beats a delegate.</summary>
        private static void FinishEdge(FlowEdge edge)
        {
            int Rank(string kind) => kind == "call" ? 0 : kind == "event" ? 1 : 2;
            var ordered = edge.Sites.OrderBy(s => Rank(s.Kind)).ToList();
            edge.Sites.Clear();
            edge.Sites.AddRange(ordered);

            var first = ordered.FirstOrDefault();
            edge.Kind = first?.Kind ?? "call";
            edge.Detail = first?.Detail;
        }

        private void ClassifyStartingPoints()
        {
            foreach (var node in _graph.Nodes.Values)
            {
                if (_hasCallers.Contains(node.Id) || node.Cut) continue;
                EntryPointClassifier.Classify(_symbols[node.Id], out node.EntryKind, out node.EntryDetail);

                // A handler whose event is raised outside this solution (Button.Click, Timer.Elapsed...).
                if (node.Subscriptions.Count > 0 &&
                    (node.EntryKind == null || node.EntryKind.StartsWith("Event handler", StringComparison.Ordinal) ||
                     node.EntryKind.StartsWith("No callers", StringComparison.Ordinal)))
                {
                    var events = node.Subscriptions.Select(s => s.Event).Distinct().ToList();
                    node.EntryKind = "Event handler (runs when the event fires)";
                    node.EntryDetail = string.Join(", ", events) + (events.Count == 1 ? " event" : " events");
                }
            }
        }

        /// <summary>
        /// For every call made through an interface / base type, check which class the DI container
        /// actually creates for it. Only runs when there is such a call.
        /// </summary>
        private async Task CheckDependencyInjectionAsync(CancellationToken ct)
        {
            if (!_graph.Edges.Any(e => e.ViaType != null)) return;

            List<DiRegistration> registrations;
            try
            {
                registrations = await DiRegistrationScanner.ScanAsync(_solution, Relative, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return;   // the DI check is a bonus: never fail the report because of it
            }

            _graph.DiScanned = true;
            _graph.DiRegistrationCount = registrations.Count;
            if (registrations.Count == 0) return;   // probably another container (Autofac...) or none: say nothing

            var index = new DiIndex(registrations);
            foreach (var edge in _graph.Edges.Where(e => e.ViaType != null))
            {
                var declaringType = _symbols[edge.To].ContainingType;
                if (declaringType != null) edge.Di = index.Check(edge.ViaType, declaringType);
            }

            foreach (var node in _graph.Nodes.Values)
            {
                var type = _symbols[node.Id].ContainingType;
                if (type != null) node.DiRegistrations.AddRange(index.ForImplementation(type));
            }
        }

        private string Relative(string path)
        {
            if (string.IsNullOrEmpty(path) || _solutionDir == null) return path;
            var dir = _solutionDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ? path.Substring(dir.Length) : path;
        }

        private static string KeyOf(ISymbol symbol)
            => symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CallFlowViewer
{
    internal sealed class CallGraphOptions
    {
        public int MaxDepth { get; set; } = 25;
        public int MaxNodes { get; set; } = 400;
        public static CallGraphOptions Default => new CallGraphOptions();
    }

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
    }

    internal sealed class CallSite
    {
        public string File;
        public int Line;
        public string Code;
    }

    internal sealed class FlowEdge
    {
        public string From;        // caller
        public string To;          // callee
        public string Via;         // "interface IOrderService" / "base Repository.Save" / null
        public bool InsideLambda;
        public readonly List<CallSite> Sites = new List<CallSite>();
    }

    internal sealed class CallGraph
    {
        public string TargetId;
        public string SolutionName;
        public DateTime GeneratedAt;
        public bool Truncated;
        public CallGraphOptions Options;
        public readonly Dictionary<string, FlowNode> Nodes = new Dictionary<string, FlowNode>();
        public readonly List<FlowEdge> Edges = new List<FlowEdge>();
    }

    /// <summary>
    /// Walks callers backwards from the target (breadth first), following interface and
    /// override relationships so that calls through IService / base classes are not lost.
    /// </summary>
    internal sealed class CallGraphBuilder
    {
        private readonly Solution _solution;
        private readonly CallGraphOptions _options;
        private readonly string _solutionDir;

        private readonly CallGraph _graph = new CallGraph();
        private readonly Dictionary<string, ISymbol> _symbols = new Dictionary<string, ISymbol>();
        private readonly Dictionary<(string, string), FlowEdge> _edgeIndex = new Dictionary<(string, string), FlowEdge>();
        private readonly HashSet<string> _hasCallers = new HashSet<string>();

        public CallGraphBuilder(Solution solution, CallGraphOptions options)
        {
            _solution = solution;
            _options = options;
            _solutionDir = string.IsNullOrEmpty(solution.FilePath) ? null : Path.GetDirectoryName(solution.FilePath);
        }

        public async Task<CallGraph> BuildAsync(ISymbol target, CancellationToken ct)
        {
            target = Normalize(target, out _);
            _graph.Options = _options;
            _graph.GeneratedAt = DateTime.Now;
            _graph.SolutionName = string.IsNullOrEmpty(_solution.FilePath) ? null : Path.GetFileName(_solution.FilePath);
            _graph.TargetId = AddNode(target).Id;

            var queue = new Queue<(ISymbol Symbol, int Depth)>();
            var expanded = new HashSet<string>();
            queue.Enqueue((target, 0));

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (symbol, depth) = queue.Dequeue();
                var id = KeyOf(symbol);
                if (!expanded.Add(id)) continue;

                if (depth >= _options.MaxDepth || _graph.Nodes.Count >= _options.MaxNodes)
                {
                    _graph.Nodes[id].Cut = true;
                    _graph.Truncated = true;
                    continue;
                }

                foreach (var (callee, via) in await GetCallTargetsAsync(symbol, ct).ConfigureAwait(false))
                {
                    var callers = await SymbolFinder.FindCallersAsync(callee, _solution, ct).ConfigureAwait(false);
                    foreach (var callerInfo in callers)
                    {
                        var caller = Normalize(callerInfo.CallingSymbol, out var insideLambda);
                        if (caller == null || !caller.Locations.Any(l => l.IsInSource)) continue;

                        var callerNode = AddNode(caller);
                        if (callerNode.Id != id) _hasCallers.Add(id); // plain recursion doesn't count as "has callers"

                        var edge = GetOrAddEdge(callerNode.Id, id, via);
                        edge.InsideLambda |= insideLambda;
                        foreach (var loc in callerInfo.Locations.Where(l => l.IsInSource))
                            AddSite(edge, loc, ct);

                        if (!expanded.Contains(callerNode.Id))
                            queue.Enqueue((caller, depth + 1));
                    }
                }
            }

            foreach (var node in _graph.Nodes.Values)
            {
                if (_hasCallers.Contains(node.Id) || node.Cut) continue;
                EntryPointClassifier.Classify(_symbols[node.Id], out node.EntryKind, out node.EntryDetail);
            }

            return _graph;
        }

        /// <summary>
        /// The symbol itself plus every interface member it implements and every base member it overrides:
        /// a call to IOrderService.PlaceOrder or BaseService.PlaceOrder may end up here at runtime.
        /// </summary>
        private async Task<List<(ISymbol, string)>> GetCallTargetsAsync(ISymbol symbol, CancellationToken ct)
        {
            var result = new List<(ISymbol, string)> { (symbol, null) };
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { symbol };

            var interfaceMembers = await SymbolFinder.FindImplementedInterfaceMembersAsync(symbol, _solution, cancellationToken: ct).ConfigureAwait(false);
            foreach (var m in interfaceMembers)
                if (seen.Add(m.OriginalDefinition))
                    result.Add((m.OriginalDefinition, "interface " + m.ContainingType.Name));

            for (var b = Overridden(symbol); b != null; b = Overridden(b))
                if (seen.Add(b.OriginalDefinition))
                    result.Add((b.OriginalDefinition, "base " + b.ContainingType.Name + "." + b.Name));

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
            _graph.Nodes.Add(id, node);
            _symbols[id] = symbol;
            return node;
        }

        private FlowEdge GetOrAddEdge(string from, string to, string via)
        {
            if (_edgeIndex.TryGetValue((from, to), out var edge))
            {
                if (edge.Via != null && via == null) edge.Via = null; // a direct call wins
                return edge;
            }
            edge = new FlowEdge { From = from, To = to, Via = via };
            _edgeIndex.Add((from, to), edge);
            _graph.Edges.Add(edge);
            return edge;
        }

        private void AddSite(FlowEdge edge, Location loc, CancellationToken ct)
        {
            var span = loc.GetLineSpan();
            var line = span.StartLinePosition.Line;
            var file = Relative(span.Path);
            if (edge.Sites.Any(s => s.File == file && s.Line == line + 1)) return;

            var text = loc.SourceTree.GetText(ct);
            var code = line < text.Lines.Count ? text.Lines[line].ToString().Trim() : "";
            if (code.Length > 160) code = code.Substring(0, 157) + "...";
            edge.Sites.Add(new CallSite { File = file, Line = line + 1, Code = code });
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

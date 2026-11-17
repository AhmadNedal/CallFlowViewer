using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CallFlowViewer.Tests
{
    /// <summary>Builds an in-memory solution from C# source strings (no MSBuild, no files).</summary>
    internal sealed class TestSolution
    {
        // Only the .NET runtime itself: the test host also loads xunit, and a reference to xunit
        // would make every test project look like a test project to CallFlowSettings.IsTestProject.
        private static readonly Lazy<MetadataReference[]> Runtime = new Lazy<MetadataReference[]>(() =>
        {
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(Path.GetDirectoryName(p), runtimeDir, StringComparison.OrdinalIgnoreCase))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToArray();
        });

        public static MetadataReference DependencyInjection =>
            MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location);

        private readonly AdhocWorkspace _workspace = new AdhocWorkspace();

        public Solution Solution { get; private set; }

        public TestSolution()
        {
            Solution = _workspace.CurrentSolution;
        }

        public static TestSolution Single(params string[] sources)
        {
            var solution = new TestSolution();
            solution.AddProject("App", sources);
            return solution;
        }

        public ProjectId AddProject(string name, string[] sources, ProjectId[] references = null, MetadataReference[] extra = null)
        {
            var id = ProjectId.CreateNewId(name);
            Solution = Solution
                .AddProject(id, name, name, LanguageNames.CSharp)
                .WithProjectCompilationOptions(id, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithProjectParseOptions(id, new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse))
                .AddMetadataReferences(id, Runtime.Value.Concat(extra ?? Array.Empty<MetadataReference>()));

            foreach (var reference in references ?? Array.Empty<ProjectId>())
                Solution = Solution.AddProjectReference(id, new ProjectReference(reference));

            for (var i = 0; i < sources.Length; i++)
                Solution = Solution.AddDocument(DocumentId.CreateNewId(id), $"File{i}.cs", sources[i], filePath: $"/src/{name}/File{i}.cs");

            return id;
        }

        /// <summary>Fails the test early when the test source itself does not compile.</summary>
        public async Task AssertCompilesAsync()
        {
            foreach (var project in Solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                Assert.True(errors.Count == 0, project.Name + ": " + string.Join(Environment.NewLine, errors));
            }
        }

        /// <summary>A member declared in source: FindAsync("App.OrderService", "PlaceOrder").</summary>
        public async Task<ISymbol> FindAsync(string typeName, string memberName)
        {
            foreach (var project in Solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                var type = compilation.GetTypeByMetadataName(typeName);
                if (type == null || !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)) continue;
                var member = type.GetMembers(memberName).FirstOrDefault();
                if (member != null) return member;
            }
            throw new InvalidOperationException($"{typeName}.{memberName} not found in the test solution.");
        }

        public async Task<CallGraph> CallersAsync(string typeName, string memberName, CallFlowSettings settings = null)
        {
            await AssertCompilesAsync();
            var target = await FindAsync(typeName, memberName);
            return await new CallGraphBuilder(Solution, settings ?? CallFlowSettings.Default).BuildAsync(target, default);
        }

        public async Task<CallGraph> CalleesAsync(string typeName, string memberName, CallFlowSettings settings = null)
        {
            await AssertCompilesAsync();
            var root = await FindAsync(typeName, memberName);
            return await new CallGraphBuilder(Solution, settings ?? CallFlowSettings.Default).BuildCalleesAsync(root, default);
        }
    }

    internal static class GraphAssert
    {
        /// <summary>The node whose display name is "Type.Member(...)" (or exactly "Type.Member" for properties).</summary>
        public static FlowNode Node(CallGraph graph, string typeDotMember)
        {
            var matches = graph.Nodes.Values.Where(n => n.Name == typeDotMember || n.Name.StartsWith(typeDotMember + "(", StringComparison.Ordinal)).ToList();
            Assert.True(matches.Count == 1, $"Expected one node '{typeDotMember}', found {matches.Count}. Nodes: {Names(graph)}");
            return matches[0];
        }

        public static bool HasNode(CallGraph graph, string typeDotMember)
            => graph.Nodes.Values.Any(n => n.Name == typeDotMember || n.Name.StartsWith(typeDotMember + "(", StringComparison.Ordinal));

        public static FlowEdge Edge(CallGraph graph, string from, string to)
        {
            var a = Node(graph, from).Id;
            var b = Node(graph, to).Id;
            var edge = graph.Edges.FirstOrDefault(e => e.From == a && e.To == b);
            Assert.True(edge != null, $"No edge {from} -> {to}. Edges: " +
                string.Join(", ", graph.Edges.Select(e => graph.Nodes[e.From].Name + " -> " + graph.Nodes[e.To].Name)));
            return edge;
        }

        public static string Names(CallGraph graph) => string.Join(", ", graph.Nodes.Values.Select(n => n.Name));
    }
}

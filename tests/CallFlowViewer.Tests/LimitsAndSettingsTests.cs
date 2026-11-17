using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Xunit;
using static CallFlowViewer.Tests.GraphAssert;

namespace CallFlowViewer.Tests
{
    public class LimitsAndSettingsTests
    {
        private static string ManyCallers(int count)
        {
            var sb = new StringBuilder("namespace App {\n public static class Store { public static void Save() { } }\n");
            for (var i = 0; i < count; i++) sb.Append($" public class Caller{i} {{ public void Run() {{ Store.Save(); }} }}\n");
            return sb.Append("}").ToString();
        }

        [Fact]
        public async Task The_method_limit_stops_the_analysis()
        {
            var solution = TestSolution.Single(ManyCallers(10));
            var graph = await solution.CallersAsync("App.Store", "Save", new CallFlowSettings { MaxNodes = 4 });

            Assert.True(graph.Truncated);
            Assert.True(graph.Nodes.Count <= 4, AssertNames(graph));
        }

        [Fact]
        public async Task Excluded_namespaces_are_skipped_and_counted()
        {
            var solution = TestSolution.Single(@"
namespace App { public static class Store { public static void Save() { } } public class Live { public void Run() { Store.Save(); } } }
namespace App.Legacy { public class Old { public void Run() { App.Store.Save(); } } }");
            var settings = new CallFlowSettings();
            settings.ExcludeNamespaces.Add("App.Legacy");

            var graph = await solution.CallersAsync("App.Store", "Save", settings);

            Assert.True(HasNode(graph, "Live.Run"));
            Assert.False(HasNode(graph, "Old.Run"));
            Assert.Equal(1, graph.SkippedByNamespace);
        }

        [Fact]
        public async Task Test_projects_can_be_left_out_of_the_search()
        {
            var solution = new TestSolution();
            var core = solution.AddProject("Core", new[] { "namespace Core { public static class Store { public static void Save() { } } }" });
            solution.AddProject("Core.Tests", new[] { "namespace Core.Tests { public class StoreTests { public void Saves() { Core.Store.Save(); } } }" }, new[] { core });

            var included = await solution.CallersAsync("Core.Store", "Save");
            Assert.True(HasNode(included, "StoreTests.Saves"));

            var graph = await solution.CallersAsync("Core.Store", "Save", new CallFlowSettings { ExcludeTests = true });
            Assert.False(HasNode(graph, "StoreTests.Saves"));
            Assert.Contains("Core.Tests", graph.ExcludedProjects);
        }

        [Theory]
        [InlineData("MyApp.Legacy", "MyApp.Legacy", true, true)]
        [InlineData("MyApp.Legacy", "MyApp.Legacy.Orders", true, true)]      // child namespace
        [InlineData("MyApp.Legacy", "MyApp.LegacyTools", true, false)]       // not a child
        [InlineData("MyApp.Legacy", "MyApp.Legacy.Orders", false, false)]    // projects: exact names only
        [InlineData("*.Generated", "MyApp.Orders.Generated", true, true)]
        [InlineData("Legacy.*", "Legacy.Tools", false, true)]
        [InlineData("legacy.*", "Legacy.Tools", false, true)]                // case-insensitive
        public void Patterns_match_as_documented(string pattern, string value, bool prefix, bool expected)
        {
            Assert.Equal(expected, CallFlowSettings.Matches(pattern, value, prefix));
        }

        [Fact]
        public async Task Settings_are_read_from_editorconfig()
        {
            var dir = Path.Combine(Path.GetTempPath(), "CallFlowViewerTests_" + Guid.NewGuid().ToString("N"));
            var src = Path.Combine(dir, "src");
            Directory.CreateDirectory(src);
            try
            {
                File.WriteAllText(Path.Combine(dir, ".editorconfig"),
                    "root = true\n\n[*.cs]\ncallflow.max_methods = 1000\ncallflow.max_depth = 40\n" +
                    "callflow.exclude_tests = true\ncallflow.exclude_namespaces = MyApp.Migrations, *.Generated\n" +
                    "[*.json]\ncallflow.max_depth = 2\n");

                var workspace = new AdhocWorkspace();
                var projectId = ProjectId.CreateNewId();
                var documentId = DocumentId.CreateNewId(projectId);
                var solution = workspace.CurrentSolution
                    .AddProject(projectId, "App", "App", LanguageNames.CSharp)
                    .AddDocument(documentId, "A.cs", "class A { }", filePath: Path.Combine(src, "A.cs"));

                var settings = await CallFlowSettings.LoadAsync(solution.GetDocument(documentId), default);

                Assert.Equal(1000, settings.MaxNodes);
                Assert.Equal(40, settings.MaxDepth);   // the [*.json] section does not apply
                Assert.True(settings.ExcludeTests);
                Assert.Equal(new[] { "MyApp.Migrations", "*.Generated" }, settings.ExcludeNamespaces.ToArray());
                Assert.NotNull(settings.Source);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public async Task Defaults_are_used_without_editorconfig()
        {
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var documentId = DocumentId.CreateNewId(projectId);
            var solution = workspace.CurrentSolution
                .AddProject(projectId, "App", "App", LanguageNames.CSharp)
                .AddDocument(documentId, "A.cs", "class A { }");

            var settings = await CallFlowSettings.LoadAsync(solution.GetDocument(documentId), default);

            Assert.Equal(400, settings.MaxNodes);
            Assert.Equal(25, settings.MaxDepth);
            Assert.False(settings.ExcludeTests);
            Assert.Null(settings.Source);
        }

        private static string AssertNames(CallGraph graph) => Names(graph);
    }
}

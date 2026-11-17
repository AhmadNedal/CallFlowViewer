using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace CallFlowViewer.Tests
{
    public class ReportTests
    {
        [Fact]
        public async Task The_report_embeds_valid_json()
        {
            var solution = TestSolution.Single(@"
namespace App
{
    public class A { public void Run() { var tag = ""</script>""; B.Do(); } }
    public static class B { public static void Do() { } }
}");
            var graph = await solution.CallersAsync("App.B", "Do");

            var html = HtmlReport.Render(graph);

            Assert.DoesNotContain("/*__CALLFLOW_DATA__*/", html);
            const string marker = "const DATA = ";
            var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = html.IndexOf("const FWD", start, StringComparison.Ordinal);
            var json = html.Substring(start, end - start).TrimEnd('\r', '\n', ' ', ';');

            using var doc = JsonDocument.Parse(json);
            Assert.Equal("callers", doc.RootElement.GetProperty("direction").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("nodes").GetArrayLength());
            Assert.Equal(1, doc.RootElement.GetProperty("edges").GetArrayLength());

            // A.Run's source (shown in the report) contains "</script>": it must not close the script block.
            Assert.DoesNotContain("</script>", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Old_reports_are_cleaned_up()
        {
            var dir = Path.Combine(Path.GetTempPath(), "CallFlowViewerTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var now = DateTime.UtcNow;
                for (var i = 0; i < 40; i++)
                {
                    var file = Path.Combine(dir, $"report{i}.html");
                    File.WriteAllText(file, "x");
                    File.SetLastWriteTimeUtc(file, now.AddMinutes(-i));
                }
                var old = Path.Combine(dir, "old.html");
                File.WriteAllText(old, "x");
                File.SetLastWriteTimeUtc(old, now.AddDays(-30));
                File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a report");

                ShowCallFlowCodeAction.DeleteOldReports(dir);

                var left = Directory.GetFiles(dir, "*.html");
                Assert.Equal(29, left.Length);                                   // room for the next report
                Assert.False(File.Exists(old));
                Assert.True(File.Exists(Path.Combine(dir, "report0.html")));     // newest kept
                Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));        // only .html files are touched
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

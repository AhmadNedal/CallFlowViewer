using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;

namespace CallFlowViewer
{
    /// <summary>
    /// A code action that does not change the code: when invoked it analyses the callers,
    /// writes an HTML report to %TEMP%\CallFlowViewer and opens it in the default browser.
    /// </summary>
    internal sealed class ShowCallFlowCodeAction : CodeAction
    {
        private readonly Solution _solution;
        private readonly ISymbol _target;

        public ShowCallFlowCodeAction(Solution solution, ISymbol target)
        {
            _solution = solution;
            _target = target;
        }

        public override string Title => "Show call flow to this method";

        public override string EquivalenceKey => nameof(ShowCallFlowCodeAction);

        // Visual Studio computes the preview when the user merely hovers the item.
        // Returning nothing here guarantees the browser opens only on click / Enter.
        protected override Task<IEnumerable<CodeActionOperation>> ComputePreviewOperationsAsync(CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<CodeActionOperation>());

        protected override async Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(CancellationToken cancellationToken)
        {
            string html;
            try
            {
                var graph = await new CallGraphBuilder(_solution, CallGraphOptions.Default)
                    .BuildAsync(_target, cancellationToken)
                    .ConfigureAwait(false);
                html = HtmlReport.Render(graph);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                html = HtmlReport.RenderError(_target, ex);
            }

            var path = WriteReport(html);
            OpenInBrowser(path);

            // No code changes.
            return Enumerable.Empty<CodeActionOperation>();
        }

        private string WriteReport(string html)
        {
            var dir = Path.Combine(Path.GetTempPath(), "CallFlowViewer");
            Directory.CreateDirectory(dir);

            var safeName = new string(_target.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            var file = Path.Combine(dir, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            File.WriteAllText(file, html, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return file;
        }

        private static void OpenInBrowser(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // Fallback for machines without an .html file association.
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
        }
    }
}

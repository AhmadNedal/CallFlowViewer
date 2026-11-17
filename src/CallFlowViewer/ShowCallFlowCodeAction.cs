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
    internal enum FlowDirection
    {
        /// <summary>How the code reaches the method (who calls it).</summary>
        Callers,

        /// <summary>What the method calls.</summary>
        Callees,
    }

    /// <summary>
    /// A code action that does not change the code: when invoked it analyses the flow,
    /// writes an HTML report to %TEMP%\CallFlowViewer and opens it in the default browser.
    /// </summary>
    internal sealed class ShowCallFlowCodeAction : CodeAction
    {
        // Old reports are removed when a new one is written.
        private const int KeepReports = 30;
        private static readonly TimeSpan MaxReportAge = TimeSpan.FromDays(7);

        private readonly Document _document;
        private readonly ISymbol _target;
        private readonly FlowDirection _direction;

        public ShowCallFlowCodeAction(Document document, ISymbol target, FlowDirection direction)
        {
            _document = document;
            _target = target;
            _direction = direction;
        }

        public override string Title => _direction == FlowDirection.Callers
            ? "Show call flow to this method"
            : "Show what this method calls";

        public override string EquivalenceKey => nameof(ShowCallFlowCodeAction) + "." + _direction;

        // Visual Studio computes the preview when the user merely hovers the item.
        // Returning nothing here guarantees the browser opens only on click / Enter.
        protected override Task<IEnumerable<CodeActionOperation>> ComputePreviewOperationsAsync(CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<CodeActionOperation>());

        protected override async Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(CancellationToken cancellationToken)
        {
            string html;
            try
            {
                // Settings come from .editorconfig (callflow.* keys), read when the action runs.
                var settings = await CallFlowSettings.LoadAsync(_document, cancellationToken).ConfigureAwait(false);
                var builder = new CallGraphBuilder(_document.Project.Solution, settings);
                var graph = _direction == FlowDirection.Callers
                    ? await builder.BuildAsync(_target, cancellationToken).ConfigureAwait(false)
                    : await builder.BuildCalleesAsync(_target, cancellationToken).ConfigureAwait(false);
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
            DeleteOldReports(dir);

            var safeName = new string(_target.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            var prefix = _direction == FlowDirection.Callers ? "" : "calls_";
            var file = Path.Combine(dir, $"{prefix}{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            File.WriteAllText(file, html, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return file;
        }

        /// <summary>Keeps the folder small: drops reports older than a week and all but the newest ones.</summary>
        internal static void DeleteOldReports(string dir)
        {
            try
            {
                var reports = new DirectoryInfo(dir).GetFiles("*.html")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                var now = DateTime.UtcNow;
                for (var i = 0; i < reports.Count; i++)
                {
                    // KeepReports - 1: room for the report about to be written.
                    if (i >= KeepReports - 1 || now - reports[i].LastWriteTimeUtc > MaxReportAge)
                    {
                        try { reports[i].Delete(); }
                        catch { /* open in a browser or locked: try again next time */ }
                    }
                }
            }
            catch
            {
                // Cleaning up is a courtesy: never block the report because of it.
            }
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

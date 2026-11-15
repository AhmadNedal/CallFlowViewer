using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Microsoft.CodeAnalysis;

namespace CallFlowViewer
{
    /// <summary>Serialises the graph to JSON and injects it into the embedded HTML template.</summary>
    internal static class HtmlReport
    {
        private const string Placeholder = "/*__CALLFLOW_DATA__*/null";

        public static string Render(CallGraph graph)
        {
            var template = LoadTemplate();
            return template.Replace(Placeholder, ToJson(graph));
        }

        public static string RenderError(ISymbol target, Exception ex)
        {
            return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Call flow – error</title></head>" +
                   "<body style=\"font-family:Segoe UI,sans-serif;padding:32px\"><h2>Could not analyse " +
                   WebUtility.HtmlEncode(SymbolText.Display(target)) + "</h2><pre style=\"white-space:pre-wrap\">" +
                   WebUtility.HtmlEncode(ex.ToString()) + "</pre></body></html>";
        }

        private static string LoadTemplate()
        {
            using (var stream = typeof(HtmlReport).Assembly.GetManifestResourceStream("CallFlowViewer.template.html"))
            using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("Report template missing from assembly.")))
                return reader.ReadToEnd();
        }

        internal static string ToJson(CallGraph g)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            Prop(sb, "target", g.TargetId).Append(',');
            Prop(sb, "solution", g.SolutionName).Append(',');
            Prop(sb, "generatedAt", g.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"truncated\":").Append(g.Truncated ? "true" : "false").Append(',');
            sb.Append("\"maxDepth\":").Append(g.Options.MaxDepth).Append(',');
            sb.Append("\"maxNodes\":").Append(g.Options.MaxNodes).Append(',');

            sb.Append("\"nodes\":[");
            sb.Append(string.Join(",", g.Nodes.Values.Select(n =>
            {
                var s = new StringBuilder("{");
                Prop(s, "id", n.Id).Append(',');
                Prop(s, "name", n.Name).Append(',');
                Prop(s, "ns", n.Namespace).Append(',');
                Prop(s, "project", n.Project).Append(',');
                Prop(s, "file", n.File).Append(',');
                s.Append("\"line\":").Append(n.Line).Append(',');
                Prop(s, "entryKind", n.EntryKind).Append(',');
                Prop(s, "entryDetail", n.EntryDetail).Append(',');
                s.Append("\"cut\":").Append(n.Cut ? "true" : "false");
                return s.Append('}').ToString();
            })));
            sb.Append("],");

            sb.Append("\"edges\":[");
            sb.Append(string.Join(",", g.Edges.Select(e =>
            {
                var s = new StringBuilder("{");
                Prop(s, "from", e.From).Append(',');
                Prop(s, "to", e.To).Append(',');
                Prop(s, "via", e.Via).Append(',');
                s.Append("\"lambda\":").Append(e.InsideLambda ? "true" : "false").Append(',');
                s.Append("\"sites\":[");
                s.Append(string.Join(",", e.Sites.Select(site =>
                {
                    var t = new StringBuilder("{");
                    Prop(t, "file", site.File).Append(',');
                    t.Append("\"line\":").Append(site.Line).Append(',');
                    Prop(t, "code", site.Code);
                    return t.Append('}').ToString();
                })));
                s.Append("]}");
                return s.ToString();
            })));
            sb.Append("]}");
            return sb.ToString();
        }

        private static StringBuilder Prop(StringBuilder sb, string name, string value)
        {
            sb.Append('"').Append(name).Append("\":");
            if (value == null) return sb.Append("null");
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    // keep "</script>" and HTML comments from breaking out of the <script> block
                    case '<': sb.Append("\\u003c"); break;
                    case '>': sb.Append("\\u003e"); break;
                    case '&': sb.Append("\\u0026"); break;
                    case (char)0x2028: sb.Append("\\u2028"); break;
                    case (char)0x2029: sb.Append("\\u2029"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"');
        }
    }
}

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
            return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Call flow - error</title></head>" +
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
            Prop(sb, "direction", g.Direction).Append(',');
            Prop(sb, "target", g.TargetId).Append(',');
            Prop(sb, "solution", g.SolutionName).Append(',');
            Prop(sb, "generatedAt", g.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"truncated\":").Append(g.Truncated ? "true" : "false").Append(',');
            sb.Append("\"maxDepth\":").Append(g.Settings.MaxDepth).Append(',');
            sb.Append("\"maxNodes\":").Append(g.Settings.MaxNodes).Append(',');
            sb.Append("\"settings\":{");
            Prop(sb, "source", g.SettingsSource).Append(',');
            sb.Append("\"excludeTests\":").Append(g.Settings.ExcludeTests ? "true" : "false").Append(',');
            StringArray(sb, "excludeProjects", g.Settings.ExcludeProjects).Append(',');
            StringArray(sb, "excludeNamespaces", g.Settings.ExcludeNamespaces);
            sb.Append("},");
            StringArray(sb, "excludedProjects", g.ExcludedProjects).Append(',');
            sb.Append("\"skippedByNamespace\":").Append(g.SkippedByNamespace).Append(',');
            sb.Append("\"diScanned\":").Append(g.DiScanned ? "true" : "false").Append(',');
            sb.Append("\"diRegistrations\":").Append(g.DiRegistrationCount).Append(',');

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
                s.Append("\"cut\":").Append(n.Cut ? "true" : "false").Append(',');
                Prop(s, "kind", n.Kind).Append(',');
                Prop(s, "signature", n.Signature).Append(',');
                Prop(s, "containingType", n.ContainingType).Append(',');
                Prop(s, "access", n.Accessibility).Append(',');
                Prop(s, "returns", n.ReturnType).Append(',');
                Prop(s, "summary", n.Summary).Append(',');
                s.Append("\"sourceStart\":").Append(n.SourceStart).Append(',');
                s.Append("\"sourceTruncated\":").Append(n.SourceTruncated ? "true" : "false").Append(',');
                StringArray(s, "source", n.Source).Append(',');
                StringArray(s, "modifiers", n.Modifiers).Append(',');
                StringArray(s, "attributes", n.Attributes).Append(',');
                s.Append("\"parameters\":[");
                s.Append(string.Join(",", n.Parameters.Select(p =>
                {
                    var t = new StringBuilder("{");
                    Prop(t, "name", p.Key).Append(',');
                    Prop(t, "type", p.Value);
                    return t.Append('}').ToString();
                })));
                s.Append("],");
                s.Append("\"subscriptions\":[");
                s.Append(string.Join(",", n.Subscriptions.Select(x =>
                {
                    var t = new StringBuilder("{");
                    Prop(t, "event", x.Event).Append(',');
                    Prop(t, "by", x.By).Append(',');
                    Prop(t, "file", x.File).Append(',');
                    t.Append("\"line\":").Append(x.Line).Append(',');
                    Prop(t, "code", x.Code);
                    return t.Append('}').ToString();
                })));
                s.Append("],");
                StringArray(s, "external", n.External).Append(',');
                s.Append("\"diRegs\":");
                Registrations(s, n.DiRegistrations);
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
                Prop(s, "kind", e.Kind).Append(',');
                Prop(s, "detail", e.Detail).Append(',');
                s.Append("\"lambda\":").Append(e.InsideLambda ? "true" : "false").Append(',');
                if (e.Di != null)
                {
                    s.Append("\"di\":{");
                    Prop(s, "status", e.Di.Status).Append(',');
                    Prop(s, "service", e.Di.Service).Append(',');
                    s.Append("\"regs\":");
                    Registrations(s, e.Di.Registrations);
                    s.Append("},");
                }
                s.Append("\"sites\":[");
                s.Append(string.Join(",", e.Sites.Select(site =>
                {
                    var t = new StringBuilder("{");
                    Prop(t, "file", site.File).Append(',');
                    t.Append("\"line\":").Append(site.Line).Append(',');
                    t.Append("\"column\":").Append(site.Column).Append(',');
                    t.Append("\"contextStart\":").Append(site.ContextStart).Append(',');
                    StringArray(t, "context", site.Context).Append(',');
                    Prop(t, "kind", site.Kind).Append(',');
                    Prop(t, "detail", site.Detail).Append(',');
                    Prop(t, "code", site.Code);
                    return t.Append('}').ToString();
                })));
                s.Append("]}");
                return s.ToString();
            })));
            sb.Append("]}");
            return sb.ToString();
        }

        private static void Registrations(StringBuilder sb, System.Collections.Generic.IEnumerable<DiRegistration> regs)
        {
            sb.Append('[');
            sb.Append(string.Join(",", regs.Select(r =>
            {
                var t = new StringBuilder("{");
                Prop(t, "service", r.Service?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)).Append(',');
                Prop(t, "impl", r.Implementation?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)).Append(',');
                Prop(t, "lifetime", r.Lifetime).Append(',');
                Prop(t, "method", r.Method).Append(',');
                Prop(t, "file", r.File).Append(',');
                t.Append("\"line\":").Append(r.Line).Append(',');
                Prop(t, "project", r.Project);
                return t.Append('}').ToString();
            })));
            sb.Append(']');
        }

        private static StringBuilder StringArray(StringBuilder sb, string name, System.Collections.Generic.IEnumerable<string> values)
        {
            sb.Append('"').Append(name).Append("\":[");
            var first = true;
            foreach (var v in values)
            {
                if (!first) sb.Append(',');
                first = false;
                Str(sb, v);
            }
            return sb.Append(']');
        }

        private static StringBuilder Prop(StringBuilder sb, string name, string value)
        {
            sb.Append('"').Append(name).Append("\":");
            return Str(sb, value);
        }

        private static StringBuilder Str(StringBuilder sb, string value)
        {
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

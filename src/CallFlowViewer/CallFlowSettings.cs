using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace CallFlowViewer
{
    /// <summary>
    /// Settings read from .editorconfig. Example:
    /// <code>
    /// [*.cs]
    /// callflow.max_methods = 1000
    /// callflow.max_depth = 40
    /// callflow.exclude_tests = true
    /// callflow.exclude_projects = Legacy.*, Tools
    /// callflow.exclude_namespaces = MyApp.Migrations, *.Generated
    /// </code>
    /// </summary>
    internal sealed class CallFlowSettings
    {
        public const string KeyMaxMethods = "callflow.max_methods";
        public const string KeyMaxDepth = "callflow.max_depth";
        public const string KeyExcludeTests = "callflow.exclude_tests";
        public const string KeyExcludeProjects = "callflow.exclude_projects";
        public const string KeyExcludeNamespaces = "callflow.exclude_namespaces";

        private static readonly string[] AllKeys = { KeyMaxMethods, KeyMaxDepth, KeyExcludeTests, KeyExcludeProjects, KeyExcludeNamespaces };

        // Hard caps so that a typo in .editorconfig cannot freeze Visual Studio.
        private const int MaxMethodsCap = 10000;
        private const int MaxDepthCap = 500;

        public int MaxDepth = 25;
        public int MaxNodes = 400;
        public bool ExcludeTests;
        public readonly List<string> ExcludeProjects = new List<string>();
        public readonly List<string> ExcludeNamespaces = new List<string>();

        /// <summary>Where the values came from: null for defaults, ".editorconfig", or the path of the file.</summary>
        public string Source;

        public static CallFlowSettings Default => new CallFlowSettings();

        public static async Task<CallFlowSettings> LoadAsync(Document document, CancellationToken ct)
        {
            var settings = new CallFlowSettings();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1) The options Visual Studio already parsed from .editorconfig / .globalconfig.
            try
            {
                var tree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
                var provider = document.Project.AnalyzerOptions?.AnalyzerConfigOptionsProvider;
                if (tree != null && provider != null)
                {
                    var options = provider.GetOptions(tree);
                    foreach (var key in AllKeys)
                    {
                        if (options.TryGetValue(key, out var value) || provider.GlobalOptions.TryGetValue(key, out value))
                            values[key] = value;
                    }
                    if (values.Count > 0) settings.Source = ".editorconfig";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fall back to reading the files ourselves.
            }

            // 2) Fallback: read the .editorconfig files next to the document and above it.
            if (values.Count == 0)
            {
                try
                {
                    settings.Source = ReadEditorConfigFiles(document.FilePath, values);
                }
                catch
                {
                    // Unreadable files: keep the defaults.
                }
            }

            settings.Apply(values);
            return settings;
        }

        private void Apply(Dictionary<string, string> values)
        {
            if (values.TryGetValue(KeyMaxMethods, out var s) && int.TryParse(s.Trim(), out var n) && n > 0)
                MaxNodes = Math.Min(n, MaxMethodsCap);
            if (values.TryGetValue(KeyMaxDepth, out s) && int.TryParse(s.Trim(), out n) && n > 0)
                MaxDepth = Math.Min(n, MaxDepthCap);
            if (values.TryGetValue(KeyExcludeTests, out s))
                ExcludeTests = s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            if (values.TryGetValue(KeyExcludeProjects, out s))
                ExcludeProjects.AddRange(SplitList(s));
            if (values.TryGetValue(KeyExcludeNamespaces, out s))
                ExcludeNamespaces.AddRange(SplitList(s));
        }

        private static IEnumerable<string> SplitList(string value)
            => value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0);

        // ------------------------------------------------------------------ matching

        public bool IsProjectExcluded(Project project)
        {
            if (ExcludeTests && IsTestProject(project)) return true;
            return ExcludeProjects.Any(p => Matches(p, project.Name, prefix: false));
        }

        public bool IsNamespaceExcluded(INamespaceSymbol ns)
        {
            if (ExcludeNamespaces.Count == 0 || ns == null || ns.IsGlobalNamespace) return false;
            var name = ns.ToDisplayString();
            return ExcludeNamespaces.Any(p => Matches(p, name, prefix: true));
        }

        /// <summary>
        /// "*" is a wildcard. Without a wildcard, a namespace pattern also matches its child
        /// namespaces ("MyApp.Legacy" matches "MyApp.Legacy.Orders").
        /// </summary>
        internal static bool Matches(string pattern, string value, bool prefix)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (pattern.IndexOf('*') < 0)
            {
                return value.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                       (prefix && value.StartsWith(pattern + ".", StringComparison.OrdinalIgnoreCase));
            }
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static readonly string[] TestFrameworkAssemblies =
        {
            "xunit.core", "xunit.v3.core", "nunit.framework", "Microsoft.VisualStudio.TestPlatform.TestFramework",
            "MSTest.TestFramework", "TUnit.Core",
        };

        /// <summary>A project that references a test framework, or is named like a test project.</summary>
        public static bool IsTestProject(Project project)
        {
            var name = project.Name ?? "";
            var paren = name.IndexOf('(');   // multi-targeted projects: "MyApp.Tests(net8.0)"
            if (paren > 0) name = name.Substring(0, paren);

            if (name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("Tests", StringComparison.Ordinal))
                return true;

            foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
            {
                var file = Path.GetFileNameWithoutExtension(reference.FilePath ?? "");
                if (TestFrameworkAssemblies.Any(t => t.Equals(file, StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ .editorconfig fallback

        /// <summary>Reads callflow.* keys; returns the path of the nearest file that had any, or null.</summary>
        private static string ReadEditorConfigFiles(string documentPath, Dictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(documentPath)) return null;

            var files = new List<string>();
            for (var dir = Path.GetDirectoryName(documentPath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                var file = Path.Combine(dir, ".editorconfig");
                if (!File.Exists(file)) continue;
                files.Add(file);
                if (IsRoot(file)) break;
            }

            // Farthest file first, so that the nearest file wins.
            string used = null;
            for (var i = files.Count - 1; i >= 0; i--)
                if (ParseFile(files[i], values)) used = files[i];
            return used;
        }

        private static bool IsRoot(string path)
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal)) return false;   // only the preamble can hold "root"
                var eq = line.IndexOf('=');
                if (eq > 0 && line.Substring(0, eq).Trim().Equals("root", StringComparison.OrdinalIgnoreCase))
                    return line.Substring(eq + 1).Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool ParseFile(string path, Dictionary<string, string> values)
        {
            var found = false;
            var applies = false;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                if (line[0] == '[')
                {
                    applies = SectionAppliesToCSharp(line.Trim('[', ']').Trim());
                    continue;
                }
                if (!applies) continue;

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                if (!key.StartsWith("callflow.", StringComparison.OrdinalIgnoreCase)) continue;
                values[key.ToLowerInvariant()] = line.Substring(eq + 1).Trim();
                found = true;
            }
            return found;
        }

        /// <summary>Approximation of editorconfig globs: [*], [*.cs], [**.cs], [**/*.cs], [*.{cs,vb}].</summary>
        private static bool SectionAppliesToCSharp(string glob)
        {
            var name = glob.Substring(glob.LastIndexOf('/') + 1);
            if (name == "*" || name == "**" || name == "*.*") return true;
            if (!name.StartsWith("*", StringComparison.Ordinal)) return false;
            if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return true;

            var brace = name.IndexOf('{');
            if (brace < 0) return false;
            var inner = name.Substring(brace + 1).TrimEnd('}');
            return inner.Split(',').Any(x => x.Trim().Equals("cs", StringComparison.OrdinalIgnoreCase));
        }
    }
}

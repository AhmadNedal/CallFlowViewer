using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace CallFlowViewer
{
    internal static class SymbolText
    {
        private static readonly SymbolDisplayFormat Format = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        public static string Display(ISymbol symbol)
        {
            if (symbol is IMethodSymbol m && m.MethodKind == MethodKind.Constructor)
            {
                var ps = string.Join(", ", m.Parameters.Select(p => p.Type.ToDisplayString(Format)));
                return "new " + m.ContainingType.ToDisplayString(Format) + "(" + ps + ")";
            }
            return symbol.ToDisplayString(Format);
        }
    }

    /// <summary>
    /// Explains WHY a method has no callers in the solution: someone outside the code calls it
    /// (the runtime, ASP.NET, a test runner, the DI container, an event...).
    /// </summary>
    internal static class EntryPointClassifier
    {
        private static readonly string[] HttpVerbs = { "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions" };
        private static readonly string[] TestAttributes = { "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod" };

        public static void Classify(ISymbol symbol, out string kind, out string detail)
        {
            detail = null;
            var type = symbol.ContainingType;
            var method = symbol as IMethodSymbol;
            var attrs = symbol.GetAttributes();

            if (method != null && method.IsStatic && method.Name == "Main")
            { kind = "Program start (Main)"; return; }

            if (method != null && type != null && type.Name == "Program" && method.Name.StartsWith("<", StringComparison.Ordinal))
            { kind = "Program start (top-level statements)"; return; }

            if (attrs.Any(a => TestAttributes.Contains(ShortName(a))))
            { kind = "Unit test"; return; }

            if (method != null && IsController(type) && method.DeclaredAccessibility == Accessibility.Public && !method.IsStatic && method.MethodKind == MethodKind.Ordinary)
            {
                kind = "HTTP endpoint";
                detail = DescribeRoute(method, type);
                return;
            }

            if (InheritsFrom(type, "Hub"))
            { kind = "SignalR hub method (called by clients)"; return; }

            if (method != null && (method.Name == "ExecuteAsync" || method.Name == "StartAsync" || method.Name == "StopAsync") &&
                (InheritsFrom(type, "BackgroundService") || Implements(type, "IHostedService")))
            { kind = "Background service (started by the host)"; return; }

            if (method != null && (method.Name == "Invoke" || method.Name == "InvokeAsync") && type != null &&
                type.Name.EndsWith("Middleware", StringComparison.Ordinal))
            { kind = "Middleware (called by the ASP.NET pipeline)"; return; }

            if (method != null && (method.Name == "Handle" || method.Name == "HandleAsync" || method.Name == "Consume") &&
                type != null && type.AllInterfaces.Any(i => i.Name == "IRequestHandler" ||
                                                            i.Name == "INotificationHandler" ||
                                                            i.Name == "IConsumer" ||
                                                            i.Name == "IHandleMessages"))
            { kind = "Message / request handler (called by MediatR / bus)"; return; }

            if (method != null && method.Parameters.Length == 2 &&
                method.Parameters[0].Type.SpecialType == SpecialType.System_Object &&
                (method.Parameters[1].Type.Name == "EventArgs" || InheritsFrom(method.Parameters[1].Type as INamedTypeSymbol, "EventArgs")))
            { kind = "Event handler (UI / event subscription)"; return; }

            if (method != null && method.MethodKind == MethodKind.Constructor)
            { kind = "Constructor (likely created by DI / framework)"; return; }

            if (method != null && method.IsOverride)
            { kind = "Override (called by the base class / framework)"; return; }

            if (attrs.Any(a => ShortName(a).EndsWith("Function", StringComparison.Ordinal) || ShortName(a) == "FunctionName"))
            { kind = "Azure Function trigger"; return; }

            if (symbol.DeclaredAccessibility == Accessibility.Public)
            { kind = "No callers in this solution (public API, reflection or external)"; return; }

            kind = "No callers found (possibly unused, or called via reflection)";
        }

        private static string DescribeRoute(IMethodSymbol method, INamedTypeSymbol controller)
        {
            var verbAttr = method.GetAttributes().FirstOrDefault(a => HttpVerbs.Contains(ShortName(a)));
            var verb = verbAttr != null ? ShortName(verbAttr).Substring(4).ToUpperInvariant() : "ANY";

            var methodRoute = FirstStringArg(verbAttr) ?? FirstStringArg(method.GetAttributes().FirstOrDefault(a => ShortName(a) == "Route"));
            var classRoute = FirstStringArg(controller.GetAttributes().FirstOrDefault(a => ShortName(a) == "Route"));

            string route;
            if (methodRoute != null && methodRoute.StartsWith("/", StringComparison.Ordinal)) route = methodRoute;
            else route = "/" + string.Join("/", new[] { classRoute, methodRoute }.Where(s => !string.IsNullOrEmpty(s)));

            var ctrlName = controller.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? controller.Name.Substring(0, controller.Name.Length - "Controller".Length)
                : controller.Name;
            route = route.Replace("[controller]", ctrlName.ToLowerInvariant()).Replace("[action]", method.Name.ToLowerInvariant());
            return verb + " " + route;
        }

        private static string FirstStringArg(AttributeData a)
            => a != null && a.ConstructorArguments.Length > 0 ? a.ConstructorArguments[0].Value as string : null;

        private static bool IsController(INamedTypeSymbol type)
        {
            if (type == null) return false;
            if (type.GetAttributes().Any(a => ShortName(a) == "ApiController")) return true;
            if (InheritsFrom(type, "ControllerBase") || InheritsFrom(type, "Controller")) return true;
            return false;
        }

        private static bool InheritsFrom(INamedTypeSymbol type, string baseName)
        {
            for (var t = type?.BaseType; t != null; t = t.BaseType)
                if (t.Name == baseName) return true;
            return false;
        }

        private static bool Implements(INamedTypeSymbol type, string interfaceName)
            => type != null && type.AllInterfaces.Any(i => i.Name == interfaceName);

        private static string ShortName(AttributeData a)
        {
            var n = a.AttributeClass?.Name ?? "";
            return n.EndsWith("Attribute", StringComparison.Ordinal) ? n.Substring(0, n.Length - "Attribute".Length) : n;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CallFlowViewer
{
    internal sealed class DiRegistration
    {
        public INamedTypeSymbol Service;
        public INamedTypeSymbol Implementation;   // null when a factory decides what is created
        public string Lifetime;                    // Scoped / Transient / Singleton
        public string Method;                      // AddScoped, TryAddSingleton, AddHttpClient, ...
        public string File;
        public int Line;
        public string Project;
    }

    /// <summary>Result of checking one "call through an interface" against the DI registrations.</summary>
    internal sealed class DiCheck
    {
        /// <summary>registered / other / factory / missing</summary>
        public string Status;
        public string Service;
        public readonly List<DiRegistration> Registrations = new List<DiRegistration>();
    }

    /// <summary>
    /// Finds Microsoft.Extensions.DependencyInjection registrations in the solution:
    /// AddScoped / AddTransient / AddSingleton (generic, typeof, factory, instance), their TryAdd and
    /// Keyed variants, and typed HttpClients. Test projects are skipped: they register test doubles.
    /// </summary>
    internal static class DiRegistrationScanner
    {
        private static readonly string[] Words = { "Scoped", "Transient", "Singleton", "AddHttpClient" };

        public static async Task<List<DiRegistration>> ScanAsync(Solution solution, Func<string, string> relativePath, CancellationToken ct)
        {
            var result = new List<DiRegistration>();
            foreach (var project in solution.Projects)
            {
                if (project.Language != LanguageNames.CSharp || CallFlowSettings.IsTestProject(project)) continue;

                foreach (var document in project.Documents)
                {
                    ct.ThrowIfCancellationRequested();

                    // Cheap text check first: most files never mention a registration method.
                    var text = await document.GetTextAsync(ct).ConfigureAwait(false);
                    var content = text.ToString();
                    if (!Words.Any(w => content.IndexOf(w, StringComparison.Ordinal) >= 0)) continue;

                    var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                    if (root == null) continue;

                    SemanticModel model = null;
                    foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        var name = MethodName(invocation);
                        if (name == null || !Words.Any(w => name.IndexOf(w, StringComparison.Ordinal) >= 0)) continue;

                        model = model ?? await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
                        if (model == null) break;

                        var registration = Read(invocation, name, model, ct);
                        if (registration == null) continue;

                        var span = invocation.GetLocation().GetLineSpan();
                        registration.File = relativePath(span.Path);
                        registration.Line = span.StartLinePosition.Line + 1;
                        registration.Project = project.Name;
                        result.Add(registration);
                    }
                }
            }
            return result;
        }

        private static string MethodName(InvocationExpressionSyntax invocation)
        {
            switch (invocation.Expression)
            {
                case MemberAccessExpressionSyntax access: return access.Name.Identifier.ValueText;
                case MemberBindingExpressionSyntax binding: return binding.Name.Identifier.ValueText;
                case SimpleNameSyntax simple: return simple.Identifier.ValueText;
                default: return null;
            }
        }

        private static DiRegistration Read(InvocationExpressionSyntax invocation, string name, SemanticModel model, CancellationToken ct)
        {
            if (!(model.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol method)) return null;

            var ns = method.ContainingNamespace?.ToDisplayString() ?? "";
            if (!ns.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal)) return null;

            var isHttpClient = name == "AddHttpClient";
            string lifetime;
            if (name.IndexOf("Scoped", StringComparison.Ordinal) >= 0) lifetime = "Scoped";
            else if (name.IndexOf("Transient", StringComparison.Ordinal) >= 0) lifetime = "Transient";
            else if (name.IndexOf("Singleton", StringComparison.Ordinal) >= 0) lifetime = "Singleton";
            else if (isHttpClient) lifetime = "Transient (typed HttpClient)";
            else return null;

            INamedTypeSymbol service, implementation = null;
            var typeArgs = method.TypeArguments;
            var args = invocation.ArgumentList.Arguments;

            if (typeArgs.Length >= 1)
            {
                // AddScoped<IService, Implementation>() / AddScoped<Service>()
                service = typeArgs[0] as INamedTypeSymbol;
                if (typeArgs.Length >= 2) implementation = typeArgs[1] as INamedTypeSymbol;
            }
            else
            {
                // AddScoped(typeof(IService), typeof(Implementation))
                var typeofs = args.Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().ToList();
                if (typeofs.Count == 0) return null;
                service = model.GetTypeInfo(typeofs[0].Type, ct).Type as INamedTypeSymbol;
                if (typeofs.Count >= 2) implementation = model.GetTypeInfo(typeofs[1].Type, ct).Type as INamedTypeSymbol;
            }
            if (service == null) return null;

            if (implementation == null && !isHttpClient)
            {
                // AddSingleton<IService>(new Implementation()) -> the instance's type
                var instance = InstanceArgument(method, args);
                if (instance != null)
                    implementation = model.GetTypeInfo(instance.Expression, ct).Type as INamedTypeSymbol;
            }

            if (implementation == null)
            {
                // AddScoped<IService>(sp => ...) -> a factory decides; otherwise the service is its own implementation.
                var hasFactory = !isHttpClient && method.Parameters.Any(p =>
                    p.Type is INamedTypeSymbol t && t.TypeKind == TypeKind.Delegate && t.Name == "Func");
                if (!hasFactory) implementation = service;
            }

            return new DiRegistration
            {
                Service = service,
                Implementation = implementation,
                Lifetime = lifetime,
                Method = name,
            };
        }

        private static ArgumentSyntax InstanceArgument(IMethodSymbol method, SeparatedSyntaxList<ArgumentSyntax> args)
        {
            var parameter = method.Parameters.FirstOrDefault(p => p.Name == "implementationInstance" || p.Name == "instance");
            if (parameter == null) return null;

            var named = args.FirstOrDefault(a => a.NameColon != null && a.NameColon.Name.Identifier.ValueText == parameter.Name);
            if (named != null) return named;

            // Reduced extension calls (services.AddX(...)) have no "this" parameter and no "services" argument;
            // static calls (Extensions.AddX(services, ...)) have both, so positions line up either way.
            var index = method.Parameters.IndexOf(parameter);
            return index >= 0 && index < args.Count ? args[index] : null;
        }
    }

    internal sealed class DiIndex
    {
        private readonly List<DiRegistration> _all;

        public DiIndex(List<DiRegistration> all)
        {
            _all = all;
        }

        public bool Any => _all.Count > 0;

        public List<DiRegistration> ForService(ITypeSymbol service)
            => _all.Where(r => Same(r.Service, service)).ToList();

        public List<DiRegistration> ForImplementation(ITypeSymbol implementation)
            => _all.Where(r => r.Implementation != null && Same(r.Implementation, implementation)).ToList();

        /// <summary>
        /// A call through <paramref name="service"/> reaches a method declared in <paramref name="declaringType"/>
        /// only if the registered implementation is that type or derives from it.
        /// </summary>
        public DiCheck Check(ITypeSymbol service, ITypeSymbol declaringType)
        {
            var check = new DiCheck { Service = service.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) };
            check.Registrations.AddRange(ForService(service));

            if (check.Registrations.Count == 0) check.Status = "missing";
            else if (check.Registrations.Any(r => r.Implementation != null && IsSameOrDerived(r.Implementation, declaringType))) check.Status = "registered";
            else if (check.Registrations.Any(r => r.Implementation == null)) check.Status = "factory";
            else check.Status = "other";
            return check;
        }

        private static bool Same(ITypeSymbol a, ITypeSymbol b)
            => a != null && b != null && SymbolEqualityComparer.Default.Equals(a.OriginalDefinition, b.OriginalDefinition);

        private static bool IsSameOrDerived(ITypeSymbol registered, ITypeSymbol declaringType)
        {
            for (var t = registered; t != null; t = t.BaseType)
                if (Same(t, declaringType)) return true;
            return false;
        }
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CallFlowViewer
{
    /// <summary>Extracts the human-readable details of a method / property for the report.</summary>
    internal static class SymbolDetails
    {
        private static readonly SymbolDisplayFormat TypeFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                                  SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        private static readonly SymbolDisplayFormat SignatureFormat = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility |
                           SymbolDisplayMemberOptions.IncludeModifiers |
                           SymbolDisplayMemberOptions.IncludeType |
                           SymbolDisplayMemberOptions.IncludeContainingType |
                           SymbolDisplayMemberOptions.IncludeParameters,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                              SymbolDisplayParameterOptions.IncludeName |
                              SymbolDisplayParameterOptions.IncludeParamsRefOut |
                              SymbolDisplayParameterOptions.IncludeDefaultValue,
            propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                                  SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        public static void Fill(FlowNode node, ISymbol symbol)
        {
            try
            {
                node.Kind = KindOf(symbol);
                node.Accessibility = AccessibilityText(symbol.DeclaredAccessibility);
                node.ContainingType = symbol.ContainingType?.ToDisplayString();
                node.Signature = Signature(symbol);
                node.Summary = Summary(symbol);

                if (symbol.IsStatic) node.Modifiers.Add("static");
                if (symbol.IsAbstract) node.Modifiers.Add("abstract");
                if (symbol.IsVirtual) node.Modifiers.Add("virtual");
                if (symbol.IsOverride) node.Modifiers.Add("override");
                if (symbol.IsSealed) node.Modifiers.Add("sealed");
                if (symbol.IsExtern) node.Modifiers.Add("extern");

                switch (symbol)
                {
                    case IMethodSymbol m:
                        if (m.IsAsync) node.Modifiers.Add("async");
                        if (m.IsExtensionMethod) node.Modifiers.Add("extension");
                        if (m.MethodKind != MethodKind.Constructor)
                            node.ReturnType = m.ReturnType.ToDisplayString(TypeFormat);
                        foreach (var p in m.Parameters)
                            node.Parameters.Add(new KeyValuePair<string, string>(p.Name, ParamType(p)));
                        break;
                    case IPropertySymbol p:
                        node.ReturnType = p.Type.ToDisplayString(TypeFormat);
                        foreach (var ip in p.Parameters)
                            node.Parameters.Add(new KeyValuePair<string, string>(ip.Name, ParamType(ip)));
                        break;
                }

                foreach (var a in symbol.GetAttributes())
                    node.Attributes.Add(AttributeText(a));
            }
            catch
            {
                // Details are a bonus: never fail the whole report because of them.
            }
        }

        private static string KindOf(ISymbol symbol)
        {
            switch (symbol)
            {
                case IPropertySymbol p: return p.IsIndexer ? "Indexer" : "Property";
                case IMethodSymbol m:
                    switch (m.MethodKind)
                    {
                        case MethodKind.Constructor: return "Constructor";
                        case MethodKind.StaticConstructor: return "Static constructor";
                        case MethodKind.UserDefinedOperator:
                        case MethodKind.Conversion: return "Operator";
                        case MethodKind.ExplicitInterfaceImplementation: return "Method (explicit interface)";
                        default: return m.IsExtensionMethod ? "Extension method" : "Method";
                    }
                case IFieldSymbol _: return "Field initializer";
                default: return symbol.Kind.ToString();
            }
        }

        private static string Signature(ISymbol symbol)
        {
            if (symbol is IMethodSymbol m && m.MethodKind == MethodKind.Constructor)
            {
                var ps = string.Join(", ", m.Parameters.Select(p => ParamType(p) + " " + p.Name));
                var acc = AccessibilityText(m.DeclaredAccessibility);
                return (acc.Length > 0 ? acc + " " : "") + m.ContainingType.Name + "(" + ps + ")";
            }

            var sig = symbol.ToDisplayString(SignatureFormat);
            if (symbol is IMethodSymbol am && am.IsAsync && !sig.Contains("async "))
            {
                // Roslyn does not print "async"; insert it after the modifiers for readability.
                var acc = AccessibilityText(am.DeclaredAccessibility);
                sig = acc.Length > 0 && sig.StartsWith(acc + " ", StringComparison.Ordinal)
                    ? acc + " async " + sig.Substring(acc.Length + 1)
                    : "async " + sig;
            }
            return sig;
        }

        private static string ParamType(IParameterSymbol p)
        {
            var prefix = p.RefKind == RefKind.Ref ? "ref " : p.RefKind == RefKind.Out ? "out " : p.RefKind == RefKind.In ? "in " : "";
            if (p.IsParams) prefix = "params " + prefix;
            return prefix + p.Type.ToDisplayString(TypeFormat);
        }

        private static string AttributeText(AttributeData a)
        {
            var name = a.AttributeClass?.Name ?? "?";
            if (name.EndsWith("Attribute", StringComparison.Ordinal)) name = name.Substring(0, name.Length - "Attribute".Length);

            var args = a.ConstructorArguments.Select(SafeArg)
                .Concat(a.NamedArguments.Select(n => n.Key + " = " + SafeArg(n.Value)))
                .ToList();
            return args.Count == 0 ? name : name + "(" + string.Join(", ", args) + ")";
        }

        private static string SafeArg(TypedConstant c)
        {
            try { return c.ToCSharpString(); } catch { return "?"; }
        }

        private static string AccessibilityText(Accessibility a)
        {
            switch (a)
            {
                case Microsoft.CodeAnalysis.Accessibility.Public: return "public";
                case Microsoft.CodeAnalysis.Accessibility.Internal: return "internal";
                case Microsoft.CodeAnalysis.Accessibility.Private: return "private";
                case Microsoft.CodeAnalysis.Accessibility.Protected: return "protected";
                case Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal: return "protected internal";
                case Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal: return "private protected";
                default: return "";
            }
        }

        /// <summary>Plain text of the XML doc &lt;summary&gt;, with &lt;see cref&gt; turned into short names.</summary>
        private static string Summary(ISymbol symbol)
        {
            var xml = symbol.GetDocumentationCommentXml();
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var doc = XDocument.Parse(xml);
                var summary = doc.Descendants("summary").FirstOrDefault();
                if (summary == null) return null;

                var sb = new StringBuilder();
                foreach (var n in summary.Nodes())
                {
                    if (n is XText t) sb.Append(t.Value);
                    else if (n is XElement e)
                    {
                        var cref = (string)e.Attribute("cref") ?? (string)e.Attribute("name") ?? (string)e.Attribute("langword");
                        if (cref != null)
                        {
                            var shortName = cref.Contains(":") ? cref.Substring(cref.IndexOf(':') + 1) : cref;
                            var paren = shortName.IndexOf('(');
                            if (paren >= 0) shortName = shortName.Substring(0, paren);
                            var parts = shortName.Split('.');
                            sb.Append(parts[parts.Length - 1]);
                        }
                        else sb.Append(e.Value);
                    }
                }
                var text = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
                return text.Length == 0 ? null : text;
            }
            catch
            {
                return null;
            }
        }
    }
}

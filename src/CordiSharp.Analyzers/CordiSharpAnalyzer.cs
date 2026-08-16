using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CordiSharp.Analyzers;

/// <summary>Validates CordiSharp plugin declarations at compile time.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CordiSharpAnalyzer : DiagnosticAnalyzer
{
    public const string PluginConfigUnsupportedProperty = "CORDIS001";
    public const string InjectEmptyName = "CORDIS002";
    public const string PluginNoPublicConstructor = "CORDIS003";

    private static readonly DiagnosticDescriptor PluginConfigUnsupportedPropertyRule = new(
        PluginConfigUnsupportedProperty,
        "Unsupported config property type",
        "Property '{0}' of config type '{1}' has a type that cannot be represented by a CordiSharp schema; it will be treated as Schema.Any()",
        "CordiSharp.Schema",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InjectEmptyNameRule = new(
        InjectEmptyName,
        "Empty inject name",
        "Inject name must not be empty or whitespace",
        "CordiSharp.Plugins",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor PluginNoPublicConstructorRule = new(
        PluginNoPublicConstructor,
        "Plugin class needs a public constructor",
        "Plugin class '{0}' must have at least one public constructor so CordiSharp can instantiate it",
        "CordiSharp.Plugins",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [PluginConfigUnsupportedPropertyRule, InjectEmptyNameRule, PluginNoPublicConstructorRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var pluginAttr = start.Compilation.GetTypeByMetadataName("CordiSharp.Registry.PluginAttribute");
            var injectAttr = start.Compilation.GetTypeByMetadataName("CordiSharp.Registry.InjectAttribute");
            var configAttr = start.Compilation.GetTypeByMetadataName("CordiSharp.Schema.PluginConfigAttribute");
            var iPlugin = start.Compilation.GetTypeByMetadataName("CordiSharp.Registry.IPlugin`1");

            start.RegisterSymbolAction(symbolContext => AnalyzeNamedType(symbolContext, pluginAttr, injectAttr, configAttr, iPlugin),
                SymbolKind.NamedType);
        });
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context, INamedTypeSymbol? pluginAttr,
        INamedTypeSymbol? injectAttr, INamedTypeSymbol? configAttr, INamedTypeSymbol? iPlugin)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsStatic) return;

        // CORDIS002: empty inject names
        if (injectAttr is not null)
        {
            foreach (var attribute in type.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, injectAttr)) continue;
                if (attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not string name || string.IsNullOrWhiteSpace(name))
                {
                    context.ReportDiagnostic(Diagnostic.Create(InjectEmptyNameRule, type.Locations.FirstOrDefault()));
                }
            }
        }

        var isPlugin = pluginAttr is not null && type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, pluginAttr));
        var implementsIPlugin = iPlugin is not null && type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, iPlugin));

        if (!isPlugin && !implementsIPlugin) return;

        // CORDIS003: public constructor requirement
        if (!type.IsAbstract)
        {
            var hasPublicCtor = type.InstanceConstructors.Any(c => c.DeclaredAccessibility == Accessibility.Public);
            if (!hasPublicCtor)
            {
                context.ReportDiagnostic(Diagnostic.Create(PluginNoPublicConstructorRule, type.Locations.FirstOrDefault(), type.Name));
            }
        }

        // CORDIS001: config property schema compatibility
        if (configAttr is not null && iPlugin is not null)
        {
            foreach (var iface in type.AllInterfaces)
            {
                if (!SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, iPlugin)) continue;
                var configType = iface.TypeArguments[0] as INamedTypeSymbol;
                if (configType is null) continue;
                var isConfigAnnotated = configType.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, configAttr));
                if (!isConfigAnnotated) continue;
                foreach (var prop in configType.GetMembers().OfType<IPropertySymbol>())
                {
                    if (prop.IsStatic || !prop.CanWrite()) continue;
                    if (IsUnsupportedSchemaType(prop.Type))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(PluginConfigUnsupportedPropertyRule, prop.Locations.FirstOrDefault(), prop.Name, configType.Name));
                    }
                }
                break;
            }
        }
    }

    private static bool IsUnsupportedSchemaType(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
            case SpecialType.System_Boolean:
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
            case SpecialType.System_Int16:
            case SpecialType.System_Byte:
            case SpecialType.System_Double:
            case SpecialType.System_Single:
            case SpecialType.System_Decimal:
                return false;
        }
        if (type.TypeKind == TypeKind.Enum) return false;
        if (type is IArrayTypeSymbol) return false;
        if (type is INamedTypeSymbol named)
        {
            if (named.IsGenericType)
            {
                var def = named.OriginalDefinition.ToDisplayString();
                var supported = def == "System.Collections.Generic.List<T>" || def == "System.Collections.Generic.IList<T>"
                    || def == "System.Collections.Generic.IReadOnlyList<T>" || def == "System.Collections.Generic.ICollection<T>"
                    || def == "System.Collections.Generic.IEnumerable<T>" || def == "System.Collections.Generic.Dictionary<K,V>"
                    || def == "System.Collections.Generic.IDictionary<K,V>" || def == "System.Collections.Generic.IReadOnlyDictionary<K,V>"
                    || def == "System.Nullable<T>";
                if (supported)
                {
                    foreach (var arg in named.TypeArguments)
                    {
                        if (IsUnsupportedSchemaType(arg)) return true;
                    }
                    return false;
                }
            }
            if (named is { TypeKind: TypeKind.Class, IsAbstract: false, InstanceConstructors.Length: > 0 })
            {
                // nested config-like class: recurse into writable properties
                foreach (var prop in named.GetMembers().OfType<IPropertySymbol>())
                {
                    if (!prop.IsStatic && prop.CanWrite() && IsUnsupportedSchemaType(prop.Type)) return true;
                }
                return false;
            }
        }
        return true;
    }
}

internal static class PropertySymbolExtensions
{
    public static bool CanWrite(this IPropertySymbol property) =>
        property.SetMethod is not null && property.SetMethod.DeclaredAccessibility == Accessibility.Public;
}

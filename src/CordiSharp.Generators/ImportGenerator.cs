using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CordiSharp.Generators;

/// <summary>Generates type-safe service accessors from two sources:
/// <list type="bullet">
/// <item><c>[assembly: Import("name", Alias?)]</c> — host entry point; resolution goes
/// through the root context (root scope, ignores isolates).</item>
/// <item><c>[Inject("name", Alias = "...")]</c> on a class — plugin side; resolution goes
/// through the fiber chain (isolate-aware). The alias requests the accessor.</item>
/// </list>
/// For each source it finds the implementing <c>[Service]</c> type in a referenced plugin
/// library, then generates a mirrored interface (the type's public members), a weak bridge
/// that forwards calls to the runtime-loaded plugin instance, and a <c>ctx.&lt;name|alias&gt;</c>
/// C# 14 extension property backed by <c>ImportResolver</c>.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class CordiSharpImportGenerator : IIncrementalGenerator
{
    private const string ImportAttribute = "CordiSharp.Registry.ImportAttribute";
    private const string InjectAttribute = "CordiSharp.Registry.InjectAttribute";
    private const string ServiceAttribute = "CordiSharp.Registry.ServiceAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // assembly-level [Import] (host): root-scope resolution
        var imports = context.SyntaxProvider.ForAttributeWithMetadataName(
            ImportAttribute,
            static (node, _) => node is CompilationUnitSyntax,
            static (ctx, ct) => CreateModels(ctx, rootScope: true))
            .SelectMany(static (models, _) => models);

        // class-level [Inject(..., Alias)] (plugin): isolate-aware resolution
        var injects = context.SyntaxProvider.ForAttributeWithMetadataName(
            InjectAttribute,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, ct) => CreateModels(ctx, rootScope: false))
            .SelectMany(static (models, _) => models);

        var all = imports.Collect().Combine(injects.Collect());
        context.RegisterSourceOutput(all, static (spc, pair) =>
        {
            // group by (scope, accessor name): a host [Import] and a plugin [Inject] may
            // target the same service but resolve differently, so they are NOT merged
            foreach (var group in pair.Left.Concat(pair.Right).GroupBy(m => (m.RootScope, m.Alias ?? m.Name)))
            {
                var model = group.First();
                if (model.Impl is null)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        new DiagnosticDescriptor("CORDIS004", "Import target not found",
                            "no [Service] type named \"{0}\" was found in referenced plugin assemblies",
                            "CordiSharp", DiagnosticSeverity.Error, true),
                        model.Location, model.Name));
                    continue;
                }
                Emit(spc, model);
            }
        });
    }

    private static System.Collections.Immutable.ImmutableArray<ImportModel> CreateModels(
        GeneratorAttributeSyntaxContext ctx, bool rootScope)
    {
        var compilation = ctx.SemanticModel.Compilation;
        var result = System.Collections.Immutable.ImmutableArray.CreateBuilder<ImportModel>();
        foreach (var (name, alias, location) in GetImports(ctx))
        {
            var impl = FindImpl(compilation, name);
            var sameAssembly = impl is not null
                && SymbolEqualityComparer.Default.Equals(impl.ContainingAssembly, compilation.Assembly);

            // [Inject] without an alias: same-assembly implementation → auto direct typed
            // accessor; otherwise → pure dependency (no accessor)
            if (!rootScope && alias is null && !sameAssembly) continue;

            if (impl is null)
            {
                result.Add(new ImportModel(name, alias, rootScope, false, null, [], location));
                continue;
            }
            var members = new List<MemberModel>();
            foreach (var member in impl.GetMembers())
            {
                switch (member)
                {
                    case IMethodSymbol method when IsUsableMethod(method, impl):
                        members.Add(new MemberModel(method.Name, FormatType(method.ReturnType), "method",
                            method.Parameters.Select(p => FormatType(p.Type)).ToList(),
                            method.Parameters.Select(p => FormatTypeKey(p.Type)).ToList(),
                            method.Parameters.Select(p => p.Name).ToList(), false, false));
                        break;
                    case IPropertySymbol property when IsUsableProperty(property, impl):
                        members.Add(new MemberModel(property.Name, FormatType(property.Type), "property", [], [], [],
                            property.GetMethod is not null, property.SetMethod is not null));
                        break;
                }
            }
            result.Add(new ImportModel(name, alias, rootScope, sameAssembly, impl, members, location));
        }
        return result.ToImmutable();
    }

    /// <summary>Yields every (service name, alias) declared by the attributes of this target,
    /// with the attribute location (used for diagnostics).</summary>
    private static IEnumerable<(string Name, string? Alias, Location Location)> GetImports(
        GeneratorAttributeSyntaxContext ctx)
    {
        foreach (var attribute in ctx.Attributes)
        {
            if (attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not string s || s.Length == 0) continue;
            string? alias = null;
            foreach (var named in attribute.NamedArguments)
            {
                if (named.Key == "Alias" && named.Value.Value is string a && a.Length > 0) alias = a;
            }
            var location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? Location.None;
            yield return (s, alias, location);
        }
    }

    private static INamedTypeSymbol? FindImpl(Compilation compilation, string name)
    {
        // same-assembly implementations first (a plugin injecting a sibling plugin)
        foreach (var type in EnumerateTypes(compilation.Assembly.GlobalNamespace))
        {
            if (HasServiceName(type, name)) return type;
        }
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol asm) continue;
            if (SkipAssembly(asm)) continue;
            foreach (var type in EnumerateTypes(asm.GlobalNamespace))
            {
                if (HasServiceName(type, name)) return type;
            }
        }
        return null;
    }

    private static bool IsUsableMethod(IMethodSymbol method, INamedTypeSymbol impl)
    {
        if (method.MethodKind != MethodKind.Ordinary) return false;
        if (method.DeclaredAccessibility != Accessibility.Public || method.IsStatic || method.IsGenericMethod) return false;
        if (!IsUsableType(method.ReturnType, impl)) return false;
        return method.Parameters.All(p => IsUsableType(p.Type, impl));
    }

    private static bool IsUsableProperty(IPropertySymbol property, INamedTypeSymbol impl)
    {
        if (property.DeclaredAccessibility != Accessibility.Public || property.IsStatic || property.IsIndexer) return false;
        return IsUsableType(property.Type, impl);
    }

    /// <summary>Only types that do not come from a plugin assembly are usable in the
    /// generated interface — referencing plugin-local types would force the runtime to load
    /// the plugin assembly into the default context. CordiSharp framework types are always
    /// shared (the host references the framework), so they stay usable even when the service
    /// itself is declared in the framework (e.g. AssemblyLoaderService's members that use
    /// <c>AssemblyPluginSet</c>).</summary>
    private static bool IsUsableType(ITypeSymbol type, INamedTypeSymbol impl)
    {
        if (type.ContainingAssembly is not null
            && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, impl.ContainingAssembly)
            && impl.ContainingAssembly.Name != "CordiSharp")
        {
            return false;
        }
        if (type is INamedTypeSymbol { TypeArguments.Length: > 0 } named)
        {
            return named.TypeArguments.All(t => IsUsableType(t, impl));
        }
        return true;
    }

    private static bool HasServiceName(INamedTypeSymbol type, string name)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract) return false;
        foreach (var attribute in type.GetAttributes())
        {
            var display = attribute.AttributeClass?.ToDisplayString() ?? "";
            if (display is not ("CordiSharp.ServiceAttribute" or "CordiSharp.Registry.ServiceAttribute" or "ServiceAttribute")) continue;
            if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string s && s == name) return true;
        }
        return false;
    }

    private static bool SkipAssembly(IAssemblySymbol asm)
    {
        // BCL / framework-noise assemblies are never import targets. CordiSharp itself is
        // searched: plugin-style framework services (e.g. AssemblyLoaderService, registered
        // as [Service("loader")]) are legitimate import/inject targets.
        return asm.Name.StartsWith("System", StringComparison.Ordinal)
            || asm.Name.StartsWith("Microsoft", StringComparison.Ordinal)
            || asm.Name.StartsWith("netstandard", StringComparison.Ordinal);
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in type.GetTypeMembers()) yield return nested;
        }
        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var type in EnumerateTypes(child)) yield return type;
        }
    }

    /// <summary>Fully-qualified display format used for mirrored interface members; keeps
    /// nullable reference annotations (<c>string?</c>) so the generated contract matches the
    /// service's public API (the files are emitted with <c>#nullable enable</c>).</summary>
    private static readonly SymbolDisplayFormat MirrorFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static string FormatType(ITypeSymbol type) => type.ToDisplayString(MirrorFormat);

    /// <summary>Format for <c>typeof(...)</c> lists in the bridge: nullable reference
    /// annotations are dropped (<c>typeof(string?)</c> is not valid C#; nullable value
    /// types such as <c>int?</c> keep their modifier).</summary>
    private static string FormatTypeKey(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string Q(string value) => "\"" + value + "\"";

    private static string PascalCase(string name)
        => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + 
                                     name.Substring(1);

    private static void Emit(SourceProductionContext spc, ImportModel model)
    {
        var impl = model.Impl!;
        var iface = "I" + impl.Name;
        var bridge = impl.Name + "Bridge";
        // default accessor name follows the same PascalCase rule as same-assembly [Inject]
        var accessor = model.Alias ?? PascalCase(model.Name);
        const string ns = "CordiSharp.Importing.Generated";

        // same-assembly injection: no mirrored interface, no weak bridge — the concrete
        // type is directly accessible, so the accessor returns it via ctx.Get<T>(name)
        if (model.SameAssembly)
        {
            var implType = impl.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var direct = new StringBuilder();
            direct.AppendLine("// <auto-generated/>");
            direct.AppendLine("#nullable enable");
            direct.AppendLine("using CordiSharp;");
            direct.AppendLine();
            direct.AppendLine("namespace " + ns);
            direct.AppendLine("{");
            // same-assembly accessors are internal: only this assembly uses them
            direct.AppendLine("    internal static class " + impl.Name + "InjectExtensions");
            direct.AppendLine("    {");
            direct.AppendLine("        extension(Context ctx)");
            direct.AppendLine("        {");
            direct.AppendLine("            public " + implType + " " + accessor + " => ctx.Get<" + implType + ">(" + Q(model.Name) + ")!;");
            direct.AppendLine("        }");
            direct.AppendLine("    }");
            direct.AppendLine("}");
            spc.AddSource("CordiSharp.Importing.Generated." + impl.Name + ".Inject", SourceText.From(direct.ToString(), Encoding.UTF8));
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using CordiSharp;");
        sb.AppendLine("using CordiSharp.Loading;");
        sb.AppendLine();
        sb.AppendLine("namespace " + ns);
        sb.AppendLine("{");
        sb.AppendLine("    public interface " + iface);
        sb.AppendLine("    {");
        foreach (var m in model.Members)
        {
            if (m.Kind == "method")
            {
                var parameters = string.Join(", ", m.ParamTypes.Zip(m.ParamNames, (t, n) => t + " " + n));
                sb.AppendLine("        " + m.ReturnType + " " + m.Name + "(" + parameters + ");");
            }
            else
            {
                var accessors = (m.Getter ? "get; " : "") + (m.Setter ? "set; " : "");
                sb.AppendLine("        " + m.ReturnType + " " + m.Name + " { " + accessors + "}");
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal sealed class " + bridge + " : " + iface);
        sb.AppendLine("    {");
        sb.AppendLine("        private readonly System.WeakReference<object> _target;");
        sb.AppendLine("        private readonly Context _ctx;");
        sb.AppendLine("        private readonly string _serviceName;");
        sb.AppendLine();
        sb.AppendLine("        public " + bridge + "(object target, string serviceName, Context ctx)");
        sb.AppendLine("        {");
        sb.AppendLine("            _target = new System.WeakReference<object>(target);");
        sb.AppendLine("            _serviceName = serviceName;");
        sb.AppendLine("            _ctx = ctx;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        private object RequireTarget()");
        sb.AppendLine("            => _target.TryGetTarget(out var target)");
        sb.AppendLine("                && ReferenceEquals(target, " + (model.RootScope ? "_ctx.Root.Get(_serviceName, strict: false)" : "_ctx.Get(_serviceName, strict: false)") + ") ? target");
        sb.AppendLine("""
                : throw new PluginUnloadedException($"imported service [{_serviceName}] belongs to an unloaded assembly");
""");
        sb.AppendLine("        private object? Invoke(string name, System.Type[] paramTypes, object?[] args)");
        sb.AppendLine("        {");
        sb.AppendLine("            var target = RequireTarget();");
        sb.AppendLine("            var method = target.GetType().GetMethod(name, paramTypes)");
        sb.AppendLine("""
                    ?? throw new CordisException($"{target.GetType().Name} does not expose method [{name}]");
""");
        sb.AppendLine("            return method.Invoke(target, args);");
        sb.AppendLine("        }");
        sb.AppendLine();
        foreach (var m in model.Members)
        {
            if (m.Kind == "method")
            {
                var parameters = string.Join(", ", m.ParamTypes.Zip(m.ParamNames, (t, n) => t + " " + n));
                var argList = string.Join(", ", m.ParamNames);
                var typeList = m.ParamTypeKeys.Count == 0
                    ? "System.Type.EmptyTypes"
                    : "new[] { " + string.Join(", ", m.ParamTypeKeys.Select(t => "typeof(" + t + ")")) + " }";
                sb.AppendLine("        public " + m.ReturnType + " " + m.Name + "(" + parameters + ")");
                sb.AppendLine("        {");
                if (m.ReturnType == "void")
                {
                    sb.AppendLine("            Invoke(" + Q(m.Name) + ", " + typeList + ", new object?[] { " + argList + " });");
                }
                else
                {
                    sb.AppendLine("            return (" + m.ReturnType + ")Invoke(" + Q(m.Name) + ", " + typeList + ", new object?[] { " + argList + " })!;");
                }
                sb.AppendLine("        }");
            }
            else
            {
                if (m is { Getter: true, Setter: true })
                {
                    sb.AppendLine("        public " + m.ReturnType + " " + m.Name);
                    sb.AppendLine("        {");
                    sb.AppendLine("            get => (" + m.ReturnType + ")RequireTarget().GetType().GetProperty(" + Q(m.Name) + ")!.GetValue(RequireTarget())!;");
                    sb.AppendLine("            set => RequireTarget().GetType().GetProperty(" + Q(m.Name) + ")!.SetValue(RequireTarget(), value);");
                    sb.AppendLine("        }");
                }
                else if (m.Getter)
                {
                    sb.AppendLine("        public " + m.ReturnType + " " + m.Name + " => (" + m.ReturnType + ")RequireTarget().GetType().GetProperty(" + Q(m.Name) + ")!.GetValue(RequireTarget())!;");
                }
                else
                {
                    sb.AppendLine("        public " + m.ReturnType + " " + m.Name + " { set => RequireTarget().GetType().GetProperty(" + Q(m.Name) + ")!.SetValue(RequireTarget(), value); }");
                }
            }
            sb.AppendLine();
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal static class " + impl.Name + "ImportExtensions");
        sb.AppendLine("    {");
        sb.AppendLine("        extension(Context ctx)");
        sb.AppendLine("        {");
        sb.AppendLine("            public " + iface + " " + accessor + " => "
            + (model.RootScope ? "ImportResolver.Resolve<" : "ImportResolver.ResolveLocal<")
            + iface + ", " + bridge + ">(ctx, " + Q(model.Name) + ");");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        spc.AddSource("CordiSharp.Importing.Generated." + iface + (model.RootScope ? ".Root" : ".Local"),
            SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private sealed class ImportModel(string name, string? alias, bool rootScope, bool sameAssembly,
        INamedTypeSymbol? impl, List<MemberModel> members, Location location)
    {
        public string Name { get; } = name;
        public string? Alias { get; } = alias;
        public bool RootScope { get; } = rootScope;
        public bool SameAssembly { get; } = sameAssembly;
        public INamedTypeSymbol? Impl { get; } = impl;
        public List<MemberModel> Members { get; } = members;
        public Location Location { get; } = location;
    }

    private sealed class MemberModel(string name, string returnType, string kind,
        List<string> paramTypes, List<string> paramTypeKeys, List<string> paramNames, bool getter, bool setter)
    {
        public string Name { get; } = name;
        public string ReturnType { get; } = returnType;
        public string Kind { get; } = kind;
        public List<string> ParamTypes { get; } = paramTypes;
        /// <summary>Non-nullable forms of the parameter types, for <c>typeof(...)</c> lists.</summary>
        public List<string> ParamTypeKeys { get; } = paramTypeKeys;
        public List<string> ParamNames { get; } = paramNames;
        public bool Getter { get; } = getter;
        public bool Setter { get; } = setter;
    }
}

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CordiSharp.Generators;

/// <summary>Host-side import generator: for every <c>[Import("name")]</c> annotation, finds
/// the implementing <c>[Service]</c> type in a referenced plugin library, then generates in
/// the host assembly a mirrored interface (the type's public members), a weak bridge that
/// forwards calls to the runtime-loaded plugin instance, and a <c>ctx.&lt;name&gt;</c>
/// accessor (C# 14 extension property) backed by <c>ImportResolver</c>.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class CordiSharpImportGenerator : IIncrementalGenerator
{
    private const string ImportAttribute = "CordiSharp.Registry.ImportAttribute";
    private const string ServiceAttribute = "CordiSharp.Registry.ServiceAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var imports = context.SyntaxProvider.ForAttributeWithMetadataName(
            ImportAttribute,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, ct) => CreateModel(ctx, ct));

        var compiled = imports.Collect();
        context.RegisterSourceOutput(compiled, static (spc, models) =>
        {
            foreach (var group in models.GroupBy(m => m.Name, StringComparer.Ordinal))
            {
                var model = group.First();
                if (model.Impl is null)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        new DiagnosticDescriptor("CORDIS004", "Import target not found",
                            "no [Service] type named \"{0}\" was found in referenced plugin assemblies",
                            "CordiSharp", DiagnosticSeverity.Error, true), model.Location));
                    continue;
                }
                Emit(spc, model);
            }
        });
    }

    private static ImportModel? CreateModel(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var name = GetImportName(ctx);
        if (string.IsNullOrEmpty(name)) return null;
        var compilation = ctx.SemanticModel.Compilation;
        var location = ctx.TargetSymbol.Locations.FirstOrDefault() ?? Location.None;

        INamedTypeSymbol? impl = null;
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol asm) continue;
            if (SkipAssembly(asm)) continue;
            foreach (var type in EnumerateTypes(asm.GlobalNamespace))
            {
                if (!HasServiceName(type, name)) continue;
                impl = type;
                break;
            }
            if (impl is not null) break;
        }
        if (impl is null) return new ImportModel(name, null, [], location);

        var members = new List<MemberModel>();
        foreach (var member in impl.GetMembers())
        {
            switch (member)
            {
                case IMethodSymbol method when IsUsableMethod(method, impl):
                    members.Add(new MemberModel(method.Name, FormatType(method.ReturnType), "method",
                        method.Parameters.Select(p => FormatType(p.Type)).ToList(),
                        method.Parameters.Select(p => p.Name).ToList(), false, false));
                    break;
                case IPropertySymbol property when IsUsableProperty(property, impl):
                    members.Add(new MemberModel(property.Name, FormatType(property.Type), "property", [], [],
                        property.GetMethod is not null, property.SetMethod is not null));
                    break;
            }
        }
        return new ImportModel(name, impl, members, location);
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

    /// <summary>Only types that do not come from the plugin assembly are usable in the
    /// generated interface — referencing plugin-local types would force the runtime to load
    /// the plugin assembly into the default context.</summary>
    private static bool IsUsableType(ITypeSymbol type, INamedTypeSymbol impl)
    {
        if (type.ContainingAssembly is not null
            && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, impl.ContainingAssembly))
        {
            return false;
        }
        if (type is INamedTypeSymbol named && named.TypeArguments.Length > 0)
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
        return asm.Name == "CordiSharp"
            || asm.Name.StartsWith("System", StringComparison.Ordinal)
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

    private static string FormatType(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string Q(string value) => "\"" + value + "\"";

    private static string? GetImportName(GeneratorAttributeSyntaxContext ctx)
    {
        foreach (var attribute in ctx.Attributes)
        {
            var display = attribute.AttributeClass?.ToDisplayString() ?? "";
            if (display is not ("CordiSharp.ImportAttribute" or "CordiSharp.Registry.ImportAttribute" or "ImportAttribute")) continue;
            if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string s && s.Length > 0) return s;
        }
        return null;
    }

    private static void Emit(SourceProductionContext spc, ImportModel model)
    {
        var impl = model.Impl!;
        var iface = "I" + impl.Name;
        var bridge = impl.Name + "Bridge";
        var ns = "CordiSharp.Importing.Generated";

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
        sb.AppendLine("                && ReferenceEquals(target, _ctx.Root.Get(_serviceName, strict: false)) ? target");
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
                var typeList = string.Join(", ", m.ParamTypes.Select(t => "typeof(" + t + ")"));
                sb.AppendLine("        public " + m.ReturnType + " " + m.Name + "(" + parameters + ")");
                sb.AppendLine("        {");
                if (m.ReturnType == "void")
                {
                    sb.AppendLine("            Invoke(" + Q(m.Name) + ", new[] { " + typeList + " }, new object?[] { " + argList + " });");
                }
                else
                {
                    sb.AppendLine("            return (" + m.ReturnType + ")Invoke(" + Q(m.Name) + ", new[] { " + typeList + " }, new object?[] { " + argList + " })!;");
                }
                sb.AppendLine("        }");
                sb.AppendLine();
            }
            else
            {
                if (m.Getter && m.Setter)
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
                sb.AppendLine();
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static class " + impl.Name + "ImportExtensions");
        sb.AppendLine("    {");
        sb.AppendLine("        extension(Context ctx)");
        sb.AppendLine("        {");
        sb.AppendLine("            public " + iface + " " + model.Name + " => ImportResolver.Resolve<" + iface + ", " + bridge + ">(ctx, " + Q(model.Name) + ");");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        spc.AddSource("CordiSharp.Importing.Generated." + iface, SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private sealed class ImportModel(string name, INamedTypeSymbol? impl, List<MemberModel> members, Location location)
    {
        public string Name { get; } = name;
        public INamedTypeSymbol? Impl { get; } = impl;
        public List<MemberModel> Members { get; } = members;
        public Location Location { get; } = location;
    }

    private sealed class MemberModel(string name, string returnType, string kind,
        List<string> paramTypes, List<string> paramNames, bool getter, bool setter)
    {
        public string Name { get; } = name;
        public string ReturnType { get; } = returnType;
        public string Kind { get; } = kind;
        public List<string> ParamTypes { get; } = paramTypes;
        public List<string> ParamNames { get; } = paramNames;
        public bool Getter { get; } = getter;
        public bool Setter { get; } = setter;
    }
}

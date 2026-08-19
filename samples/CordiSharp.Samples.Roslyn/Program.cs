using CordiSharp;
using CordiSharp.Loading;
using CordiSharp.Plugins.Roslyn;
using static System.Console;

// =========================================================================
// 运行时编译插件：C# 源码 → Roslyn 编译 → AssemblyLoaderService 加载
// （可回收 ALC）→ 反射发现 [Plugin] → 运行 / 卸载
// =========================================================================

var root = Context.Create();

// 1. 先加载 loader 插件：roslyn 服务声明了 [Inject("loader")]，
//    loader 必须先可用（或稍后加载，fiber 会自动从 Pending 激活）
var loaderHandle = root.Plugin(typeof(AssemblyLoaderService));
await loaderHandle;

// 2. 加载 roslyn 插件服务（fiber 激活后才提供 "roslyn" 服务）
var roslynHandle = root.Plugin(typeof(RoslynPluginService));
await roslynHandle;
var roslyn = root.Get<RoslynPluginService>("roslyn")!;
WriteLine($"roslyn service: {roslyn.Name}");

// 3. 插件源码：普通 CordiSharp 插件（[Plugin] 类 + 配置类）
//    —— 运行时编译没有隐式 using，源码要自带 using（编译器会补充一组常用 global using）
const string source = """
    using CordiSharp;
    using CordiSharp.Registry;
    using CordiSharp.Schema;
    using static System.Console;

    [Plugin("runtime-counter")]
    public sealed class RuntimeCounterPlugin : IPlugin<RuntimeCounterConfig>
    {
        public void Load(Context ctx, RuntimeCounterConfig config)
        {
            ctx.Provide("runtime-counter", config.Start);
            ctx.Effect(() =>
            {
                WriteLine($"[runtime] counter plugin loaded (start = {config.Start})");
                return () => WriteLine("[runtime] counter plugin disposed");
            }, "counter-body");
        }
    }

    [PluginConfig]
    public sealed class RuntimeCounterConfig
    {
        [DefaultValue(0)]
        public int Start { get; set; }
    }
    """;

// 4. 编译 + 通过注入的 loader 加载进可回收 ALC
var set = roslyn.CompileAndLoad(source);
WriteLine($"ALC discovered: {string.Join(", ", set.Plugins.Select(p => p.Name))}");

// 5. 运行插件（配置用 dict，loader 会物化成插件自己的配置类型实例）
var handle = set.LoadPlugin("runtime-counter", new Dictionary<string, object?> { ["Start"] = 42 });
await handle;
WriteLine($"counter value: {root.Get<int>("runtime-counter")}");

// 6. 编译错误的诊断（Compile 是纯静态操作，不需要 loader）
try
{
    RoslynPluginService.Compile("class Broken { void M() => ; }");
}
catch (RoslynCompilationException e)
{
    WriteLine($"compile error (expected): {e.Errors.Count} diagnostic(s)");
    foreach (var error in e.Errors) WriteLine($"  {error}");
}

// 7. 卸载整包（verify: false 以便在受限沙箱也能跑通；生产可省略该参数）
await set.UnloadAsync(verify: false);
WriteLine($"counter after unload: {root.Get("runtime-counter")?.ToString() ?? "null"}");

WriteLine("\nRuntime-compiled plugin sample done.");

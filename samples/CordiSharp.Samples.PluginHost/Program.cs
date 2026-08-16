using System.Reflection;
using CordiSharp;
using CordiSharp.Importing.Generated;
using CordiSharp.Loading;
using CordiSharp.Registry;
using CordiSharp.Samples.PluginLibrary;
using static System.Console;

// host-side imports are ASSEMBLY scoped: [assembly: Import(...)] generates the accessor
[assembly: Import("greeter")]

// =========================================================================
// Cross-assembly plugin loading
// The plugins (CounterPlugin, GreeterService, DependentPlugin) live in the
// CordiSharp.Samples.PluginLibrary assembly; this host loads them by type.
// =========================================================================

WriteLine($"host assembly:   {Assembly.GetExecutingAssembly().GetName().Name}");
WriteLine($"plugin assembly: {typeof(CounterPlugin).Assembly.GetName().Name}");

var root = Context.Create();

// ---- 1. static cross-assembly loading via typeof() ----------------------
var counter = await root.Plugin(typeof(CounterPlugin), new CounterConfig { Start = 10 });
WriteLine($"counter loaded, ctx = {counter.CtxRef.Name}");
WriteLine($"counter value: {root.Get<int>("counter")}");

// ---- 2. services + inject across the assembly boundary ------------------
await root.Plugin(typeof(GreeterService));
await root.Plugin(typeof(DependentPlugin), new DependentConfig { Message = "cross-assembly" });

// ---- 3. dynamic discovery: scan the library assembly for [Plugin] types --
var pluginTypes = typeof(CounterPlugin).Assembly.GetExportedTypes()
    .Where(t => t.GetCustomAttribute<PluginAttribute>() != null)
    .ToList();
WriteLine($"discovered [Plugin] types: {string.Join(", ", pluginTypes.Select(t => t.Name))}");

// load an extra instance through discovery into an isolated scope
var isolated = root.Isolate("counter");
var discovered = await isolated.Plugin(pluginTypes[0], new CounterConfig { Start = 99 });
WriteLine($"root counter: {root.Get<int>("counter")}, isolated counter: {isolated.Get<int>("counter")}");

// ---- 4. generated metadata from the library assembly --------------------
WriteLine("generated metadata for CounterPlugin is discovered from the plugin library assembly");

// ---- 5. update / unload --------------------------------------------------
counter.Update(new CounterConfig { Start = 100 });
await counter.Await();
WriteLine($"counter after update: {root.Get<int>("counter")}");

await discovered.DisposeAsync();
WriteLine($"isolated counter after dispose: {(isolated.Get("counter") as int?)?.ToString() ?? "null"}");

// ---- 6. "loading external assemblies" is itself a plugin ---------------------
// 加载 AssemblyLoaderService（服务名 "loader"）后，任何插件都可以 [Inject("loader")]
// 拿到它。插件程序集被放入可回收的 AssemblyLoadContext，卸载时释放所有引用并 Unload。
// 用 isolate 作用域避免与第 1 节默认加载的 "counter" 服务名冲突。
// 用独立的 root：第 1/2 节已在默认 ALC 加载过 counter/greeter（同名服务会冲突），
// 独立 root 既避免冲突，也让 [Import] 访问器的 root 解析命中 ALC 里的服务。
var alcRoot = Context.Create();
await alcRoot.Plugin(typeof(AssemblyLoaderService));
var loader = alcRoot.Get<AssemblyLoaderService>("loader")!;
var set = loader.LoadAssembly(typeof(CounterPlugin).Assembly.Location);
WriteLine($"ALC discovered: {string.Join(", ", set.Plugins.Select(p => p.Name))}");

var alcCounter = set.LoadPlugin("counter", new Dictionary<string, object?> { ["Start"] = 7 });
await alcCounter;
WriteLine($"ALC counter value: {alcRoot.Get<int>("counter")}");

// ---- 7. [Import]: 源生成器在宿主侧生成契约接口 + ctx.greeter 访问器 -------------
// H 引用 L，用 [Import("greeter")] 标注所需服务（见文件末尾的 HostImports）；
// 生成器在 L 里找到实现类型 GreeterService，在 H 生成镜像接口 IGreeterService、
// 弱引用桥和 C#14 扩展属性 ctx.greeter —— 不手写任何契约。
await set.LoadPlugin("GreeterService");
WriteLine($"imported greeter: {alcRoot.Greeter.Greet("cordis")}");

// verify: true（默认）会做强制 GC + 弱引用校验，卸载失败时抛 AssemblyUnloadException；
// 这里用 verify: false 以便在任何环境（含沙箱）都能跑通
await set.UnloadAsync(verify: false);
WriteLine($"ALC counter after unload: {(alcRoot.Get("counter") as int?)?.ToString() ?? "null"}");

WriteLine($"imported greeter after unload: {TryGreeter(alcRoot)}");

WriteLine("\nCross-assembly sample done.");

static string TryGreeter(Context ctx)
{
    try { return ctx.Greeter.Greet("cordis"); }
    catch (PluginUnloadedException) { return "PluginUnloadedException (expected)"; }
}
using System.Reflection;
using CordiSharp;
using CordiSharp.Registry;
using CordiSharp.Samples.PluginLibrary;

// =========================================================================
// Cross-assembly plugin loading
// The plugins (CounterPlugin, GreeterService, DependentPlugin) live in the
// CordiSharp.Samples.PluginLibrary assembly; this host loads them by type.
// =========================================================================

Console.WriteLine($"host assembly:   {Assembly.GetExecutingAssembly().GetName().Name}");
Console.WriteLine($"plugin assembly: {typeof(CounterPlugin).Assembly.GetName().Name}");

var root = Context.Create();

// ---- 1. static cross-assembly loading via typeof() ----------------------
var counter = await root.Plugin(typeof(CounterPlugin), new CounterConfig { Start = 10 });
Console.WriteLine($"counter loaded, ctx = {counter.CtxRef.Name}");
Console.WriteLine($"counter value: {root.Get<int>("counter")}");

// ---- 2. services + inject across the assembly boundary ------------------
await root.Plugin(typeof(GreeterService));
await root.Plugin(typeof(DependentPlugin), new DependentConfig { Message = "cross-assembly" });

// ---- 3. dynamic discovery: scan the library assembly for [Plugin] types --
var pluginTypes = typeof(CounterPlugin).Assembly.GetExportedTypes()
    .Where(t => t.GetCustomAttribute<PluginAttribute>() != null)
    .ToList();
Console.WriteLine($"discovered [Plugin] types: {string.Join(", ", pluginTypes.Select(t => t.Name))}");

// load an extra instance through discovery into an isolated scope
var isolated = root.Isolate("counter");
var discovered = await isolated.Plugin(pluginTypes[0], new CounterConfig { Start = 99 });
Console.WriteLine($"root counter: {root.Get<int>("counter")}, isolated counter: {isolated.Get<int>("counter")}");

// ---- 4. generated metadata from the library assembly --------------------
Console.WriteLine("generated metadata for CounterPlugin is discovered from the plugin library assembly");

// ---- 5. update / unload --------------------------------------------------
counter.Update(new CounterConfig { Start = 100 });
await counter.Await();
Console.WriteLine($"counter after update: {root.Get<int>("counter")}");

await discovered.DisposeAsync();
Console.WriteLine($"isolated counter after dispose: {(isolated.Get("counter") as int?)?.ToString() ?? "null"}");

Console.WriteLine("\nCross-assembly sample done.");

using CordiSharp;
using CordiSharp.Events;
using CordiSharp.Registry;
using CordiSharp.Samples.Basic;
using CordiSharp.Schema;

// =========================================================================
// CordiSharp basics: context, plugins, services, events, effects, isolates
// =========================================================================

var root = Context.Create();
Console.WriteLine($"root context: {root}");

// ---- 1. plugins with config (generated metadata + schema) -----------------
var counter = await root.Plugin(typeof(CounterPlugin), new CounterConfig { Start = 10 });
Console.WriteLine($"plugin loaded, ctx = {counter.CtxRef.Name}");
Console.WriteLine($"counter value: {root.Get<int>("counter")}");

// ---- 2. typed events ------------------------------------------------------
var ping = EventKey.Create<string>("ping");
var listener = root.On(ping, (_, message) =>
{
    Console.WriteLine($"ping: {message}");
    return null;
});
root.Emit(ping, "hello");
listener.Dispose();

// ---- 3. serial / waterfall dispatch --------------------------------------
var add = EventKey.Create<int>("math/add");
root.On(add, (_, value) => { Console.WriteLine($"  handler A: {value}"); return value + 1; });
root.On(add, (_, value) => { Console.WriteLine($"  handler B: {value}"); return value * 2; });
var serialResult = await root.Serial(add, 5);
Console.WriteLine($"serial result (first bailed): {serialResult}");

var wf = EventKey.Create<int>("math/wf");
root.OnWaterfall(wf, (value, next) => value + (int)next()!);
root.OnWaterfall(wf, (value, next) => value + (int)next()!);
Console.WriteLine($"waterfall(1, 2) = {root.Waterfall(wf, 1, () => 2)}"); // 4

// ---- 4. effects: disposers run on plugin unload ---------------------------
await root.Plugin((Func<Context, object?>)(ctx =>
{
    ctx.Effect(() =>
    {
        Console.WriteLine("effect started");
        return () => Console.WriteLine("effect disposed");
    }, "demo-effect");
    return null;
}));

// ---- 5. async plugins -----------------------------------------------------
await root.Plugin((Func<Context, Task>)(async ctx =>
{
    await Task.Delay(10);
    ctx.Effect(() =>
    {
        Console.WriteLine("async plugin started");
        return () => Console.WriteLine("async plugin disposed");
    }, "async-effect");
}));

// ---- 6. services ----------------------------------------------------------
await root.Plugin(typeof(GreeterService));
var greeter = root.Get<GreeterService>("greeter")!;
Console.WriteLine($"greeter: {greeter.Greet("CordiSharp")}");

// ---- 7. inject: a plugin that waits for the greeter service ---------------
await root.Plugin(typeof(DependentPlugin), new DependentConfig { Message = "via inject" });

// ---- 8. isolates: hide a service name in a child scope --------------------
var isolated = root.Isolate("counter");
var counter2 = await isolated.Plugin(typeof(CounterPlugin), new CounterConfig { Start = 7 });
Console.WriteLine($"root counter: {root.Get<int>("counter")}, isolated counter: {isolated.Get<int>("counter")}");
await counter2.DisposeAsync();
Console.WriteLine($"isolated counter after dispose: {(isolated.Get("counter") as int?)?.ToString() ?? "null"}");

// ---- 9. update config and restart -----------------------------------------
counter.Update(new CounterConfig { Start = 100 });
await counter.Await(); // wait for the restart to finish
Console.WriteLine($"counter after update: {root.Get<int>("counter")}");

// ---- 10. dispose the plugin (effects run in reverse order) ----------------
Console.WriteLine("disposing counter plugin...");
await counter.DisposeAsync();
Console.WriteLine($"counter after dispose: {(root.Get("counter") as int?)?.ToString() ?? "null"}");

Console.WriteLine("\nBasic sample done.");

namespace CordiSharp.Samples.Basic
{
    // ============================ plugin definitions ===========================

    [Plugin("counter")]
    public sealed class CounterPlugin : IPlugin<CounterConfig>
    {
        public void Load(Context ctx, CounterConfig config)
        {
            ctx.Provide("counter", config.Start);
            ctx.Effect(() =>
            {
                Console.WriteLine($"counter plugin loaded (start = {config.Start})");
                return () => Console.WriteLine("counter plugin disposed");
            }, "counter-body");
        }
    }

    [PluginConfig]
    public sealed class CounterConfig
    {
        [DefaultValue(0)]
        public int Start { get; set; }
    }

    [Service("greeter")]
    public sealed class GreeterService(Context ctx) : Service(ctx)
    {
        public string Greet(string name) => $"Hello, {name}!";
    }

    [Inject("greeter")]
    public sealed class DependentPlugin : IPlugin<DependentConfig>
    {
        public void Load(Context ctx, DependentConfig config)
        {
            var greeter = ctx.Get<GreeterService>("greeter")!;
            Console.WriteLine($"{config.Message}: {greeter.Greet("cordis")}");
        }
    }

    [PluginConfig]
    public sealed class DependentConfig
    {
        public string? Message { get; set; }
    }
}
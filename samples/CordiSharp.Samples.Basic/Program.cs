using CordiSharp;
using CordiSharp.Events;
using CordiSharp.Samples.Basic;
using static System.Console;

// =========================================================================
// CordiSharp basics: context, plugins, services, events, effects, isolates
// =========================================================================

var root = Context.Create();
WriteLine($"root context: {root}");
WriteLine();

// ---- 1. plugins with config (generated metadata + schema) -----------------
var counter = await root.Plugin(typeof(CounterPlugin), new CounterConfig { Start = 10 });
WriteLine($"plugin loaded, ctx = {counter.CtxRef.Name}");
WriteLine($"counter value: {root.Get<int>("counter")}");
WriteLine();

// ---- 2. typed events ------------------------------------------------------
var ping = EventKey.Create<string>("ping");
var listener = root.On(ping, (_, message) =>
{
    WriteLine($"ping: {message}");
    return null;
});
root.Emit(ping, "hello");
listener.Dispose();

// ---- 3. serial / waterfall dispatch --------------------------------------
var add = EventKey.Create<int>("math/add");
root.On(add, (_, value) => { WriteLine($"  handler A: {value}"); return value + 1; });
root.On(add, (_, value) => { WriteLine($"  handler B: {value}"); return value * 2; });
var serialResult = await root.Serial(add, 5);
WriteLine($"serial result (first bailed): {serialResult}");
WriteLine();

var wf = EventKey.Create<int>("math/wf");
root.OnWaterfall(wf, (value, next) => value + (int)next()!);
root.OnWaterfall(wf, (value, next) => value + (int)next()!);
WriteLine($"waterfall(1, 2) = {root.Waterfall(wf, 1, () => 2)}"); // 4
WriteLine();

// ---- 4. effects: disposers run on plugin unload ---------------------------
await root.Plugin(ctx =>
{
    ctx.Effect(() =>
    {
        WriteLine("effect started");
        return () => WriteLine("effect disposed");
    }, "demo-effect");
    return null;
});

// ---- 5. async plugins -----------------------------------------------------
await root.Plugin((Func<Context, Task>)(async ctx =>
{
    await Task.Delay(10);
    ctx.Effect(() =>
    {
        WriteLine("async plugin started");
        return () => WriteLine("async plugin disposed");
    }, "async-effect");
}));
WriteLine();

// ---- 6. services ----------------------------------------------------------
await root.Plugin(typeof(GreeterService));
var greeter = root.Get<GreeterService>("greeter")!;
WriteLine($"greeter: {greeter.Greet("CordiSharp")}");
WriteLine();

// ---- 7. inject: a plugin that waits for the greeter service ---------------
await root.Plugin(typeof(DependentPlugin), new DependentConfig { Message = "via inject" });

// ---- 8. isolates: hide a service name in a child scope --------------------
var isolated = root.Isolate("counter");
var counter2 = await isolated.Plugin(typeof(CounterPlugin), new CounterConfig { Start = 7 });
WriteLine($"root counter: {root.Get<int>("counter")}, isolated counter: {isolated.Get<int>("counter")}");
await counter2.DisposeAsync();
WriteLine($"isolated counter after dispose: {(isolated.Get("counter") as int?)?.ToString() ?? "null"}");
WriteLine();

// ---- 9. update config and restart -----------------------------------------
counter.Update(new CounterConfig { Start = 100 });
await counter.Await(); // wait for the restart to finish
WriteLine($"counter after update: {root.Get<int>("counter")}");
WriteLine();

await root.Fiber.DisposeAsync();
WriteLine("Basic sample done.");
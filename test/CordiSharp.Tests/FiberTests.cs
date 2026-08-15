using CordiSharp.Logger;
using Xunit;

namespace CordiSharp.Tests;

public class FiberTests
{
    [Fact]
    public async Task PluginError_FiberFailed_HookRemoved()
    {
        var root = Context.Create();
        var count = 0;
        var f1 = root.Plugin(ctx =>
        {
            ctx.On(TestEvents.Custom, (_, _) => { count++; return null; });
            throw new Exception("plugin error");
        });
        var f2 = root.Plugin(ctx =>
        {
            ctx.On(TestEvents.Custom, (_, _) => { count++; return null; });
            return null;
        });
        // mirror cordis `await sleep()`: wait for BOTH fibers to settle
        await TestHelpers.WaitUntil(() => f1.State == FiberState.Failed && f2.State == FiberState.Active);
        Assert.Equal(FiberState.Failed, f1.State);
        Assert.Equal(FiberState.Active, f2.State);
        root.Emit(TestEvents.Custom, null);
        Assert.Equal(1, count); // only f2's hook survived
        await f2.DisposeAsync();
    }

    [Fact]
    public async Task DisposeError_IsLoggedNotThrown()
    {
        var root = Context.Create();
        var errors = 0;
        root.LoggerService.Exporter(new CountingExporter(m => { if (m.Level == LogLevel.Error) errors++; }));
        var fiber = await root.Plugin(_ => (Action)(() => throw new Exception("test")));
        Assert.Equal(0, errors);
        await fiber.DisposeAsync();
        await TestHelpers.WaitUntil(() => errors == 1);
    }

    private sealed class CountingExporter(Action<LogMessage> action) : ILogExporter
    {
        public void Export(LogMessage message) => action(message);
    }

    [Fact]
    public async Task UpdateConfig_Restarts()
    {
        var root = Context.Create();
        var configs = new List<string?>();
        var handle = root.Plugin((Action<Context, string?>)((_, config) => configs.Add(config)), "hello");
        await handle.Await();
        Assert.Equal(new[] { "hello" }, configs);

        handle.Update("world");
        await handle.Await();
        Assert.Equal(new[] { "hello", "world" }, configs);

        handle.Update("!!!");
        await handle.Await();
        Assert.Equal(new[] { "hello", "world", "!!!" }, configs);
    }

    [Fact]
    public async Task Restart_Reloads()
    {
        var root = Context.Create();
        var calls = 0;
        var handle = root.Plugin(_ => { calls++; return null; });
        await handle.Await();
        await handle.Restart();
        Assert.Equal(2, calls);
        Assert.Equal(FiberState.Active, handle.State);
    }

    [Fact]
    public async Task InertiaLock_Transitions()
    {
        var root = Context.Create();
        var loadGate = new TaskCompletionSource();
        var unloadGate = new TaskCompletionSource();
        var dispose = root.Provide("foo", 1);
        var handle = root.Inject(["foo"], (_, _) => LoadBody(loadGate, unloadGate));

        await Task.Yield(); await Task.Yield();
        Assert.Equal(FiberState.Loading, handle.State);

        dispose.Dispose(); // inject removed while loading
        await Task.Yield(); await Task.Yield();
        Assert.Equal(FiberState.Loading, handle.State); // still loading

        loadGate.SetResult(); // load completes -> unload begins
        await TestHelpers.WaitUntil(() => handle.State == FiberState.Unloading);
        Assert.Equal(FiberState.Unloading, handle.State);

        unloadGate.SetResult();
        root.Provide("foo", 2);
        await TestHelpers.WaitUntil(() => handle.State == FiberState.Active);
        await handle.DisposeAsync();
    }

    private static async Task<object?> LoadBody(TaskCompletionSource loadGate, TaskCompletionSource unloadGate)
    {
        await loadGate.Task;
        return new Func<ValueTask>(async () => await unloadGate.Task);
    }

    [Fact]
    public async Task ServiceRemoval_ReturnsToPending()
    {
        var root = Context.Create();
        var provider = await root.Plugin(typeof(FooService));
        var loaded = 0;
        var handle = root.Inject(["foo"], (_, _) => { loaded++; return null; });
        await TestHelpers.WaitUntil(() => loaded == 1);
        Assert.Equal(FiberState.Active, handle.State);

        await provider.DisposeAsync();
        await TestHelpers.WaitUntil(() => handle.State == FiberState.Pending);
        Assert.Equal(FiberState.Pending, handle.State);
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task UpdateWhileInjectedServiceReloads()
    {
        var applied = new List<(int value, string mode)>();
        var root = Context.Create();
        var provider = root.Plugin(typeof(ProviderService), new ProviderConfig(1));
        var consumer = root.Inject(["provider"], (ctx, config) =>
        {
            applied.Add((ctx.Get<ProviderService>("provider")!.Value, ((ConsumerConfig?)config)?.Mode ?? ""));
            return null;
        });
        // NOTE: Inject callback does not carry a config; use the fiber update instead
        await provider.Await();
        await consumer.Await();
        await TestHelpers.WaitUntil(() => applied.Count == 1);
        Assert.Equal(new[] { (1, "") }, applied);

        provider.Update(new ProviderConfig(2));
        // mirror cordis: await BOTH the provider and the injected consumer
        await provider.Await();
        await consumer.Await();
        await TestHelpers.WaitUntil(() => applied.Count == 2);
        Assert.Equal((2, ""), applied[1]);
        await provider.DisposeAsync();
        await consumer.DisposeAsync();
    }

    public sealed class ProviderConfig(int value)
    {
        public int Value { get; set; } = value;
    }

    public sealed class ConsumerConfig
    {
        public string? Mode { get; set; }
    }

    private sealed class ProviderService(Context ctx, ProviderConfig config) : Service(ctx, "provider")
    {
        public int Value { get; } = config.Value;
    }

    private sealed class FooService(Context ctx) : Service(ctx, "foo");
}
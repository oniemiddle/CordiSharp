using CordiSharp.Registry;
using Xunit;

namespace CordiSharp.Tests;

public class PluginTests
{
    [Fact]
    public async Task Apply_FunctionalPlugin()
    {
        var root = Context.Create();
        object? received = null;
        var options = new Dictionary<string, object?> { ["foo"] = "bar" };
        await root.Plugin((Action<Context, object>)((_, config) => received = config), options);
        Assert.Same(options, received);
    }

    [Fact]
    public async Task Apply_ObjectPlugin()
    {
        var root = Context.Create();
        var called = 0;
        var options = new Dictionary<string, object?> { ["bar"] = "foo" };
        await root.Plugin(new TestObjectPlugin((_, o) => { called++; Assert.Same(options, o); return null; }), options);
        Assert.Equal(1, called);
    }

    [Fact]
    public void Apply_InvalidPlugin_Throws()
    {
        var root = Context.Create();
        Assert.Throws<InvalidPluginException>(() => root.Plugin(new object()));
        Assert.Throws<InvalidPluginException>(() => root.Plugin(null!));
    }

    [Fact]
    public async Task InactiveContext_ThrowsInsideDisposer()
    {
        var root = Context.Create();
        var callback = 0;
        var fiber = root.Plugin(ctx =>
        {
            return () =>
            {
                Assert.Throws<InactiveEffectException>(() => ctx.Plugin((Action<Context>)(_ => callback++)));
                Assert.Throws<InactiveEffectException>(() => ctx.Effect((Func<object?>)(() => null)));
                Assert.Throws<InactiveEffectException>(() => ctx.On(TestEvents.Custom, (_, _) => null));
            };
        });
        await fiber.DisposeAsync();
        Assert.Equal(0, callback);
    }

    [Fact]
    public async Task NestedPlugins_DisposeCascades()
    {
        var root = Context.Create();
        var count = 0;
        root.On(TestEvents.Custom, (_, _) => { count++; return null; });
        var rootFiber = await root.Plugin((Func<Context, Task>)(async ctx =>
        {
            ctx.On(TestEvents.Custom, (_, _) => { count++; return null; });
            await ctx.Plugin((Func<Context, Task>)(async ctx =>
            {
                ctx.On(TestEvents.Custom, (_, _) => { count++; return null; });
                await ctx.Plugin((Func<Context, Task>)(ctx =>
                {
                    ctx.On(TestEvents.Custom, (_, _) => { count++; return null; });
                    return Task.CompletedTask;
                }));
            }));
        }));

        Assert.Equal(3, root.Registry.Size);
        root.Emit(TestEvents.Custom, null);
        Assert.Equal(4, count); // root hook + 3 plugin hooks

        count = 0;
        await rootFiber.DisposeAsync();
        Assert.Equal(0, root.Registry.Size);
        root.Emit(TestEvents.Custom, null);
        Assert.Equal(1, count); // only the root-level hook remains

        count = 0;
        await rootFiber.DisposeAsync();
        Assert.Equal(0, root.Registry.Size);
        root.Emit(TestEvents.Custom, null);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ContextNames()
    {
        var root = Context.Create();
        Assert.Equal("Context <root>", root.ToString());
        await root.Plugin(ctx => { Assert.Equal("Context <root>", ctx.ToString()); return Task.CompletedTask; });
        await root.Plugin(NamedPlugin.Run);
        await root.Plugin(new TestObjectPlugin((ctx, _) => { Assert.Equal("Context <bar>", ctx.ToString()); return null; }) { Name = "bar" });
    }

    private sealed class NamedPlugin
    {
        public static Task Run(Context ctx)
        {
            Assert.Equal("Context <Run>", ctx.ToString());
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RootDispose_UnloadsPlugins()
    {
        var root = Context.Create();
        var disposed = 0;
        var fiber = root.Plugin((Func<Context, object?>)(_ => () => disposed++));
        Assert.Equal(0, root.Fiber.Uid);
        Assert.Equal(1, fiber.Fiber.Uid);
        Assert.Equal(0, disposed);
        Assert.Equal(1, root.Fiber.DisposableCount);

        await root.Fiber.DisposePluginAsync();
        Assert.Equal(0, root.Fiber.Uid);
        Assert.Null(fiber.Fiber.Uid);
        Assert.Equal(1, disposed);
        Assert.Equal(0, root.Fiber.DisposableCount);

        await root.Fiber.DisposePluginAsync();
        Assert.Equal(1, disposed);
        Assert.Equal(0, root.Fiber.DisposableCount);
    }

    [Fact]
    public async Task ServiceInit_Lifecycle()
    {
        InitPlugin.Reset();
        var root = Context.Create();
        var fiber = await root.Plugin(typeof(InitPlugin));
        Assert.Equal(1, InitPlugin.Started);
        Assert.Equal(0, InitPlugin.Stopped);
        await fiber.DisposeAsync();
        Assert.Equal(1, InitPlugin.Started);
        Assert.Equal(1, InitPlugin.Stopped);
    }

    private sealed class InitPlugin(Context ctx) : Service(ctx, "init")
    {
        public static int Started;
        public static int Stopped;
        protected override object Init() { Started++; return () => Stopped++; }
        public static void Reset() { Started = 0; Stopped = 0; }
    }

    [Fact]
    public async Task RegistryDelete_DisposesFibers()
    {
        var root = Context.Create();
        var disposed = 0;
        var plugin = (Func<Context, object?>)(_ => () => disposed++);
        await root.Plugin(plugin);
        Assert.Equal(1, root.Registry.Size);
        Assert.True(root.RegistryDelete(plugin));
        await TestHelpers.WaitUntil(() => disposed == 1);
        Assert.Equal(0, root.Registry.Size);
    }

    private sealed class TestObjectPlugin : IPluginObject
    {
        private readonly Func<Context, object?, object?> _apply;
        public string? Name { get; set; }
        public TestObjectPlugin(Func<Context, object?, object?> apply) => _apply = apply;
        public TestObjectPlugin(Action<Context, object?> apply) => _apply = (ctx, config) => { apply(ctx, config); return null; };
        public object? Apply(Context ctx, object? config) => _apply(ctx, config);
    }
}
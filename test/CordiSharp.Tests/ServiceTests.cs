using CordiSharp.Registry;
using Xunit;

namespace CordiSharp.Tests;

public class ServiceTests
{
    [Fact]
    public async Task PendingInject_WaitsForServiceInit()
    {
        BlockedService.Gate = new TaskCompletionSource();
        var root = Context.Create();
        var callback = 0;
        root.Inject(["foo"], (_, _) => { callback++; return null; });
        Assert.Equal(0, callback);

        var service = root.Plugin(typeof(BlockedService));
        await Task.Yield(); await Task.Yield();
        Assert.Equal(0, callback); // blocked by Service.init

        BlockedService.Gate.SetResult();
        await TestHelpers.WaitUntil(() => callback == 1);
        await service.DisposeAsync();
    }

    private sealed class BlockedService(Context ctx) : Service(ctx, "foo")
    {
        public static TaskCompletionSource Gate = new();
        protected override object Init() => Gate.Task;
    }

    private static int FooInitCount;
    private static int BarInitCount;
    private static int QuxInitCount;

    [Fact]
    public async Task MultipleInjects_LoadInDependencyOrder()
    {
        FooInitCount = 0;
        BarInitCount = 0;
        QuxInitCount = 0;
        var root = Context.Create();

        var foo = root.Plugin(typeof(FooSvc));
        var bar = root.Plugin(typeof(BarSvc));
        var qux = root.Plugin(typeof(QuxSvc));

        await TestHelpers.WaitUntil(() => FooInitCount == 1 && BarInitCount == 1 && QuxInitCount == 1);
        Assert.Equal(1, FooInitCount);
        Assert.Equal(1, BarInitCount);
        Assert.Equal(1, QuxInitCount);
        await foo.DisposeAsync();
        await bar.DisposeAsync();
        await qux.DisposeAsync();
    }

    [Inject("qux")]
    private sealed class FooSvc(Context ctx) : Service(ctx, "foo")
    {
        protected override object? Init() { FooInitCount++; return null; }
    }

    [Inject("foo")]
    [Inject("qux")]
    private sealed class BarSvc(Context ctx) : Service(ctx, "bar")
    {
        protected override object? Init() { BarInitCount++; return null; }
    }

    private sealed class QuxSvc(Context ctx) : Service(ctx, "qux")
    {
        protected override object? Init() { QuxInitCount++; return null; }
    }

    [Fact]
    public async Task HookSnapshot_CompareBeforeAfter()
    {
        var root = Context.Create();
        Func<Context, Task> outer = async ctx =>
        {
            ctx.On(TestEvents.Custom, (_, _) => null);
            await ctx.Plugin((Func<Context, Task>)(ctx =>
            {
                ctx.On(TestEvents.Custom, (_, _) => null);
                return Task.CompletedTask;
            }));
        };

        var before = TestHelpers.HookSnapshot(root);
        await root.Plugin(outer);
        var after = TestHelpers.HookSnapshot(root);

        Assert.True(await root.RegistryDeleteAsync(outer));
        Assert.True(HookSnapshotEqual(before, TestHelpers.HookSnapshot(root)));
        await root.Plugin(outer);
        Assert.True(HookSnapshotEqual(after, TestHelpers.HookSnapshot(root)));
    }

    private static bool HookSnapshotEqual(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    [Fact]
    public async Task Service_ProvideAndGet()
    {
        var root = Context.Create();
        var dispose = root.Provide("counter", 42);
        Assert.Equal(42, root.Get("counter"));
        root.Set("counter", 43);
        Assert.Equal(43, root.Get("counter"));
        dispose.Dispose();
        Assert.Null(root.Get("counter"));
    }

    [Fact]
    public void Provide_Twice_Throws()
    {
        var root = Context.Create();
        root.Provide("foo");
        Assert.Throws<CordisException>(() => root.Provide("foo"));
    }
}
using Xunit;
using static CordiSharp.Tests.TestHelpers;

namespace CordiSharp.Tests;

public class EventsTests
{
    [Fact]
    public void On_Dispose_Unregisters()
    {
        var root = Context.Create();
        var count = 0;
        var dispose = root.On(TestEvents.Custom, (_, _) => { count++; return null; });
        root.Emit(TestEvents.Custom, null);
        root.Emit(TestEvents.Custom, null);
        Assert.Equal(2, count);
        dispose.Dispose();
        root.Emit(TestEvents.Custom, null);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Once_RunsOnce()
    {
        var root = Context.Create();
        var count = 0;
        var dispose = root.Once(TestEvents.Custom, (_, _) => { count++; return null; });
        root.Emit(TestEvents.Custom, null);
        root.Emit(TestEvents.Custom, null);
        Assert.Equal(1, count);
        dispose.Dispose();
        root.Emit(TestEvents.Custom, null);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Parallel_AggregatesErrors()
    {
        var root = Context.Create();
        var settled = false;
        var d1 = root.OnAsync(TestEvents.Custom, async (_, _) =>
        {
            await Task.Delay(50);
            settled = true;
            throw new Exception("async");
        });
        var d2 = root.On(TestEvents.Custom, (_, _) => throw new Exception("test"));

        var error = await Assert.ThrowsAsync<AggregateException>(() => root.Parallel(TestEvents.Custom, null));
        Assert.Equal(["async", "test"], error.InnerExceptions.Select(e => e.Message).OrderBy(x => x));
        Assert.True(settled);
        d1.Dispose();
        d2.Dispose();
    }

    [Fact]
    public void Emit_PropagatesSyncError()
    {
        var root = Context.Create();
        root.On(TestEvents.Custom, (_, _) => throw new Exception("test"));
        Assert.Throws<Exception>(() => root.Emit(TestEvents.Custom, null));
    }

    [Fact]
    public async Task Serial_StopsAtFirstBailed()
    {
        var root = Context.Create();
        var second = 0;
        root.On(TestEvents.Custom, (_, _) => "a");
        root.On(TestEvents.Custom, (_, _) => { second++; return null; });
        var result = await root.Serial(TestEvents.Custom, null);
        Assert.Equal("a", result);
        Assert.Equal(0, second);
    }

    [Fact]
    public void Bail_StopsAtFirstBailed()
    {
        var root = Context.Create();
        var second = 0;
        root.On(TestEvents.Custom, (_, _) => "a");
        root.On(TestEvents.Custom, (_, _) => { second++; return null; });
        var result = root.Bail(TestEvents.Custom, null);
        Assert.Equal("a", result);
        Assert.Equal(0, second);
    }

    [Fact]
    public void Waterfall_ChainsHandlers()
    {
        var root = Context.Create();
        var cb1 = new WaterfallFn((value, next) => value + (int)next()!);
        var cb2 = new WaterfallFn((value, next) => value + (int)next()!);
        root.OnWaterfall(TestEvents.Waterfall, cb1.Invoke);
        root.OnWaterfall(TestEvents.Waterfall, cb2.Invoke);
        Assert.Equal(4, root.Waterfall(TestEvents.Waterfall, 1, () => 2));

        // a handler that does not call next short-circuits the rest
        var cb3 = new WaterfallFn((value, _) => value);
        var cb4 = new WaterfallFn((value, next) => value + (int)next()!);
        root.OnWaterfall(TestEvents.Waterfall, cb3.Invoke);
        root.OnWaterfall(TestEvents.Waterfall, cb4.Invoke);
        Assert.Equal(3, root.Waterfall(TestEvents.Waterfall, 1, () => 2));
        Assert.Equal(0, cb4.Calls); // cb3 short-circuits before cb4
    }

    private sealed class WaterfallFn(Func<int, Func<object?>, object?> impl)
    {
        public int Calls { get; private set; }
        public object? Invoke(int value, Func<object?> next) { Calls++; return impl(value, next); }
    }

    [Fact]
    public void Emit_WithThisArg_FiltersByContext()
    {
        var root = Context.Create();
        var extended = root.Extend(new Dictionary<string, object?> { ["filter"] = (Func<object?, bool>)(s => ((Session)s!).Flag) });
        var outer = 0;
        var inner = 0;
        root.On(TestEvents.Custom, (_, _) => { outer++; return null; });
        extended.On(TestEvents.Custom, (_, _) => { inner++; return null; });

        root.Emit(new Session(false), TestEvents.Custom, null);
        Assert.Equal(1, outer);
        Assert.Equal(0, inner);
        root.Emit(new Session(true), TestEvents.Custom, null);
        Assert.Equal(2, outer); // root hook has no filter and runs on both emits
        Assert.Equal(1, inner);
    }
}
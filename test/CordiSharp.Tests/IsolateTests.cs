using Xunit;

namespace CordiSharp.Tests;

public class IsolateTests
{
    [Fact]
    public async Task IsolatedContext()
    {
        var root = Context.Create();
        var loaded = 0;
        var unloaded = 0;
        Func<Context, object?> plugin = _ => { loaded++; return () => unloaded++; };

        var h0 = root.Inject(["foo"], (ctx, _) => plugin(ctx));
        var ctx1 = root.Isolate("foo");
        var h1 = ctx1.Inject(["foo"], (ctx, _) => plugin(ctx));
        var ctx2 = root.Isolate("foo");
        var h2 = ctx2.Inject(["foo"], (ctx, _) => plugin(ctx));

        var dispose0 = root.Provide("foo", new Dictionary<string, object?> { ["bar"] = 100 });
        Assert.Equal(100, (int)((Dictionary<string, object?>)root.Get("foo")!)["bar"]);
        Assert.Null(ctx1.Get("foo"));
        Assert.Null(ctx2.Get("foo"));
        await h0.Await();
        Assert.Equal(1, loaded);
        Assert.Equal(0, unloaded);

        var dispose1 = ctx1.Provide("foo", new Dictionary<string, object?> { ["bar"] = 200 });
        Assert.Equal(200, (int)((Dictionary<string, object?>)ctx1.Get("foo")!)["bar"]);
        Assert.Null(ctx2.Get("foo"));
        await h1.Await();
        Assert.Equal(2, loaded);

        dispose0.Dispose();
        Assert.Null(root.Get("foo"));
        Assert.Equal(200, (int)((Dictionary<string, object?>)ctx1.Get("foo")!)["bar"]);
        await TestHelpers.WaitUntil(() => unloaded == 1);
        Assert.Equal(2, loaded);

        var dispose2 = ctx2.Provide("foo", new Dictionary<string, object?> { ["bar"] = 300 });
        await h2.Await();
        Assert.Equal(3, loaded);
        Assert.Equal(1, unloaded);
        dispose1.Dispose();
        dispose2.Dispose();
    }

    [Fact]
    public async Task SharedLabel()
    {
        var root = Context.Create();
        var loaded = 0;
        var unloaded = 0;
        Func<Context, object?> plugin = _ => { loaded++; return () => unloaded++; };

        var label = new IsolateToken("test");
        var h0 = root.Inject(["foo"], (ctx, _) => plugin(ctx));
        var ctx1 = root.Isolate("foo", label);
        var h1 = ctx1.Inject(["foo"], (ctx, _) => plugin(ctx));
        var ctx2 = root.Isolate("foo", label);
        var h2 = ctx2.Inject(["foo"], (ctx, _) => plugin(ctx));
        Assert.Equal(0, loaded);

        var dispose0 = root.Provide("foo", new Dictionary<string, object?> { ["bar"] = 100 });
        await h0.Await();
        Assert.Equal(1, loaded);

        var dispose12 = ctx1.Provide("foo", new Dictionary<string, object?> { ["bar"] = 200 });
        Assert.Equal(200, (int)((Dictionary<string, object?>)ctx1.Get("foo")!)["bar"]);
        Assert.Equal(200, (int)((Dictionary<string, object?>)ctx2.Get("foo")!)["bar"]);
        await h1.Await();
        await h2.Await();
        Assert.Equal(3, loaded); // root + ctx1 + ctx2 (shared label -> same token)

        dispose12.Dispose();
        Assert.Null(ctx1.Get("foo"));
        Assert.Null(ctx2.Get("foo"));
        await TestHelpers.WaitUntil(() => unloaded == 2);
        dispose0.Dispose();
    }

    [Fact]
    public async Task IsolatedEvent_ServiceThisArg()
    {
        var root = Context.Create();
        var ctx = root.Isolate("foo");
        var outer = 0;
        var inner = 0;
        root.On(TestEvents.Custom, (_, _) => { outer++; return null; });
        ctx.On(TestEvents.Custom, (_, _) => { inner++; return null; });
        await ctx.Plugin(typeof(EmittingService));

        Assert.Equal(0, outer);
        Assert.Equal(1, inner);
    }

    private sealed class EmittingService : Service
    {
        public EmittingService(Context ctx) : base(ctx, "foo")
        {
            ctx.Emit(this, TestEvents.Custom, null);
        }
    }
}

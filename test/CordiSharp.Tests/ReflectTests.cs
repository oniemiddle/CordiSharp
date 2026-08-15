using Xunit;

namespace CordiSharp.Tests;

public class ReflectTests
{
    [Fact]
    public async Task AccessCheck()
    {
        var root = Context.Create();
        await root.Plugin((Func<Context, Task>)(ctx =>
        {
            Assert.Throws<ServiceResolutionException>(() => ctx.Get("bar"));
            Assert.Throws<ServiceResolutionException>(() => ctx.Set("bar", 0));
            return Task.CompletedTask;
        }));
        await root.Plugin((Func<Context, Task>)(ctx =>
        {
            Assert.Throws<ServiceResolutionException>(() => ctx.Set("foo", 0));
            ctx.Provide("foo");
            Assert.Throws<CordisException>(() => ctx.Provide("foo"));
            ctx.Set("foo", 0);
            return Task.CompletedTask;
        }));
    }

    [Fact]
    public async Task ServiceInjection()
    {
        var root = Context.Create();
        var count = 0;
        root.Mixin("foo", ["bar"]);
        root.Provide("foo");
        root.Set("foo", new Dictionary<string, object?> { ["bar"] = 1 });

        Assert.NotNull(root.Get("foo"));
        Assert.Equal(1, root.Get("bar")); // mixin accessor forwards to foo.bar
        Assert.Null(root.Get("root")); // 'root' is a real property, not a service

        await root.Inject(["foo"], (_, _) =>
        {
            count++;
            return null;
        });
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ServiceInjectLeak()
    {
        var root = Context.Create();
        root.Provide("foo");
        root.Set("foo", new Dictionary<string, object?> { ["bar"] = 1 });
        var fiber = await root.Inject(["foo"], (_, _) => null);
        await fiber.Await();
        Assert.NotNull(fiber.Ctx.Get("foo"));
        await fiber.DisposeAsync();
        Assert.Throws<ServiceResolutionException>(() => fiber.Ctx.Get("foo"));
    }

    [Fact]
    public async Task Extend_MetaProperties()
    {
        var root = Context.Create();
        var extended = root.Extend(new Dictionary<string, object?> { ["baz"] = 2 });
        await extended.Plugin((Func<Context, Task>)(ctx =>
        {
            Assert.Equal(2, ctx.Get("baz"));
            return Task.CompletedTask;
        }));
    }

    [Fact]
    public void Set_WithoutProvide_Throws()
    {
        var root = Context.Create();
        Assert.Throws<ServiceResolutionException>(() => root.Set("nope", 1));
    }
}
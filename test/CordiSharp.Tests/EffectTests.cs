using Xunit;

namespace CordiSharp.Tests;

public class EffectTests
{
    [Fact]
    public async Task Dispose_ByPlugin()
    {
        var root = Context.Create();
        var disposed = 0;
        var fiber = await root.Plugin(ctx =>
        {
            ctx.Effect(() => () => disposed++, "test");
            return null;
        });
        Assert.Equal(0, disposed);
        await fiber.DisposeAsync();
        Assert.Equal(1, disposed);
        await fiber.DisposeAsync();
        Assert.Equal(1, disposed);
    }

    [Fact]
    public void Dispose_Manually()
    {
        var root = Context.Create();
        var disposed = 0;
        var effect = root.Effect(() => () => disposed++);
        Assert.Equal(0, disposed);
        effect.Dispose();
        Assert.Equal(1, disposed);
        effect.Dispose();
        Assert.Equal(1, disposed);
    }

    [Fact]
    public void Yield_Dispose_ReverseOrder()
    {
        var root = Context.Create();
        var seq = new List<int>();

        var effect = root.Effect(Outer);
        Assert.Empty(seq);
        effect.Dispose();
        Assert.Equal(new[] { 3, 2, 1 }, seq);
        effect.Dispose();
        Assert.Equal(new[] { 3, 2, 1 }, seq);
        return;

        IEnumerable<object?> Outer()
        {
            yield return (Action)(() => seq.Add(1));
            yield return root.On(TestEvents.Custom, (_, _) => null);
            yield return (Action)(() => seq.Add(2));
            yield return root.Effect(Inner);
        }

        IEnumerable<object?> Inner()
        {
            yield return root.On(TestEvents.Custom, (_, _) => null);
            yield return (Action)(() => seq.Add(3));
        }
    }

    [Fact]
    public async Task AsyncReturn_WaitsSetupThenDisposes()
    {
        var root = Context.Create();
        var seq = new List<int>();
        var gate = new TaskCompletionSource();
        var dispose = root.Effect(AsyncSetup);
        Assert.Empty(seq);
        gate.SetResult();
        await dispose.AwaitDisposed();
        Assert.Equal(new[] { 1, 2 }, seq);
        return;

        async Task<Action?> AsyncSetup()
        {
            await gate.Task;
            seq.Add(1);
            return () => seq.Add(2);
        }
    }

    [Fact]
    public async Task AsyncReturn_DisposeBeforeSetupCompletes()
    {
        var root = Context.Create();
        var seq = new List<int>();
        var gate = new TaskCompletionSource();
        var dispose = root.Effect(AsyncSetup2);
        dispose.Dispose();
        Assert.Empty(seq);
        gate.SetResult();
        await TestHelpers.WaitUntil(() => seq.SequenceEqual([1, 2]));
        return;

        async Task<Action?> AsyncSetup2()
        {
            await gate.Task;
            seq.Add(1);
            return () => seq.Add(2);
        }
    }

    [Fact]
    public async Task AsyncReturn_WithError_PropagatesOnAwait()
    {
        var root = Context.Create();
        var dispose = root.Effect(() => Task.FromException<Action>(new Exception("test")));
        await Assert.ThrowsAsync<Exception>(dispose.AwaitDisposed);
    }

    [Fact]
    public async Task AsyncYield_CollectsDisposersOverTime()
    {
        var root = Context.Create();
        var seq = new List<int>();
        var gate1 = new TaskCompletionSource();
        var gate2 = new TaskCompletionSource();
        var gate3 = new TaskCompletionSource();
        var dispose = root.Effect(Gen);
        gate1.SetResult();
        await TestHelpers.WaitUntil(() => seq.Contains(1));
        gate2.SetResult();
        await TestHelpers.WaitUntil(() => seq.Contains(3));
        gate3.SetResult();
        await TestHelpers.WaitUntil(() => seq.Contains(5));
        await dispose.AwaitDisposed();
        Assert.Equal(new[] { 1, 3, 5, 6, 4, 2 }, seq);
        return;

        async IAsyncEnumerable<object?> Gen()
        {
            await gate1.Task;
            seq.Add(1);
            yield return (Action)(() => seq.Add(2));
            await gate2.Task;
            seq.Add(3);
            yield return (Action)(() => seq.Add(4));
            await gate3.Task;
            seq.Add(5);
            yield return (Action)(() => seq.Add(6));
        }
    }

    [Fact]
    public async Task AsyncYield_AbortedMidFlight()
    {
        var root = Context.Create();
        var seq = new List<int>();
        var gate1 = new TaskCompletionSource();
        var gate2 = new TaskCompletionSource();
        var d2Collected = new TaskCompletionSource();
        async IAsyncEnumerable<object?> Gen()
        {
            await gate1.Task;
            seq.Add(1);
            yield return (Action)(() => seq.Add(2));
            d2Collected.SetResult(); // the generator resumed past the yield: d2 is collected
            await gate2.Task;
            seq.Add(3);
            yield return (Action)(() => seq.Add(4));
        }
        var dispose = root.Effect(Gen);
        gate1.SetResult();
        await d2Collected.Task; // deterministic: d2 collected, generator suspended at gate2
        dispose.Dispose(); // abort while generator is suspended on gate2
        gate2.SetResult();
        await TestHelpers.WaitUntil(() => seq.SequenceEqual([1, 3, 4, 2]));
    }

    [Fact]
    public void SyncYield_WithError_DisposesCollected()
    {
        var root = Context.Create();
        var seq = new List<int>();
        Assert.Throws<Exception>(() => root.Effect(Gen));
        Assert.Equal(new[] { 1 }, seq);
        return;

        IEnumerable<object?> Gen()
        {
            yield return (Action)(() => seq.Add(1));
            throw new Exception("test");
        }
    }
}
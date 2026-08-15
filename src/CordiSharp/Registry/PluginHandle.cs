using System.Runtime.CompilerServices;

namespace CordiSharp.Registry;

/// <summary>A handle to a plugin fiber: awaitable, disposable, restartable.
/// Mirrors the promise-like fiber wrapper of cordis <c>ctx.plugin()</c>.</summary>
public sealed class PluginHandle
{
    private readonly Fiber _fiber;

    internal PluginHandle(Fiber fiber) => _fiber = fiber;

    /// <summary>The underlying fiber.</summary>
    public Fiber Fiber => _fiber;

    public FiberState State => _fiber.State;

    public object? Config => _fiber.Config;

    public Context Ctx => _fiber.Ctx;

    /// <summary>Awaits the fiber until it settles; throws if loading failed.</summary>
    public Task<Fiber> Await() => _fiber.Await();

    public TaskAwaiter<Fiber> GetAwaiter() => Await().GetAwaiter();

    /// <summary>Updates the plugin config and restarts the fiber.</summary>
    public void Update(object? config, bool noSave = false) => _fiber.Update(config, noSave);

    /// <summary>Restarts the fiber (unload + reload).</summary>
    public Task Restart() => _fiber.Restart();

    /// <summary>Unloads and disposes the plugin fiber.</summary>
    public ValueTask DisposeAsync() => _fiber.DisposePluginAsync();

    public void Dispose() => _ = _fiber.DisposePluginAsync();

    public override string ToString() => $"PluginHandle <{_fiber.Name}>";
}
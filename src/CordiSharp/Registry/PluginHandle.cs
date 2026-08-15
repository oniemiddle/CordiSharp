using System.Runtime.CompilerServices;

namespace CordiSharp.Registry;

/// <summary>A handle to a plugin fiber: awaitable, disposable, restartable.
/// Mirrors the promise-like fiber wrapper of cordis <c>ctx.plugin()</c>.</summary>
public sealed class PluginHandle
{
    internal PluginHandle(Fiber fiber) => Fiber = fiber;

    /// <summary>The underlying fiber.</summary>
    public Fiber Fiber { get; }

    public FiberState State => Fiber.State;

    public object? Config => Fiber.Config;

    public Context Ctx => Fiber.Ctx;

    /// <summary>Awaits the fiber until it settles; throws if loading failed.</summary>
    public Task<Fiber> Await() => Fiber.Await();

    public TaskAwaiter<Fiber> GetAwaiter() => Await().GetAwaiter();

    /// <summary>Updates the plugin config and restarts the fiber.</summary>
    public void Update(object? config, bool noSave = false) => Fiber.Update(config, noSave);

    /// <summary>Restarts the fiber (unload + reload).</summary>
    public Task Restart() => Fiber.Restart();

    /// <summary>Unloads and disposes the plugin fiber.</summary>
    public ValueTask DisposeAsync() => Fiber.DisposePluginAsync();

    public void Dispose() => _ = Fiber.DisposePluginAsync();

    public override string ToString() => $"PluginHandle <{Fiber.Name}>";
}
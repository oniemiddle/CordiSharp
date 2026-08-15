using System.Runtime.CompilerServices;

namespace CordiSharp.Loading;

/// <summary>A handle to a plugin fiber created from an external assembly. When the owning
/// <see cref="AssemblyPluginSet"/> is unloaded the handle detaches itself: its internal fiber
/// reference is nulled, so a retained handle does not prevent the assembly from being
/// collected. Any operation on a detached handle throws <see cref="ObjectDisposedException"/>.</summary>
public sealed class AssemblyPluginHandle
{
    private Fiber? _fiber;

    internal AssemblyPluginHandle(Fiber fiber, AssemblyPluginSet set)
    {
        _fiber = fiber;
        set.Handles.Add(this);
    }

    /// <summary>True once the owning assembly has been unloaded (the handle is inert).</summary>
    public bool IsUnloaded => _fiber is null;

    public FiberState State => _fiber?.State ?? FiberState.Disposed;

    /// <summary>The current (validated) config. Retaining it after unload pins the assembly.</summary>
    public object? Config => _fiber?.Config;

    /// <summary>The context owned by the plugin fiber (null after unload).</summary>
    public Context? Ctx => _fiber?.Ctx;

    /// <summary>Awaits the fiber until it settles; throws if loading failed.</summary>
    public Task<Fiber> Await() => RequireFiber().Await();

    public TaskAwaiter<Fiber> GetAwaiter() => Await().GetAwaiter();

    /// <summary>Updates the plugin config and restarts the fiber.</summary>
    public void Update(object? config, bool noSave = false) => RequireFiber().Update(config, noSave);

    /// <summary>Restarts the fiber (unload + reload).</summary>
    public Task Restart() => RequireFiber().Restart();

    /// <summary>Unloads this plugin fiber (does not unload the whole assembly).</summary>
    public ValueTask DisposeAsync()
    {
        var fiber = _fiber;
        _fiber = null;
        return fiber?.DisposePluginAsync() ?? default;
    }

    public void Dispose() => _ = DisposeAsync();

    internal void Detach() => _fiber = null;

    private Fiber RequireFiber()
        => _fiber ?? throw new ObjectDisposedException(nameof(AssemblyPluginHandle),
            "the owning assembly has been unloaded");

    public override string ToString() => $"AssemblyPluginHandle <{(_fiber?.Name ?? "unloaded")}>";
}

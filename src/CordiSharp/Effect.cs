using System.Runtime.CompilerServices;
using CordiSharp.Logger;

namespace CordiSharp;

/// <summary>Metadata describing an effect (label + nested children) for diagnostics.</summary>
public sealed class EffectMeta(string label)
{
    public string Label { get; } = label;
    public List<EffectMeta> Children { get; } = [];

    public override string ToString() => Label;
}

/// <summary>A disposer collected by an effect: synchronous or asynchronous.</summary>
public readonly struct Disposer
{
    private readonly Action? _sync;
    private readonly Func<ValueTask>? _async;

    public Disposer(Action sync) { _sync = sync; _async = null; }
    public Disposer(Func<ValueTask> async) { _sync = null; _async = async; }
    public Disposer(Func<Task> async) { _sync = null; _async = () => new ValueTask(async()); }

    public bool IsAsync => _async is not null;

    public static Disposer From(Action sync) => new(sync);
    public static Disposer From(Func<ValueTask> async) => new(async);
    public static Disposer From(Func<Task> async) => new(async);

    public ValueTask Invoke()
    {
        if (_async is not null) return _async();
        _sync!.Invoke();
        return default;
    }
}

/// <summary>A handle to a registered effect. Disposing it runs the collected disposers
/// (in reverse order, awaiting async disposers) and is idempotent.</summary>
public interface IEffect : IDisposable, IAsyncDisposable
{
    string Label { get; }
    IReadOnlyList<EffectMeta> Children { get; }

    /// <summary>Await setup completion (for async effects) then run all disposers.</summary>
    Task AwaitDisposed();
}

internal sealed class EffectHandle : IEffect
{
    private readonly object _lock = new();
    private readonly List<Disposer> _disposers = [];
    private readonly Action<EffectHandle>? _onDisposed;  // fiber bookkeeping
    private Task? _setupTask;                            // pending async setup
    private bool _disposed;
    private Exception? _setupError;

    public string Label { get; }
    public List<EffectMeta> Children { get; } = [];
    IReadOnlyList<EffectMeta> IEffect.Children => Children;

    internal EffectMeta? Meta { get; }

    internal EffectHandle(string label, Action<EffectHandle>? onDisposed = null)
    {
        Label = label;
        Meta = new EffectMeta(label);
        _onDisposed = onDisposed;
    }

    internal void Collect(Disposer disposer)
    {
        lock (_lock)
        {
            // collection after disposal is allowed: async generators may still yield
            // disposables before the disposal drain runs (they are picked up then)
            _disposers.Add(disposer);
        }
    }

    internal void CollectChildMeta(EffectMeta child) => Children.Add(child);

    internal void RecordSetupTask(Task task)
    {
        lock (_lock) { _setupTask = task; }
    }

    internal void RecordSetupError(Exception error)
    {
        lock (_lock) { _setupError ??= error; }
    }

    internal bool IsDisposed { get { lock (_lock) return _disposed; } }

    public void Dispose()
    {
        bool start;
        lock (_lock) { start = !_disposed; _disposed = true; }
        if (!start) return;
        _onDisposed?.Invoke(this);
        // fire-and-forget with error containment (setup errors also logged here)
        _ = DisposeCoreAsync();
    }

    public ValueTask DisposeAsync()
    {
        bool start;
        lock (_lock) { start = !_disposed; _disposed = true; }
        if (!start) return default;
        _onDisposed?.Invoke(this);
        return new ValueTask(DisposeCoreAsync());
    }

    public async Task AwaitDisposed()
    {
        bool start;
        lock (_lock) { start = !_disposed; _disposed = true; }
        if (!start) return;
        _onDisposed?.Invoke(this);
        await DisposeCoreAsync();
    }

    public TaskAwaiter GetAwaiter() => AwaitDisposed().GetAwaiter();

    private async Task DisposeCoreAsync()
    {
        // wait for an in-flight async setup first
        Exception? setupError = null;
        Task? setup;
        lock (_lock) setup = _setupTask;
        if (setup is not null)
        {
            try { await setup; }
            catch (Exception error) { setupError = error; }
        }
        lock (_lock)
        {
            setupError ??= _setupError;
        }

        Disposer[] drain;
        lock (_lock)
        {
            drain = _disposers.ToArray();
            _disposers.Clear();
            Array.Reverse(drain);
        }
        foreach (var disposer in drain)
        {
            await disposer.Invoke();
        }

        // a failed async setup surfaces on await (mirrors cordis wrapper.then)
        if (setupError is not null) throw setupError;
    }

    /// <summary>Dispose that never throws; errors are forwarded to the logger (fiber unload path).</summary>
    public async Task RunSafe(LoggerService logger)
    {
        try
        {
            await AwaitDisposed();
        }
        catch (Exception error)
        {
            logger.Error(error);
        }
    }
}
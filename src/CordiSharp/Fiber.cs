using CordiSharp.Events;
using CordiSharp.Internal;
using CordiSharp.Registry;

namespace CordiSharp;

/// <summary>A plugin runtime fiber: one instance of a plugin, with its own context,
/// inject requirements, effect registry and lifecycle state machine.
/// Ports cordis <c>Fiber</c> (packages/core/src/fiber.ts).</summary>
public sealed class Fiber
{
    internal const string Inactive = "__INACTIVE__";

    private readonly Context _parentCtx;
    private readonly Func<object?> _executeRunner;
    private readonly List<object> _updateHooks = [];

    /// <summary>Unique id of this fiber (0 for the root fiber, null after disposal).</summary>
    public int? Uid;
    internal readonly Context Ctx;
    internal object? Config;
    internal FiberState State = FiberState.Pending;
    /// <summary>Load-time snapshot of resolved dependencies (mirrors cordis `fiber.store`);
    /// rebuilt from <see cref="_store"/> on every reload, cleared on unload.</summary>
    internal Dictionary<string, Impl>? Store;
    /// <summary>Dependency resolution cache (mirrors cordis `fiber._store`); survives
    /// unloads so a partial notify can still rebuild a complete epoch signature.</summary>
    private readonly Dictionary<string, Impl> _store = new();
    internal Task? Inertia;
    internal Exception? Error;
    internal string Epoch = Inactive;

    internal readonly DisposableList<EffectHandle> Disposables = new();

    /// <summary>The parent context this fiber was created from.</summary>
    public Context ParentContext => _parentCtx;

    /// <summary>The context owned by this fiber.</summary>
    public Context CtxRef => Ctx;

    /// <summary>The plugin runtime this fiber belongs to (null for the root fiber).</summary>
    public PluginRuntime? Runtime { get; }

    /// <summary>Injected service names (name -&gt; inject config).</summary>
    public IReadOnlyDictionary<string, object?> Inject { get; }

    /// <summary>Fibers of the same runtime (shared registry list).</summary>
    internal readonly DisposableList<Fiber> RuntimeFibers = null!;

    internal IReadOnlyList<object> UpdateHooks => _updateHooks;

    /// <summary>The display name of this fiber (walks up to the nearest named plugin).</summary>
    public string Name
    {
        get
        {
            var fiber = this;
            while (true)
            {
                if (fiber.Runtime?.Name is { Length: > 0 } n) return n;
                if (ReferenceEquals(fiber, fiber._parentCtx.Fiber)) return "root";
                fiber = fiber._parentCtx.Fiber;
            }
        }
    }

    internal Fiber(Context parentCtx, object? config, IReadOnlyDictionary<string, object?> inject, PluginRuntime? runtime)
    {
        _parentCtx = parentCtx;
        Config = config;
        Inject = inject;
        Runtime = runtime;
        _executeRunner = () => runtime?.Callback(Ctx, Config);

        if (runtime is not null)
        {
            RuntimeFibers = runtime.Fibers;
            Uid = parentCtx.Registry.NextCounter();
            Ctx = parentCtx.ExtendForFiber(this);
            // inject intercepts
            if (inject.Count > 0)
            {
                var intercepts = new PropertyMap<object?>(parentCtx.Intercepts);
                foreach (var (name, config2) in inject)
                {
                    if (config2 is not null) intercepts.Set(name, config2);
                }
                Ctx.Intercepts = intercepts;
            }
            parentCtx.Events.EmitRaw(null, parentCtx, InternalEvents.Plugin, [this]);

            foreach (var name in inject.Keys)
            {
                CheckImpl(name);
            }

            // register on the parent fiber: creating + disposing this fiber is an effect
            // of the parent, so the child is unloaded when the parent unloads.
            var handle = parentCtx.Fiber.Effect(() =>
            {
                RuntimeFibers.Add(this);
                try
                {
                    Config = ResolveConfig(runtime, config);
                    Refresh();
                }
                catch (Exception error)
                {
                    LogError(error);
                    Error = error;
                }
                return new Disposer(UnloadAsync);
            }, "ctx.plugin()");
            _parentEffect = (EffectHandle)handle;
        }
        else
        {
            // root fiber
            Uid = 0;
            Ctx = parentCtx;
            State = FiberState.Active;
            Store = new Dictionary<string, Impl>();
            Epoch = "";
        }
    }


    private readonly EffectHandle? _parentEffect;

    internal static object? ResolveConfig(PluginRuntime runtime, object? config)
    {
        return runtime.ConfigSchema is null ? config : runtime.ConfigSchema.Parse(config);
    }

    internal void AssertActive()
    {
        if (Uid is not null) return;
        throw new InactiveEffectException();
    }

    internal void AddUpdateHook(object hook, bool prepend)
    {
        if (prepend) _updateHooks.Insert(0, hook); else _updateHooks.Add(hook);
    }

    internal void RemoveUpdateHook(object hook) => _updateHooks.Remove(hook);

    // ---- effect machinery ----

    /// <summary>Creates an effect on this fiber. The setup delegate may return:
    /// <c>Action</c>, <c>Func&lt;ValueTask&gt;</c>, <c>Func&lt;Task&gt;</c>,
    /// <c>IDisposable</c>/<c>IAsyncDisposable</c>, a <c>Task</c> (async setup),
    /// an <c>IEnumerable</c> (sync generator of disposables) or an
    /// <c>IAsyncEnumerable</c> (async generator of disposables).</summary>
    public IEffect Effect(Func<object?> setup, string? label = null)
    {
        AssertActive();
        var handle = new EffectHandle(label ?? "anonymous", onDisposed: h => Disposables.Remove(h));
        Disposables.Add(handle);
        try
        {
            RunSetup(handle, setup);
        }
        catch
        {
            _ = handle.AwaitDisposed().ContinueWith(t =>
            {
                if (t.IsFaulted) LogError(t.Exception!);
            }, TaskScheduler.Default);
            throw;
        }
        return handle;
    }

    /// <summary>Creates a sync-generator effect: the iterator yields disposables.</summary>
    public IEffect Effect(Func<IEnumerable<object?>> setup, string? label = null)
        => Effect(object? () => setup(), label);

    /// <summary>Creates an async-generator effect: the iterator yields disposables over time.</summary>
    public IEffect Effect(Func<IAsyncEnumerable<object?>> setup, string? label = null)
        => Effect(object? () => setup(), label);

    private void RunSetup(EffectHandle handle, Func<object?> setup)
    {
        var result = setup();
        switch (result)
        {
            case null:
                break;
            case Task task:
                handle.RecordSetupTask(AwaitSetup(handle, task));
                break;
            case IAsyncEnumerable<object?> asyncItems:
                handle.RecordSetupTask(IterateAsyncGenerator(handle, asyncItems));
                break;
            default:
                CollectSync(handle, result);
                break;
        }
    }

    private static async Task AwaitSetup(EffectHandle handle, Task task)
    {
        try
        {
            var result = await UnwrapTaskResult(task);
            if (result is not null) CollectSync(handle, result);
        }
        catch (Exception error)
        {
            handle.RecordSetupError(error);
        }
    }

    private static async Task IterateAsyncGenerator(EffectHandle handle, IAsyncEnumerable<object?> source)
    {
        try
        {
            await foreach (var item in source)
            {
                CollectSync(handle, item);
                if (handle.IsDisposed) return; // stop advancing after disposal
            }
        }
        catch (Exception error)
        {
            handle.RecordSetupError(error);
        }
    }

    private static void CollectSync(EffectHandle handle, object? result)
    {
        switch (result)
        {
            case null:
                return;
            case Disposer disposer:
                handle.Collect(disposer);
                return;
            case Action action:
                handle.Collect(Disposer.From(action));
                return;
            case Func<ValueTask> valueTask:
                handle.Collect(Disposer.From(valueTask));
                return;
            case Func<Task> task:
                handle.Collect(Disposer.From(task));
                return;
            case EffectHandle child:
                handle.Collect(Disposer.From(child.Dispose));
                handle.Children.Add(child.Meta);
                return;
            case IEnumerable<object?> items:
                // NOTE: must come before IDisposable: compiler-generated iterator types
                // implement IEnumerator<T> (IDisposable) as well as IEnumerable<T>
                foreach (var item in items) CollectSync(handle, item);
                return;
            case IAsyncDisposable asyncDisposable:
                handle.Collect(Disposer.From(() => asyncDisposable.DisposeAsync()));
                return;
            case IDisposable disposable:
                handle.Collect(Disposer.From(disposable.Dispose));
                return;
            case Delegate d:
                // any other delegate is treated as a disposer (mirrors cordis functions)
                handle.Collect(Disposer.From(() => d.DynamicInvoke()));
                return;
            default:
                throw new InvalidOperationException("Invalid effect");
        }
    }

    /// <summary>Returns the effect metadata tree of this fiber (for diagnostics).</summary>
    public IReadOnlyList<EffectMeta> GetEffects() => Disposables.Snapshot()
        .Select(d => d.Meta)
        .Where(m => m is not null)
        .Cast<EffectMeta>()
        .ToList();

    private void LogError(object error) => Ctx.LoggerService.Get(Ctx.Name).Error(error);

    private static async Task<object?> UnwrapTaskResult(Task task)
    {
        await task;
        if (task is Task<object?> t) return await t;
        if (task.GetType().IsGenericType)
        {
            // async Task methods compile to Task<VoidTaskResult> on modern .NET
            // (or AsyncStateMachineBox<VoidTaskResult, StateMachine>); their
            // "result" is void, not a disposer
            var args = task.GetType().GetGenericArguments();
            if (args.Length > 0 && args[0].Name == "VoidTaskResult") return null;
            var resultProp = task.GetType().GetProperty("Result");
            if (resultProp is not null) return resultProp.GetValue(task);
        }
        return null;
    }

    // ---- state machine (ports cordis _getState/_updateState/_setEpoch/_refresh/_checkImpl) ----

    internal FiberState GetState()
    {
        if (Uid is null) return FiberState.Disposed;
        if (Error is not null) return FiberState.Failed;
        return Epoch != Inactive ? FiberState.Active : FiberState.Pending;
    }

    private void UpdateState(Func<FiberState?> callback)
    {
        var oldState = State;
        State = callback() ?? GetState();
        if (oldState == State) return;
        Ctx.Events.EmitRaw(null, Ctx, InternalEvents.Status, [this, oldState]);

        if (oldState != FiberState.Active && State != FiberState.Active) return;
        var names = Ctx.Reflect.Store.Keys
            .Where(k => Ctx.Reflect.Store[k].Fiber == this)
            .Select(k => Ctx.Reflect.Store[k].Name)
            .ToList();
        if (names.Count > 0) Ctx.Reflect.Notify(Ctx, names);
    }

    internal void CheckImpl(string name)
    {
        var impl = Ctx.Reflect.GetImpl(Ctx, name, strict: true);
        if (impl is null) 
        {
            _store.Remove(name);
            return;
        }
        try
        {
            if (impl.Check is not null && !impl.Check())
            {
                _store.Remove(name);
                return;
            }
        }
        catch (Exception error)
        {
            impl.Fiber.LogError(error);
            _store.Remove(name);
            return;
        }
        _store[name] = impl;
    }

    internal void Refresh()
    {
        var epoch = "";
        foreach (var name in Inject.Keys)
        {
            if (!_store.TryGetValue(name, out var impl))
            {
                epoch = Inactive;
                break;
            }
            epoch += ":" + impl.Fiber.Uid;
        }
        SetEpoch(epoch);
    }

    private void SetEpoch(string epoch)
    {
        var oldEpoch = Epoch;
        if (epoch == oldEpoch) return;
        Epoch = epoch;
        if (Inertia is not null) return;
        UpdateState(() =>
        {
            if (epoch != Inactive && oldEpoch == Inactive)
            {
                Inertia = Reload();
                return FiberState.Loading;
            }
            Inertia = Unload();
            return FiberState.Unloading;
        });
    }

    private async Task Reload()
    {
        Store = new Dictionary<string, Impl>(_store);
        var oldEpoch = Epoch;
        EffectHandle? loadHandle = null;
        if (Runtime is not null)
        {
            // only plugin fibers track a load handle; the root's reload is a no-op
            loadHandle = new EffectHandle("plugin body", onDisposed: h => Disposables.Remove(h));
            Disposables.Add(loadHandle);
        }
        try
        {
            await Task.Yield();
            var result = _executeRunner();
            if (result is Task task)
            {
                var unwrapped = await UnwrapTaskResult(task);
                if (unwrapped is not null && loadHandle is not null) CollectSync(loadHandle, unwrapped);
            }
            else if (result is not null && loadHandle is not null)
            {
                CollectSync(loadHandle, result);
            }
        }
        catch (Exception reason)
        {
            LogError(reason);
            Error = reason;
            Epoch = Inactive;
        }
        UpdateState(() =>
        {
            if (Epoch == oldEpoch)
            {
                Inertia = null;
                return null;
            }
            Inertia = Unload();
            return FiberState.Unloading;
        });
    }

    private async Task Unload()
    {
        // MUST yield first: otherwise a synchronous completion would let the
        // `Inertia = Unload()` assignment overwrite the Inertia=null set by
        // UpdateState below, causing an endless await loop (see cordis fiber.ts,
        // where async functions always return a pending promise).
        await Task.Yield();
        // run disposers sequentially: cordis is single-threaded, and concurrent
        // disposal of hooks would race on the shared hook lists
        var drains = Disposables.DrainReverse();
        foreach (var handle in drains)
        {
            await RunDisposerSafe(handle);
        }
        Store = null;
        UpdateState(() =>
        {
            if (Epoch == Inactive)
            {
                Inertia = null;
                return null;
            }
            Inertia = Reload();
            return FiberState.Loading;
        });
    }

    private async Task RunDisposerSafe(EffectHandle handle)
    {
        try
        {
            await handle.AwaitDisposed();
        }
        catch (Exception reason)
        {
            LogError(reason);
        }
    }

    /// <summary>Awaits this fiber until it settles (no in-flight transitions, no error).</summary>
    public async Task<Fiber> Await()
    {
        while (Inertia is not null)
        {
            var inertia = Inertia;
            await inertia;
        }
        return Error is not null ? throw Error : this;
    }

    /// <summary>Restarts this fiber: unloads and reloads it (used by <c>Update</c>).</summary>
    public async Task Restart()
    {
        var fiber = Ctx.Fiber;
        fiber.AssertActive();
        fiber.SetEpoch(Inactive);
        fiber.Refresh();
        await fiber.Await();
    }

    /// <summary>Updates the config of this fiber and restarts it.</summary>
    public void Update(object? config, bool noSave = false)
    {
        var fiber = Ctx.Fiber;
        fiber.AssertActive();
        if (fiber.Runtime is null) throw new CordisException("cannot update root fiber");
        config = ResolveConfig(fiber.Runtime, config);
        fiber.RunUpdate(config, noSave);
    }

    internal void RunUpdate(object? config, bool noSave)
    {
        var queue = new Queue<Func<object?, bool, Func<object?>, object?>>();
        foreach (var hook in UpdateHooks)
        {
            queue.Enqueue((Func<object?, bool, Func<object?>, object?>)hook);
        }
        foreach (var hook in Ctx.Events.GlobalUpdateHooks)
        {
            queue.Enqueue(hook);
        }
        Func<object?> next = null!;
        next = () =>
        {
            if (queue.Count > 0)
            {
                var hook = queue.Dequeue();
                return hook(config, noSave, next);
            }
            Config = config;
            Error = null;
            return Restart();
        };
        next();
    }

    internal async Task UnloadAsync()
    {
        Uid = null;
        Ctx.Events.EmitRaw(null, Ctx, InternalEvents.Plugin, [this]);
        if (Ctx.Registry.HasRuntime(Runtime))
        {
            RuntimeFibers.Remove(this);
            if (RuntimeFibers.Count == 0)
            {
                Ctx.Registry.RemoveRuntime(Runtime);
            }
        }
        SetEpoch(Inactive);
        while (Inertia is not null)
        {
            var inertia = Inertia;
            await inertia;
        }
    }

    /// <summary>Number of currently registered effect handles (diagnostics).</summary>
    public int DisposableCount => Disposables.Count;

    /// <summary>Disposes this fiber (unloads the plugin). For the root fiber this
    /// unloads and restarts it. Idempotent.</summary>
    public ValueTask DisposePluginAsync() => _parentEffect?.DisposeAsync() ?? new ValueTask(Restart());

    /// <summary>Alias of <see cref="DisposePluginAsync"/>.</summary>
    public ValueTask DisposeAsync() => DisposePluginAsync();


    public override string ToString() => $"Fiber <{Name}>";
}
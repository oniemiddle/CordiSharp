using System.Text.Json;

namespace CordiSharp.Events;

/// <summary>Implemented by objects that can act as an event dispatch thisArg and filter
/// hooks by context (models <c>Context.filter</c> / <c>Service.filter</c>).</summary>
public interface IContextFilter
{
    bool FilterContext(Context ctx);
}

/// <summary>Internal event names (mirrors cordis 'internal/*' events).</summary>
public static class InternalEvents
{
    public const string Plugin = "internal/plugin";
    public const string Status = "internal/status";
    public const string Service = "internal/service";
    public const string Update = "internal/update";
    public const string Get = "internal/get";
    public const string Set = "internal/set";
    public const string Listener = "internal/listener";
    public const string Dispatch = "internal/dispatch";
}

internal delegate object? SyncCallback(object?[] args);
internal delegate Task<object?> AsyncCallback(object?[] args);

internal sealed record Hook(Context Ctx, object Callback, bool IsAsync, bool Global, bool Prepend)
{
    public bool VisibleTo(object? thisArg)
    {
        if (Global || thisArg is null) return true;
        return thisArg is not IContextFilter filter || filter.FilterContext(Ctx);
    }
}

/// <summary>The events service: registration and dispatch of hooks. All hooks are stored
/// globally per root context; dispatch-time thisArg filtering controls visibility.</summary>
public sealed class EventsService
{
    private readonly Dictionary<string, List<Hook>> _hooks = new();

    internal EventsService(Context ctx)
    {
        var ctx1 = ctx;
        On(ctx1, InternalEvents.Listener, (object?[] args) =>
        {
            var name = (string)args[0]!;
            var listener = args[1]!;
            var options = (EventOptions)args[2]!;
            if (name == InternalEvents.Update && !options.Global)
            {
                var fiber = ctx1.Fiber;
                var hook = (Func<object?, bool, Func<object?>, object?>)listener;
                fiber.AddUpdateHook(hook, options.Prepend);
                return new UpdateHookDisposer(fiber, hook);
            }
            return null;
        }, new EventOptions());
    }

    private sealed class UpdateHookDisposer(Fiber fiber, object hook) : IDisposable
    {
        public void Dispose() => fiber.RemoveUpdateHook(hook);
    }

    internal IReadOnlyDictionary<string, List<Hook>> Hooks => _hooks;

    /// <summary>Number of registered hooks per event name (diagnostics).</summary>
    public IReadOnlyDictionary<string, int> HookSnapshot()
    {
        var result = new Dictionary<string, int>();
        foreach (var (name, hooks) in _hooks)
        {
            if (hooks.Count > 0) result[name] = hooks.Count;
        }
        return result;
    }

    internal IReadOnlyList<Hook> ResolveHooks(string name, object? thisArg)
    {
        // snapshot: listeners may unregister (e.g. ctx.once) during dispatch
        if (!_hooks.TryGetValue(name, out var hooks)) return [];
        if (thisArg is null) return hooks.ToList();
        return hooks.Where(h => h.VisibleTo(thisArg)).ToList();
    }

    internal static object? InvokeSync(object callback, object?[] args)
    {
        if (callback is SyncCallback sync) return sync(args);
        var async = (AsyncCallback)callback;
        _ = async(args).ContinueWith(t =>
        {
            if (t.IsFaulted) Console.Error.WriteLine(t.Exception!.GetBaseException());
        }, TaskScheduler.Default);
        return null;
    }

    // ---- registration ----

    /// <summary>Registers a listener on <paramref name="ctx"/>'s fiber. Returns a disposable
    /// that unregisters the listener (also removed when the fiber unloads).</summary>
    public IDisposable On<TArgs>(Context ctx, EventKey<TArgs> key, Func<Context, TArgs, object?> listener, EventOptions? options = null)
        => On(ctx, key.Name, (Func<object?[], object?>)(args => listener(ctx, (TArgs)args[0]!)), options);

    /// <summary>Registers an asynchronous listener (awaited by <c>Parallel</c>/<c>Serial</c>).</summary>
    public IDisposable OnAsync<TArgs>(Context ctx, EventKey<TArgs> key, Func<Context, TArgs, Task<object?>> listener, EventOptions? options = null)
        => On(ctx, key.Name, (Func<object?[], Task<object?>>)(args => listener(ctx, (TArgs)args[0]!)), options, async: true);

    internal IDisposable On(Context ctx, string name, object rawListener, EventOptions? options = null, bool async = false)
    {
        options ??= new EventOptions();
        ctx.Fiber.AssertActive();
        var hooks = GetHooks(name);
        object callback = async
            ? (AsyncCallback)(args => ((Func<object?[], Task<object?>>)rawListener)(args))
            : (SyncCallback)(args => ((Func<object?[], object?>)rawListener)(args));

        // internal/listener interception
        var intercepted = BailRaw(ctx, InternalEvents.Listener, null, [name, callback, options]);
        if (intercepted is IDisposable disposer) return disposer;

        var label = $"ctx.on({JsonSerializer.Serialize(name)})";
        return ctx.Fiber.Effect(() =>
        {
            var hook = new Hook(ctx, callback, async, options.Global, options.Prepend);
            if (options.Prepend) hooks.Insert(0, hook); else hooks.Add(hook);
            return Disposer.From(() => Unregister(hooks, callback));
        }, label);
    }

    /// <summary>Registers a waterfall listener: invoked during Waterfall dispatch with
    /// (args, next) where next() invokes the following listener.</summary>
    public IDisposable OnWaterfall<TArgs>(Context ctx, EventKey<TArgs> key, Func<TArgs, Func<object?>, object?> listener, EventOptions? options = null)
        => On(ctx, key.Name, (Func<object?[], object?>)(args => listener((TArgs)args[0]!, (Func<object?>)args[1]!)), options);

    private readonly List<Func<object?, bool, Func<object?>, object?>> _globalUpdateHooks = [];

    internal IReadOnlyList<Func<object?, bool, Func<object?>, object?>> GlobalUpdateHooks => _globalUpdateHooks;

    /// <summary>Registers an 'internal/update' hook (config interception on fiber update).
    /// Non-global hooks attach to the registering fiber; global hooks run for every fiber.</summary>
    public IDisposable OnUpdateHook(Context ctx, Func<object?, bool, Func<object?>, object?> hook, EventOptions? options = null)
    {
        options ??= new EventOptions();
        if (!options.Global)
        {
            return ctx.Fiber.Effect(() =>
            {
                ctx.Fiber.AddUpdateHook(hook, options.Prepend);
                return Disposer.From(() => ctx.Fiber.RemoveUpdateHook(hook));
            }, "ctx.on(\"internal/update\")");
        }
        return ctx.Fiber.Effect(() =>
        {
            if (options.Prepend) _globalUpdateHooks.Insert(0, hook); else _globalUpdateHooks.Add(hook);
            return Disposer.From(() => _globalUpdateHooks.Remove(hook));
        }, "ctx.on(\"internal/update\")");
    }

    /// <summary>Registers a once-only listener.</summary>
    public IDisposable Once<TArgs>(Context ctx, EventKey<TArgs> key, Func<Context, TArgs, object?> listener, EventOptions? options = null)
    {
        IDisposable? self = null;
        self = On(ctx, key, (c, args) =>
        {
            self?.Dispose();
            return listener(c, args);
        }, options);
        return self;
    }

    internal bool Unregister(List<Hook> hooks, object callback)
    {
        var index = hooks.FindIndex(h => h.Callback == callback);
        if (index >= 0)
        {
            hooks.RemoveAt(index);
            return true;
        }
        return false;
    }

    private List<Hook> GetHooks(string name)
    {
        if (!_hooks.TryGetValue(name, out var hooks))
        {
            hooks = [];
            _hooks[name] = hooks;
        }
        return hooks;
    }

    // ---- dispatch (typed, user-facing) ----

    public void Emit<TArgs>(EventKey<TArgs> key, TArgs args) => Emit(null, key, args);

    public void Emit<TArgs>(object? thisArg, EventKey<TArgs> key, TArgs args)
    {
        foreach (var hook in ResolveHooks(key.Name, thisArg))
        {
            InvokeSync(hook.Callback, [args]);
        }
    }

    public async Task Parallel<TArgs>(EventKey<TArgs> key, TArgs args)
    {
        var hooks = ResolveHooks(key.Name, null);
        var tasks = new List<Task>();
        foreach (var hook in hooks)
        {
            var args2 = new object?[] { args };
            tasks.Add(hook.IsAsync
                ? ((AsyncCallback)hook.Callback)(args2)
                : Task.Run(() => ((SyncCallback)hook.Callback)(args2)));
        }
        var results = await Task.WhenAll(tasks.Select(async t =>
        {
            try { await t; return null; }
            catch (Exception e) { return e; }
        })).ConfigureAwait(false);
        var errors = results.Where(e => e is not null).Cast<Exception>().ToList();
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    public async Task<object?> Serial<TArgs>(EventKey<TArgs> key, TArgs args)
    {
        foreach (var hook in ResolveHooks(key.Name, null))
        {
            object? result;
            if (hook.IsAsync) result = await ((AsyncCallback)hook.Callback)([args]);
            else result = ((SyncCallback)hook.Callback)([args]);
            if (IsBailed(result)) return result;
        }
        return null;
    }

    public object? Bail<TArgs>(EventKey<TArgs> key, TArgs args) => Bail(null, key, args);

    public object? Bail<TArgs>(object? thisArg, EventKey<TArgs> key, TArgs args)
    {
        foreach (var hook in ResolveHooks(key.Name, thisArg))
        {
            var result = InvokeSync(hook.Callback, [args]);
            if (IsBailed(result)) return result;
        }
        return null;
    }

    public object? Waterfall<TArgs>(EventKey<TArgs> key, TArgs args, Func<object?> fallback) => Waterfall(null, key, args, fallback);

    public object? Waterfall<TArgs>(object? thisArg, EventKey<TArgs> key, TArgs args, Func<object?> fallback)
    {
        var callbacks = ResolveHooks(key.Name, thisArg)
            .Where(h => !h.IsAsync)
            .Select(h => (SyncCallback)h.Callback)
            .ToList();
        return RunWaterfall(callbacks, [args], fallback);
    }

    // ---- dispatch (raw, internal) ----

    internal void EmitRaw(Context ctx, string name, object?[] args) => EmitRaw(null, ctx, name, args);

    internal void EmitRaw(object? thisArg, Context ctx, string name, object?[] args)
    {
        foreach (var hook in ResolveHooks(name, thisArg))
        {
            InvokeSync(hook.Callback, args);
        }
    }

    internal object? BailRaw(Context ctx, string name, object? thisArg, object?[] args)
    {
        foreach (var hook in ResolveHooks(name, thisArg))
        {
            var result = InvokeSync(hook.Callback, args);
            if (IsBailed(result)) return result;
        }
        return null;
    }

    internal object? WaterfallRaw(object? thisArg, Context ctx, string name, object?[] args, Func<object?> fallback)
    {
        var callbacks = ResolveHooks(name, thisArg)
            .Where(h => !h.IsAsync)
            .Select(h => (SyncCallback)h.Callback)
            .ToList();
        return RunWaterfall(callbacks, args, fallback);
    }

    private static object? RunWaterfall(List<SyncCallback> callbacks, object?[] args, Func<object?> fallback)
    {
        var queue = new Queue<SyncCallback>(callbacks);
        var inner = args.ToArray();
        var next = () =>
        {
            if (queue.Count > 0)
            {
                var cb = queue.Dequeue();
                return cb(inner);
            }
            return fallback();
        };
        inner = inner.Append(next).ToArray();
        return next();
    }

    private static bool IsBailed(object? value) => value is not null && !Equals(value, false);
}
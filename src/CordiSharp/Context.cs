using CordiSharp.Events;
using CordiSharp.Internal;
using CordiSharp.Logger;
using CordiSharp.Registry;

namespace CordiSharp;

/// <summary>The central object of CordiSharp: owns scopes (extend/isolate/intercept),
/// services, events, plugins and the fiber lifecycle. Ports cordis <c>Context</c>.</summary>
public sealed class Context
{
    internal PropertyMap<IsolateToken> Isolates;
    internal PropertyMap<object?> Intercepts;
    internal Dictionary<string, object?> Extra = new();

    /// <summary>The root context of this context tree.</summary>
    public Context Root { get; }

    /// <summary>The context this context was created from (null for the root).</summary>
    public Context? Parent { get; }

    /// <summary>The fiber that owns this context.</summary>
    public Fiber Fiber { get; internal set; }

    /// <summary>The events service (shared across the whole root tree).</summary>
    public EventsService Events { get; internal set; } = null!;

    /// <summary>The logger service (shared across the whole root tree).</summary>
    public LoggerService LoggerService { get; internal set; } = null!;

    /// <summary>The reflect service (shared across the whole root tree).</summary>
    public ReflectService Reflect { get; internal set; } = null!;

    /// <summary>The registry service (shared across the whole root tree).</summary>
    public RegistryService Registry { get; internal set; } = null!;

    /// <summary>Optional MSDI provider attached to this root (used for plugin construction).</summary>
    public IServiceProvider? ServiceProvider { get; set; }

    /// <summary>Display name of this context (derived from the owning fiber).</summary>
    public string Name => Fiber.Name;

    /// <summary>Optional context filter used when this context acts as an event thisArg.</summary>
    public Func<object?, bool>? Filter { get; set; }

    internal Context(Context? parent, Fiber fiber, PropertyMap<IsolateToken> isolates, PropertyMap<object?> intercepts)
    {
        Parent = parent;
        Root = parent?.Root ?? this;
        Fiber = fiber;
        Isolates = isolates;
        Intercepts = intercepts;
        if (parent is not null)
        {
            // shared services live on the root context; child contexts inherit them
            Events = parent.Events;
            LoggerService = parent.LoggerService;
            Reflect = parent.Reflect;
            Registry = parent.Registry;
            ServiceProvider = parent.ServiceProvider;
        }
    }

    /// <summary>Creates a new root context.</summary>
    public static Context Create()
    {
        var root = new Context(null, null!, new PropertyMap<IsolateToken>(), new PropertyMap<object?>());
        var fiber = new Fiber(root, null, new Dictionary<string, object?>(), null);
        root.Fiber = fiber;
        root.Events = new EventsService(root);
        root.Reflect = new ReflectService(root);
        root.Registry = new RegistryService(root);
        root.LoggerService = new LoggerService(root);
        // mirror cordis: the root fiber's internal effects (service setup) are not
        // tracked for disposal - only user effects count
        fiber.Disposables.Clear();
        return root;
    }

    public override string ToString() => $"Context <{Name}>";

    // ---- scopes ----

    /// <summary>Creates a child context sharing the isolate/intercept scope (mirrors
    /// <c>ctx.extend()</c>).</summary>
    public Context Extend() => new(this, Fiber, Isolates, Intercepts) { Extra = Extra };

    /// <summary>Creates a child context with extra properties (mirrors <c>ctx.extend(meta)</c>).</summary>
    public Context Extend(IReadOnlyDictionary<string, object?> meta)
    {
        var extended = new Context(this, Fiber, Isolates, Intercepts)
        {
            Extra = new Dictionary<string, object?>(Extra),
        };
        foreach (var (key, value) in meta) extended.Extra[key] = value;
        if (meta.TryGetValue("filter", out var filter) && filter is Func<object?, bool> f) extended.Filter = f;
        return extended;
    }

    /// <summary>Creates a child context that isolates the given service name (its provides
    /// are invisible to the parent scope and vice versa).</summary>
    public Context Isolate(string name, IsolateToken? label = null)
    {
        var isolates = new PropertyMap<IsolateToken>(Isolates);
        isolates.Set(name, label ?? new IsolateToken(name));
        return new Context(this, Fiber, isolates, Intercepts) { Extra = Extra };
    }

    /// <summary>Creates a child context with an intercepted config for a service.</summary>
    public Context Intercept(string name, object? config)
    {
        var intercepts = new PropertyMap<object?>(Intercepts);
        intercepts.Set(name, config);
        return new Context(this, Fiber, Isolates, intercepts) { Extra = Extra };
    }

    internal Context ExtendForFiber(Fiber fiber) => new(this, fiber, Isolates, Intercepts) { Extra = Extra };

    // ---- services ----

    public object? this[string name]
    {
        get => Get(name);
        set => Set(name, value);
    }

    /// <summary>Resolves a service (or extra property). Throws when unresolvable in a
    /// plugin context without a matching provide/inject.</summary>
    public object? Get(string name, bool strict = true)
    {
        if (Extra.TryGetValue(name, out var extra)) return extra;
        if (Reflect.Props.TryGetValue(name, out var def) && def is { Type: "accessor", Get: not null })
        {
            return def.Get(this);
        }
        return Fiber.Runtime is null ? Reflect.Get(this, name, strict: false) : GetFromFiberChain(name);
    }

    public T? Get<T>(string name, bool strict = true) => (T?)Get(name, strict);

    /// <summary>Updates the value of an existing service.</summary>
    public void Set(string name, object? value) => Reflect.Set(this, name, value);

    public void Set<T>(string name, T value) => Reflect.Set(this, name, value);

    /// <summary>Registers a service implementation on this context's fiber.</summary>
    public IDisposable Provide(string name, object? value = null, Func<bool>? check = null)
        => Reflect.Provide(this, name, value, check);

    public IDisposable Provide<T>(string name, T value, Func<bool>? check = null)
        => Reflect.Provide(this, name, value, check);

    /// <summary>Declares an accessor property.</summary>
    public IDisposable Accessor(string name, AccessorOptions options) => Reflect.Accessor(this, name, options);

    /// <summary>Creates accessors forwarding to members of a service.</summary>
    public IDisposable Mixin(string source, IReadOnlyList<string> mixins) => Reflect.Mixin(this, source, mixins);

    private object? GetFromFiberChain(string name)
    {
        // key = the ROOT context's token for this name (mirrors the JS proxy where
        // target[symbols.isolate] is the root context's map)
        var key = Root.Isolates.GetOrCreateRoot(name, () => new IsolateToken(name));
        var fiber = Fiber;
        while (true)
        {
            if (fiber.Store is not null && fiber.Store.TryGetValue(name, out var impl)) return impl.Value;
            if (fiber.Inject.ContainsKey(name))
            {
                throw new ServiceResolutionException($"cannot get required service \"{name}\" in inactive context");
            }
            if (fiber.Runtime is null)
            {
                throw new ServiceResolutionException($"cannot get property \"{name}\" without inject");
            }
            fiber.ParentContext.Isolates.TryGet(name, out var parentKey);
            if (!ReferenceEquals(parentKey, key))
            {
                throw new ServiceResolutionException($"cannot get property \"{name}\" without inject");
            }
            fiber = fiber.ParentContext.Fiber;
        }
    }

    // ---- plugins ----

    /// <summary>Loads a plugin (class, delegate or object with Apply).</summary>
    public PluginHandle Plugin(object plugin, object? config = null) => Registry.Plugin(plugin, config);

    /// <summary>Loads a plugin from a typed callback.</summary>
    public PluginHandle Plugin<TConfig>(Action<Context, TConfig> plugin, TConfig? config = default)
        => Registry.Plugin(plugin, config);

    /// <summary>Loads a plugin from a typed callback that may return a disposer.</summary>
    public PluginHandle Plugin<TConfig>(Func<Context, TConfig, object?> plugin, TConfig? config = default)
        => Registry.Plugin(plugin, config);

    /// <summary>Loads a plugin from a callback without config.</summary>
    public PluginHandle Plugin(Func<Context, object?> plugin) => Registry.Plugin(plugin);

    /// <summary>Loads a plugin from a callback without config.</summary>
    public PluginHandle Plugin(Action<Context> plugin) => Registry.Plugin(plugin);

    /// <summary>Loads a plugin from a callback with declared injected services.</summary>
    public PluginHandle Inject(IEnumerable<string> deps, Func<Context, object?, object?> callback)
        => Registry.Inject(deps, callback);

    /// <summary>Loads a typed plugin.</summary>
    public PluginHandle Plugin<TPlugin, TConfig>(TConfig? config = default) where TPlugin : class, IPlugin<TConfig>, new()
        => Registry.Plugin<TPlugin, TConfig>(config);

    /// <summary>Unregisters a plugin runtime (disposing its fibers).</summary>
    public bool RegistryDelete(object plugin) => Registry.Delete(plugin);

    /// <summary>Unregisters a plugin runtime and awaits the disposal of its fibers.</summary>
    public Task<bool> RegistryDeleteAsync(object plugin) => Registry.DeleteAsync(plugin);

    // ---- effects ----

    public IEffect Effect(Func<object?> setup, string? label = null) => Fiber.Effect(setup, label);

    public IEffect Effect(Func<IEnumerable<object?>> setup, string? label = null) => Fiber.Effect(setup, label);

    public IEffect Effect(Func<IAsyncEnumerable<object?>> setup, string? label = null) => Fiber.Effect(setup, label);

    // ---- events ----

    public IDisposable On<TArgs>(EventKey<TArgs> key, Func<Context, TArgs, object?> listener, EventOptions? options = null)
        => Events.On(this, key, listener, options);

    public IDisposable OnAsync<TArgs>(EventKey<TArgs> key, Func<Context, TArgs, Task<object?>> listener, EventOptions? options = null)
        => Events.OnAsync(this, key, listener, options);

    public IDisposable Once<TArgs>(EventKey<TArgs> key, Func<Context, TArgs, object?> listener, EventOptions? options = null)
        => Events.Once(this, key, listener, options);

    public IDisposable OnWaterfall<TArgs>(EventKey<TArgs> key, Func<TArgs, Func<object?>, object?> listener, EventOptions? options = null)
        => Events.OnWaterfall(this, key, listener, options);

    /// <summary>Registers an 'internal/update' hook (config interception on fiber update).</summary>
    public IDisposable OnUpdate(Func<object?, bool, Func<object?>, object?> hook, EventOptions? options = null)
        => Events.OnUpdateHook(this, hook, options);

    public void Emit<TArgs>(EventKey<TArgs> key, TArgs args) => Events.Emit(key, args);

    public void Emit<TArgs>(object? thisArg, EventKey<TArgs> key, TArgs args) => Events.Emit(thisArg, key, args);

    public Task Parallel<TArgs>(EventKey<TArgs> key, TArgs args) => Events.Parallel(key, args);

    public Task<object?> Serial<TArgs>(EventKey<TArgs> key, TArgs args) => Events.Serial(key, args);

    public object? Bail<TArgs>(EventKey<TArgs> key, TArgs args) => Events.Bail(key, args);

    public object? Waterfall<TArgs>(EventKey<TArgs> key, TArgs args, Func<object?> fallback)
        => Events.Waterfall(key, args, fallback);

    // ---- logging ----

    /// <summary>Gets a logger named after this context (or an explicit name).</summary>
    public Logger.Logger Logger(string? name = null) => LoggerService.Get(name ?? Name);
}
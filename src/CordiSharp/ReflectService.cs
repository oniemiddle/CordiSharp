using System.Reflection;
using System.Text.Json;
using CordiSharp.Events;

namespace CordiSharp;

/// <summary>An implementation of a service stored by <see cref="ReflectService"/>.</summary>
public sealed class Impl
{
    public string Name { get; }
    public Fiber Fiber { get; }
    public object? Value { get; internal set; }
    public Func<bool>? Check { get; }

    internal Impl(string name, Fiber fiber, object? value, Func<bool>? check)
    {
        Name = name;
        Fiber = fiber;
        Value = value;
        Check = check;
    }
}

/// <summary>Options for a declared property accessor.</summary>
public sealed class AccessorOptions
{
    public Func<Context, object?>? Get { get; set; }
    public Func<Context, object?, bool>? Set { get; set; }
}

internal sealed class PropertyDef
{
    public string Type = "service"; // 'service' | 'accessor'
    public Func<Context, object?>? Get;
    public Func<Context, object?, bool>? Set;
}

/// <summary>The reflect service: manages service registration, resolution and
/// notification. Ports cordis <c>ReflectService</c>.</summary>
public sealed class ReflectService
{
    private readonly Context _ctx;
    internal readonly Dictionary<IsolateToken, Impl> Store = new();
    internal readonly Dictionary<string, PropertyDef> Props = new();

    internal ReflectService(Context ctx) => _ctx = ctx;

    /// <summary>Resolves a service value for the given context (strict by default).</summary>
    public object? Get(Context ctx, string name, bool strict = true)
    {
        var impl = GetImpl(ctx, name, strict);
        return impl?.Value;
    }

    internal Impl? GetImpl(Context ctx, string name, bool strict = true)
    {
        if (!ctx.Isolates.TryGet(name, out var key)) return null;
        if (!Store.TryGetValue(key, out var impl)) return null;
        if (strict && impl.Fiber.State != FiberState.Active) return null;
        return impl;
    }

    /// <summary>Updates the value of an existing service.</summary>
    public bool Set(Context ctx, string name, object? value)
    {
        if (!Props.TryGetValue(name, out var def))
        {
            throw new ServiceResolutionException($"cannot set property \"{name}\" without provide");
        }
        if (def.Type == "accessor")
        {
            if (def.Set is null) return false;
            return def.Set(ctx, value);
        }
        // internal/set waterfall
        return (bool)ctx.Events.WaterfallRaw(null, ctx, InternalEvents.Set, [name, value], () =>
        {
            return SetImpl(ctx, name, value);
        })!;
    }

    private bool SetImpl(Context ctx, string name, object? value)
    {
        if (!ctx.Isolates.TryGet(name, out var key)) throw new ServiceResolutionException($"cannot set property \"{name}\" without provide");
        if (!Store.TryGetValue(key, out var impl)) throw new ServiceResolutionException($"cannot set property \"{name}\" without provide");
        if (impl.Fiber != ctx.Fiber) throw new ServiceResolutionException($"cannot set property \"{name}\" in multiple fibers");
        impl.Value = value;
        return true;
    }

    /// <summary>Registers a service implementation on the given context's fiber. Returns a
    /// disposable that unregisters it (and unloads dependent fibers).</summary>
    public IDisposable Provide(Context ctx, string name, object? value = null, Func<bool>? check = null)
    {
        return ctx.Fiber.Effect(() =>
        {
            if (!Props.TryGetValue(name, out var def))
            {
                def = new PropertyDef { Type = "service" };
                Props[name] = def;
            }
            else if (def.Type != "service")
            {
                throw new CordisException($"property \"{name}\" is already declared as {def.Type}");
            }

            var key = ctx.Isolates.GetOrCreateRoot(name, () => new IsolateToken(name));
            if (Store.TryGetValue(key, out var existing))
            {
                throw new CordisException($"service \"{name}\" has been registered at <{existing.Fiber.Name}>");
            }
            var impl = new Impl(name, ctx.Fiber, value, check);
            Store[key] = impl;
            if (ctx.Fiber.Store is null) ctx.Fiber.Store = new Dictionary<string, Impl>();
            ctx.Fiber.Store[name] = impl;
            if (ctx.Fiber.State == FiberState.Active)
            {
                Notify(ctx, [name]);
            }
            return Disposer.From(new Func<Task>(async () =>
            {
                Store.Remove(key);
                var fibers = Notify(ctx, [name]);
                await Task.WhenAll(fibers.Select(f => f.Await()).ToArray());
                ctx.Fiber.Store?.Remove(name);
            }));
        }, $"ctx.provide({JsonSerializer.Serialize(name)})");
    }

    /// <summary>Declares an accessor property (get/set redirection).</summary>
    public IDisposable Accessor(Context ctx, string name, AccessorOptions options)
    {
        return ctx.Fiber.Effect(() =>
        {
            if (Props.ContainsKey(name))
            {
                throw new CordisException($"property \"{name}\" is already declared as {Props[name].Type}");
            }
            Props[name] = new PropertyDef { Type = "accessor", Get = options.Get, Set = options.Set };
            return Disposer.From(() => Props.Remove(name));
        }, $"ctx.accessor({JsonSerializer.Serialize(name)})");
    }

    /// <summary>Creates accessors that forward to properties of a service (mixin).</summary>
    public IDisposable Mixin(Context ctx, string source, IReadOnlyList<string> mixins)
    {
        return ctx.Fiber.Effect((Func<object?>)(() =>
        {
            foreach (var key in mixins)
            {
                Accessor(ctx, key, new AccessorOptions
                {
                    Get = _ =>
                    {
                        var service = ctx.Get(source, strict: false);
                        if (service is null) return null;
                        return GetMember(service, key);
                    },
                    Set = (_, value) =>
                    {
                        var service = ctx.Get(source, strict: false);
                        if (service is null) return false;
                        return SetMember(service, key, value);
                    },
                });
            }
            return null;
        }), $"ctx.mixin({JsonSerializer.Serialize(source)})");
    }

    internal static object? GetMember(object target, string name)
    {
        if (target is IDictionary<string, object?> dict)
        {
            return dict.TryGetValue(name, out var value) ? value : null;
        }
        var type = target.GetType();
        var prop = type.GetProperty(name) ?? type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop is not null && prop.CanRead) return prop.GetValue(target);
        var field = type.GetField(name);
        if (field is not null) return field.GetValue(target);
        var method = type.GetMethod(name, Type.EmptyTypes);
        if (method is not null) return method.CreateDelegate(typeof(Action), target);
        throw new CordisException($"cannot get member \"{name}\" from {type.Name}");
    }

    internal static bool SetMember(object target, string name, object? value)
    {
        if (target is IDictionary<string, object?> dict)
        {
            dict[name] = value;
            return true;
        }
        var type = target.GetType();
        var prop = type.GetProperty(name);
        if (prop is not null && prop.CanWrite)
        {
            prop.SetValue(target, value);
            return true;
        }
        var field = type.GetField(name);
        if (field is not null)
        {
            field.SetValue(target, value);
            return true;
        }
        return false;
    }

    /// <summary>Notifies fibers that inject any of the given names, refreshing their epochs
    /// (loading/unloading dependent plugins).</summary>
    internal List<Fiber> Notify(Context ctx, IReadOnlyList<string> names)
    {
        var fibers = new List<Fiber>();
        foreach (var runtime in ctx.Registry.Values())
        {
            foreach (var fiber in runtime.Fibers)
            {
                var hasUpdate = false;
                foreach (var name in names)
                {
                    if (!fiber.Inject.ContainsKey(name)) continue;
                    if (!SameIsolate(ctx, fiber.Ctx, name)) continue;
                    hasUpdate = true;
                    fiber.CheckImpl(name);
                }
                if (!hasUpdate) continue;
                fiber.Refresh();
                fibers.Add(fiber);
            }
        }
        foreach (var name in names)
        {
            var filterCtx = new IsolateFilter(ctx, name);
            ctx.Events.EmitRaw(filterCtx, ctx, InternalEvents.Service, [name, GetImpl(ctx, name, strict: false)?.Value]);
        }
        return fibers;
    }

    private static bool SameIsolate(Context providerCtx, Context fiberCtx, string name)
    {
        providerCtx.Isolates.TryGet(name, out var a);
        fiberCtx.Isolates.TryGet(name, out var b);
        return ReferenceEquals(a, b);
    }

    /// <summary>Synthetic context used as an event thisArg to filter by isolate.</summary>
    private sealed class IsolateFilter(Context ctx, string name) : IContextFilter
    {
        public bool FilterContext(Context target)
        {
            ctx.Isolates.TryGet(name, out var a);
            target.Isolates.TryGet(name, out var b);
            return ReferenceEquals(a, b);
        }
    }
}
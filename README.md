# CordiSharp

A C# port of [Cordis](https://github.com/cordiverse/cordis) - the meta-framework of spatiotemporal composability.

> **Docs**: detailed API reference (in Chinese) lives in [`docs/`](docs/README.md).
CordiSharp brings Cordis' context/scope-based plugin system, dependency-injected services, typed events,
and fiber lifecycle management to .NET, with first-class integration for
Microsoft.Extensions.DependencyInjection, a Roslyn source generator and Roslyn analyzers.

> **Status**: faithful behavioral port of cordis v4 (`packages/core`). The core semantics
> (fiber state machine, inject/isolate/intercept, effect lifecycle, event dispatch modes) are
> ported directly from the TypeScript source and covered by a ported test suite.

## Packages

The runtime library ships in `CordiSharp` together with the source generator and analyzers
(all DLLs are packed into the one nupkg), so a single PackageReference gives you:

- the core framework (`Context`, plugins, services, events, effects)
- compile-time plugin metadata generation (`[Plugin]` classes)
- compile-time diagnostics (`CORDIS001`-`CORDIS003`)

```
dotnet add package CordiSharp
```

Microsoft.Extensions integration lives in dedicated extension packages:

```
dotnet add package CordiSharp.Extensions.DependencyInjection   # AddCordiSharp, CordiSharpOptions, ctx.Resolve<T>()
dotnet add package CordiSharp.Extensions.Hosting               # CordiSharpHost (IHostedService), AddCordiSharpHosting
dotnet add package CordiSharp.Extensions.Logging               # AddCordiSharpLogging, CordiSharpLogExporter, UseLoggerFactory
```

## Quick start

```csharp
using CordiSharp;
using CordiSharp.Schema;

// 1. create a root context
var root = Context.Create();

// 2. load a plugin (class, delegate or object) - awaitable like cordis
await root.Plugin(typeof(GreeterPlugin), new GreeterConfig { Message = "hi" });

// 3. plugins can register services, events and effects
root.Emit(ChatEvents.Message, "hello world");

[Plugin("greeter")]
public sealed class GreeterPlugin : IPlugin<GreeterConfig>
{
    public void Load(Context ctx, GreeterConfig config)
    {
        ctx.On(ChatEvents.Message, (c, text) =>
        {
            Console.WriteLine($"{config.Message}: {text}");
            return null;
        });
        ctx.Provide("greeter", this);
    }
}

[PluginConfig]
public sealed class GreeterConfig
{
    public string? Message { get; set; }
}

public static class ChatEvents
{
    public static readonly EventKey<string> Message = EventKey.Create<string>("chat/message");
}
```

## Core concepts

### Context and scopes

A `Context` is the central object. Scopes mirror cordis:

- `ctx.Extend()` - a child context sharing the isolate/intercept scope.
- `ctx.Isolate(name)` - a child context that isolates a service name (provides in the
  parent are invisible to it and vice versa).
- `ctx.Intercept(name, config)` - a child context with an intercepted config for a service.

### Plugins and fibers

`ctx.Plugin(...)` loads a plugin and returns an awaitable/disposable `PluginHandle`:

- `await handle` - waits until the plugin is loaded (or throws if loading failed).
- `handle.Update(config)` - updates the config and restarts the fiber.
- `await handle.DisposeAsync()` - unloads the plugin (runs its effects' disposers in reverse order).

Plugin shapes:

- `IPlugin<TConfig>` / `IAsyncPlugin<TConfig>` classes (also usable as MSDI services)
- `Service` subclasses (auto-provide themselves; override `Init()` for lifecycle)
- delegates: `ctx.Plugin((ctx, config) => ...)` / `ctx.Plugin((ctx) => ...)`
- object plugins implementing `IPluginObject`

Plugins declare dependencies with `[Inject("service")]`; the fiber stays **pending** until the
injected service is provided in the matching isolate, then loads - and unloads if the service
is removed (the core Cordis dependency graph).

### Effects

`ctx.Effect(setup, label?)` creates an effect whose disposer runs when the fiber unloads
(sync, async, generator and async-generator forms are supported):

```csharp
await ctx.Plugin((Func<Context, object?>)(ctx =>
{
    ctx.Effect(() =>
    {
        var timer = StartTimer();
        return () => timer.Stop();          // runs on plugin unload
    }, "timer");
    return null;
}));
```

### Events

Typed event keys with cordis dispatch modes:

- `ctx.On(key, handler)` / `ctx.Once(key, handler)` - registration (returns a disposable)
- `ctx.Emit(key, args)` - synchronous dispatch
- `await ctx.Parallel(key, args)` - concurrent, aggregates errors
- `await ctx.Serial(key, args)` - sequential, stops at the first bailed result
- `ctx.Bail(key, args)` - synchronous serial dispatch
- `ctx.Waterfall(key, args, fallback)` - chained dispatch with `next()`
- `ctx.OnUpdate(hook)` - intercept `internal/update` (config changes)

### Services

`ctx.Provide(name, value)`, `ctx.Get<T>(name)`, `ctx.Set(name, value)`, `ctx.Mixin(source, members)`
and `Service` subclasses. Services are scoped by isolate tokens, and providing/removing a
service notifies dependent fibers (loading/unloading them).

## Microsoft.Extensions.DependencyInjection

```csharp
using CordiSharp.Extensions.DependencyInjection;
using CordiSharp.Extensions.Hosting;

var services = new ServiceCollection();
services.AddCordiSharp(o => o.AddPlugin(typeof(GreeterPlugin), new GreeterConfig { Message = "hi" }));
services.AddCordiSharpHosting();
services.AddSingleton<IGreeter, ConsoleGreeter>();
await using var provider = services.BuildServiceProvider();

var host = provider.GetRequiredService<CordiSharpHost>();   // IHostedService
await host.StartAsync();                                    // loads configured plugins
// ...
await host.StopAsync();                                     // unloads them
```

`AddCordiSharp` (from `CordiSharp.Extensions.DependencyInjection`) registers the root
`Context` singleton and `CordiSharpOptions`. `AddCordiSharpHosting` (from
`CordiSharp.Extensions.Hosting`) additionally registers `CordiSharpHost` as an
`IHostedService`, so plugins load/unload with a `Microsoft.Extensions.Hosting` host.

Plugin classes can take MSDI services in their constructors (e.g. `(IGreeter greeter)` or
`(Context ctx, IGreeter greeter)`); `ctx.Resolve<T>()` resolves from the attached provider.

## Microsoft.Extensions.Logging

`CordiSharp.Extensions.Logging` bridges CordiSharp's logger with the standard logging pipeline:

- `builder.Logging.AddCordiSharpLogging()` — registers a `CordiSharpLoggerProvider` so MEL
  `ILogger<T>` entries are written into the root context's `LoggerService`.
- `ctx.UseLoggerFactory(factory)` — attaches a `CordiSharpLogExporter` so `ctx.Logger()`
  output is forwarded to the MEL pipeline (console, file, ...). Both directions can be
  combined safely (re-exported messages are not echoed back).

```csharp
using CordiSharp.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddCordiSharpLogging();   // MEL -> CordiSharp

var host = builder.Build();
host.Services.GetRequiredService<Context>()
    .UseLoggerFactory(host.Services.GetRequiredService<ILoggerFactory>()); // CordiSharp -> MEL
```

## Source generator

Classes annotated with `[Plugin]` are discovered at compile time; the generator emits a
`CordiSharp.Generated.PluginRegistrations` type with plugin name, config type, a compiled
config schema (from `[PluginConfig]` + `[DefaultValue]`) and the inject list. The runtime
prefers this metadata over reflection.

## Analyzers

- `CORDIS001` (warning) - `[PluginConfig]` property types not representable by a schema
- `CORDIS002` (error) - empty `[Inject]` name
- `CORDIS003` (error) - plugin class without a usable public constructor

## Differences from the JS version

- No transparent property proxying (the JS `ctx.foo` magic); services are resolved via
  `ctx.Get<T>(name)` / typed helpers, and events use typed keys.
- Config objects are typed POCOs; schemas validate and preserve the instance (dictionaries are
  coerced like JS plain objects).
- Effect setup accepts `Action`, `Func<ValueTask>`, `Func<Task>`, `Task`, `IEnumerable`,
  `IAsyncEnumerable`, `IDisposable` and delegates.

## Samples

Two runnable samples live in `samples/`:

- `CordiSharp.Samples.Basic` — walks through the core concepts: plugins with
  generated metadata, typed events, serial/waterfall dispatch, effects, services,
  `[Inject]` dependency resolution, isolates, config updates and disposal.
  ```
  dotnet run --project samples/CordiSharp.Samples.Basic
  ```
- `CordiSharp.Samples.Msdi` — hosts CordiSharp inside `Microsoft.Extensions.Hosting`;
  plugins are loaded/unloaded with the host, plugin classes receive MSDI services
  via constructor injection, a timer plugin emits events consumed by another, and
  `ctx.Logger()` output is bridged into the host's console logging.
  ```
  dotnet run --project samples/CordiSharp.Samples.Msdi
  ```
- `CordiSharp.Samples.PluginLibrary` + `CordiSharp.Samples.PluginHost` — cross-assembly
  plugin loading: plugins live in a separate library assembly and the host loads them
  both statically (`typeof(CounterPlugin)`) and dynamically (scanning the library
  assembly for `[Plugin]` types), with generated metadata, injects and isolates
  working across the assembly boundary.
  ```
  dotnet run --project samples/CordiSharp.Samples.PluginHost
  ```

## Building

```
dotnet build CordiSharp.slnx
dotnet test test/CordiSharp.Tests
dotnet pack src/CordiSharp/CordiSharp.csproj -c Release    # entry package (runtime + analyzers)
dotnet pack src/CordiSharp.Extensions.DependencyInjection/CordiSharp.Extensions.DependencyInjection.csproj -c Release
dotnet pack src/CordiSharp.Extensions.Hosting/CordiSharp.Extensions.Hosting.csproj -c Release
dotnet pack src/CordiSharp.Extensions.Logging/CordiSharp.Extensions.Logging.csproj -c Release
dotnet pack src/CordiSharp.Generators/CordiSharp.Generators.csproj -c Release
dotnet pack src/CordiSharp.Analyzers/CordiSharp.Analyzers.csproj -c Release
```

## License

MIT (port of cordis, which is MIT-licensed).
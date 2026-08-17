# CordiSharp

[English](README.md) | 简体中文

[Cordis](https://github.com/cordiverse/cordis) —— 时空可组合性元框架的 C# 移植版。
CordiSharp 将 Cordis 基于上下文/作用域的插件系统、依赖注入的服务、类型化事件和
fiber 生命周期管理带到 .NET，并原生集成 Microsoft.Extensions.DependencyInjection、
Roslyn 源生成器和 Roslyn 分析器。

> **文档**：详细的中文 API 参考见 [`docs/`](docs/README.md)。

> **状态**：对 cordis v4（`packages/core`）的行为忠实移植。核心语义
> （fiber 状态机、inject/isolate/intercept、effect 生命周期、事件分发模式）均直接
> 移植自 TypeScript 源码，并由移植过来的测试套件覆盖。

## 包结构

运行时库随 `CordiSharp` 一起发布，并打包了源生成器和分析器
（所有 DLL 都被打进同一个 nupkg），一次 PackageReference 即可获得：

- 核心框架（`Context`、插件、服务、事件、effects）
- 编译期插件元数据生成（`[Plugin]` 类）
- 编译期诊断（`CORDIS001`–`CORDIS003`）

```
dotnet add package CordiSharp
```

Microsoft.Extensions 集成拆分到了独立的扩展包：

```
dotnet add package CordiSharp.Extensions.DependencyInjection   # AddCordiSharp、CordiSharpOptions、ctx.Resolve<T>()
dotnet add package CordiSharp.Extensions.Hosting               # CordiSharpHost（IHostedService）、AddCordiSharpHosting
dotnet add package CordiSharp.Extensions.Logging               # AddCordiSharpLogging、CordiSharpLogExporter、UseLoggerFactory
```

## 快速上手

```csharp
using CordiSharp;
using CordiSharp.Schema;

// 1. 创建根上下文
var root = Context.Create();

// 2. 加载插件（类、委托或对象）—— 可 await，与 cordis 一致
await root.Plugin(typeof(GreeterPlugin), new GreeterConfig { Message = "hi" });

// 3. 插件可以注册服务、事件和 effects
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

## 核心概念

### Context 与作用域

`Context` 是核心对象。作用域与 cordis 对齐：

- `ctx.Extend()` —— 共享 isolate/intercept 作用域的子上下文。
- `ctx.Isolate(name)` —— 隔离某个服务名的子上下文（父作用域提供的服务对它不可见，反之亦然）。
- `ctx.Intercept(name, config)` —— 为某个服务注入拦截配置的子上下文。

### 插件与 Fiber

`ctx.Plugin(...)` 加载插件并返回可 await、可释放的 `PluginHandle`：

- `await handle` —— 等待插件加载完成（加载失败则抛出异常）。
- `handle.Update(config)` —— 更新配置并重启 fiber。
- `await handle.DisposeAsync()` —— 卸载插件（逆序执行其 effects 的 disposer）。

插件形态：

- `IPlugin<TConfig>` / `IAsyncPlugin<TConfig>` 类（也可作为 MSDI 服务使用）
- `Service` 子类（自动注册自身；重写 `Init()` 处理生命周期）
- 委托：`ctx.Plugin((ctx, config) => ...)` / `ctx.Plugin((ctx) => ...)`
- 实现 `IPluginObject` 的对象插件

插件通过 `[Inject("service")]` 声明依赖；fiber 会一直保持 **pending**，直到匹配的
isolate 作用域内提供了被注入的服务，然后加载 —— 服务被移除时则卸载
（这正是 Cordis 的核心依赖图）。

### Effects

`ctx.Effect(setup, label?)` 创建 effect，其 disposer 在 fiber 卸载时执行
（支持同步、异步、生成器和异步生成器形式）：

```csharp
await ctx.Plugin((Func<Context, object?>)(ctx =>
{
    ctx.Effect(() =>
    {
        var timer = StartTimer();
        return () => timer.Stop();          // 插件卸载时执行
    }, "timer");
    return null;
}));
```

### 事件

类型化事件键 + cordis 分发模式：

- `ctx.On(key, handler)` / `ctx.Once(key, handler)` —— 注册（返回可释放句柄）
- `ctx.Emit(key, args)` —— 同步分发
- `await ctx.Parallel(key, args)` —— 并发执行，聚合错误
- `await ctx.Serial(key, args)` —— 顺序执行，遇到第一个 bail 结果即停止
- `ctx.Bail(key, args)` —— 同步顺序分发
- `ctx.Waterfall(key, args, fallback)` —— 链式分发（`next()`）
- `ctx.OnUpdate(hook)` —— 拦截 `internal/update`（配置变更）

### 服务

`ctx.Provide(name, value)`、`ctx.Get<T>(name)`、`ctx.Set(name, value)`、
`ctx.Mixin(source, members)` 以及 `Service` 子类。服务按 isolate 令牌隔离；
提供/移除服务会通知依赖它的 fiber（加载/卸载）。

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
await host.StartAsync();                                    // 加载已配置的插件
// ...
await host.StopAsync();                                     // 卸载它们
```

插件类的构造函数可以直接接收 MSDI 服务（如 `(IGreeter greeter)` 或
`(Context ctx, IGreeter greeter)`）；`ctx.Resolve<T>()` 从挂载的 provider 解析服务。
`AddCordiSharp`（来自 `CordiSharp.Extensions.DependencyInjection`）注册根 `Context`
单例和 `CordiSharpOptions`；`AddCordiSharpHosting`（来自 `CordiSharp.Extensions.Hosting`）
额外把 `CordiSharpHost` 注册为 `IHostedService`，可直接配合 `Microsoft.Extensions.Hosting`
的 `Host.CreateApplicationBuilder` 使用。

## Microsoft.Extensions.Logging

`CordiSharp.Extensions.Logging` 把 CordiSharp 日志系统接入标准 .NET 日志管线：

- `builder.Logging.AddCordiSharpLogging()` —— 注册 `CordiSharpLoggerProvider`，MEL 的
  `ILogger<T>` 条目写入根上下文的 `LoggerService`；
- `ctx.UseLoggerFactory(factory)` —— 挂载 `CordiSharpLogExporter`，`ctx.Logger()` 的输出
  转发到 MEL 管线（控制台、文件等）。两个方向可同时使用（重入保护，不会回环）。

```csharp
using CordiSharp.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddCordiSharpLogging();   // MEL → CordiSharp

var host = builder.Build();
host.Services.GetRequiredService<Context>()
    .UseLoggerFactory(host.Services.GetRequiredService<ILoggerFactory>()); // CordiSharp → MEL
```

## 源生成器

标有 `[Plugin]` 的类在编译期被发现；生成器会生成一个
`CordiSharp.Generated.PluginRegistrations` 类型，包含插件名、配置类型、编译好的
配置 schema（来自 `[PluginConfig]` + `[DefaultValue]`）以及 inject 列表。运行时优先
使用这份元数据而非反射。

## 分析器

- `CORDIS001`（警告）—— `[PluginConfig]` 中存在无法用 schema 表示的类型
- `CORDIS002`（错误）—— 空的 `[Inject]` 名称
- `CORDIS003`（错误）—— 插件类缺少可用的公共构造函数

## 示例

`samples/` 下有两个可运行示例：

- `CordiSharp.Samples.Basic` —— 演示核心概念：带生成元数据的插件、类型化事件、
  serial/waterfall 分发、effects、服务、`[Inject]` 依赖解析、isolate、配置更新与卸载。
  ```
  dotnet run --project samples/CordiSharp.Samples.Basic
  ```
- `CordiSharp.Samples.Msdi` —— 将 CordiSharp 托管进 `Microsoft.Extensions.Hosting`；
  插件随主机启停，插件类通过构造函数注入 MSDI 服务，一个计时器插件向另一个插件发事件，
  并把 `ctx.Logger()` 的输出桥接到主机的控制台日志。
  ```
  dotnet run --project samples/CordiSharp.Samples.Msdi
  ```
- `CordiSharp.Samples.PluginLibrary` + `CordiSharp.Samples.PluginHost` —— 跨程序集插件加载：
  插件定义在独立的类库程序集中，宿主分别用静态方式（`typeof(CounterPlugin)`）和动态方式
  （扫描库程序集中的 `[Plugin]` 类型）加载它们；生成的元数据、inject 和 isolate 都能跨
  程序集边界正常工作。
  ```
  dotnet run --project samples/CordiSharp.Samples.PluginHost
  ```

## 与 JS 版的差异

- 没有透明的属性代理（JS 的 `ctx.foo` 魔法）；服务通过 `ctx.Get<T>(name)` / 类型化辅助方法解析，
  事件使用类型化键。
- 配置对象是类型化 POCO；schema 校验并保留实例（字典会被强制转换为 JS 风格的普通对象）。
- Effect setup 接受 `Action`、`Func<ValueTask>`、`Func<Task>`、`Task`、`IEnumerable`、
  `IAsyncEnumerable`、`IDisposable` 和委托。

## 构建

```
dotnet build CordiSharp.slnx
dotnet test test/CordiSharp.Tests
dotnet pack src/CordiSharp/CordiSharp.csproj -c Release    # 入口包（运行时 + 分析器）
dotnet pack src/CordiSharp.Extensions.DependencyInjection/CordiSharp.Extensions.DependencyInjection.csproj -c Release
dotnet pack src/CordiSharp.Extensions.Hosting/CordiSharp.Extensions.Hosting.csproj -c Release
dotnet pack src/CordiSharp.Extensions.Logging/CordiSharp.Extensions.Logging.csproj -c Release
dotnet pack src/CordiSharp.Generators/CordiSharp.Generators.csproj -c Release
dotnet pack src/CordiSharp.Analyzers/CordiSharp.Analyzers.csproj -c Release
```

## 许可证

MIT（Cordis 的移植版，原项目为 MIT 许可）。
# MSDI 与 Hosting 集成

CordiSharp 提供三个 Microsoft.Extensions 扩展包，把插件系统接入标准的 .NET 依赖注入与托管生命周期：

```bash
dotnet add package CordiSharp.Extensions.DependencyInjection   # AddCordiSharp、CordiSharpOptions、ctx.Resolve<T>()
dotnet add package CordiSharp.Extensions.Hosting               # CordiSharpHost（IHostedService）、AddCordiSharpHosting
dotnet add package CordiSharp.Extensions.Logging               # 日志桥接：AddCordiSharpLogging、UseLoggerFactory（见[日志系统](10-logging.md)）
```

## CordiSharp.Extensions.DependencyInjection

### `AddCordiSharp`

注册根 `Context` 单例（并把 MSDI provider 挂到 `ctx.ServiceProvider`，供插件构造函数解析服务），以及 `CordiSharpOptions`：

```csharp
using CordiSharp.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddCordiSharp(o => o.AddPlugin(typeof(GreeterPlugin), new GreeterConfig { Message = "hi" }));

await using var provider = services.BuildServiceProvider();
var ctx = provider.GetRequiredService<Context>();
```

要点：

- `Context` 注册为**单例**；
- 插件配置在 `CordiSharpOptions` 中声明（`AddPlugin`），由宿主加载（见下）；
- `UseServiceProvider(provider)` 可把 provider 挂到已有根 `Context` 上。

### `CordiSharpOptions`

```csharp
var options = new CordiSharpOptions();
options.AddPlugin(typeof(GreeterPlugin), new GreeterConfig { Message = "hi" });
options.AddPlugin<OtherPlugin>();                    // 泛型重载：T : class, IPlugin, new()
```

| 成员 | 说明 |
| --- | --- |
| `AddPlugin(Type pluginType, object? config = null)` | 注册要加载的插件 |
| `AddPlugin<T>(object? config = null)` | 泛型注册，`T : class, IPlugin, new()` |

### `ctx.Resolve<T>()`

从挂载的 MSDI provider 解析服务（C# 14 扩展成员）：

```csharp
var greeter = ctx.Resolve<IGreeter>();      // T : class
var svc = ctx.Resolve(typeof(IGreeter));    // 按类型
```

未挂载 provider 时返回 `null`。实现细节：使用 C# 14 `extension(Context ctx)` 扩展成员语法，需要 .NET 10 SDK / C# 14 编译器。

### 插件构造函数注入 MSDI 服务

插件类构造函数的参数解析顺序（见[插件与 Fiber 生命周期](03-plugins-fibers.md#构造函数注入)）：

1. `Context` → 当前 fiber 的 ctx
2. 配置类型 → 传入的 config
3. 其他 class/interface → 从 `ctx.ServiceProvider` 解析

```csharp
public sealed class GreeterPlugin(IGreeter greeter, Context ctx, GreeterConfig config)
    : IPlugin<GreeterConfig>
{
    public void Load(Context ctx, GreeterConfig config) { }
}
```

## CordiSharp.Extensions.Hosting

### `AddCordiSharpHosting`

注册 `AddCordiSharp` 的一切，外加 `CordiSharpHost` 单例，并把它注册为 `IHostedService` —— 配合 `Microsoft.Extensions.Hosting` 的主机启停自动加载/卸载插件：

```csharp
using CordiSharp.Extensions.DependencyInjection;
using CordiSharp.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCordiSharpHosting(o => o.AddPlugin(typeof(GreeterPlugin), new GreeterConfig { Message = "hi" }));
builder.Services.AddSingleton<IGreeter, ConsoleGreeter>();

var host = builder.Build();
await host.RunAsync();
```

- `StartAsync`：按配置顺序 `RootContext.Plugin(...)` 并 `await` 每个 handle（加载完成才继续）；
- `StopAsync`：**逆序**卸载所有插件；
- `IHostedService` 注册让插件生命周期与主机一致。

### `CordiSharpHost`

直接使用：

```csharp
var host = provider.GetRequiredService<CordiSharpHost>();
await host.StartAsync();   // 加载配置的插件
// ...
await host.StopAsync();    // 卸载它们
```

| 成员 | 说明 |
| --- | --- |
| `RootContext` | 被托管的根 `Context` |
| `StartAsync(ct)` | 加载 `CordiSharpOptions` 中配置的全部插件 |
| `StopAsync(ct)` | 逆序卸载全部插件 |
| `Dispose()` | 触发 `StopAsync`（fire-and-forget） |

## 完整示例

```csharp
using CordiSharp;
using CordiSharp.Extensions.DependencyInjection;
using CordiSharp.Extensions.Hosting;
using CordiSharp.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCordiSharpHosting(o =>
{
    o.AddPlugin(typeof(GreeterPlugin), new GreeterConfig { Message = "hi" });
});
builder.Services.AddSingleton<IGreeter, ConsoleGreeter>();

await builder.Build().RunAsync();

// ---- 插件：构造函数注入 MSDI 服务 ----

[Plugin("greeter")]
public sealed class GreeterPlugin(IGreeter greeter, GreeterConfig config) : IPlugin<GreeterConfig>
{
    public void Load(Context ctx, GreeterConfig config)
    {
        ctx.On(ChatEvents.Message, (c, text) =>
        {
            greeter.Say($"{config.Message}: {text}");
            return null;
        });
    }
}

public interface IGreeter { void Say(string message); }
public sealed class ConsoleGreeter : IGreeter { public void Say(string m) => Console.WriteLine(m); }

[PluginConfig]
public sealed class GreeterConfig { public string? Message { get; set; } }

public static class ChatEvents
{
    public static readonly EventKey<string> Message = EventKey.Create<string>("chat/message");
}
```

## 注意事项

- `AddCordiSharp` 与 `AddCordiSharpHosting` 都可多次调用（幂等：`TryAddSingleton`）；
- 不调用 `AddCordiSharpHosting` 时，`AddCordiSharp` 只注册 `Context` 与选项，插件不会自动加载 —— 需要手动 `ctx.Plugin(...)`；
- 插件类既是 `IPlugin<TConfig>` 又是 MSDI 服务时，注意构造函数解析的优先级（`Context`/config 优先于 provider 解析）；
- `Resolve<T>()` 依赖 C# 14 扩展成员，若项目编译器较旧，改用 `ctx.ServiceProvider?.GetService<T>()`。

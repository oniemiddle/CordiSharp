# 快速上手

## 安装

```bash
dotnet add package CordiSharp
```

一个 `PackageReference` 即可获得全部能力：

- 核心运行时（`Context`、插件、服务、事件、effects、schema、日志）
- 编译期插件元数据生成器（`[Plugin]` 类）
- 编译期诊断（`CORDIS001`–`CORDIS003`）

如果需要 Microsoft.Extensions 集成，再额外安装：

```bash
dotnet add package CordiSharp.Extensions.DependencyInjection   # AddCordiSharp、ctx.Resolve<T>()
dotnet add package CordiSharp.Extensions.Hosting               # CordiSharpHost、AddCordiSharpHosting
```

## 最小示例

```csharp
using CordiSharp;
using CordiSharp.Schema;

// 1. 创建根上下文 —— 一切从这里开始
var root = Context.Create();

// 2. 加载插件（类、委托或对象），可 await，与 cordis 一致
await root.Plugin(typeof(GreeterPlugin), new GreeterConfig { Message = "hi" });

// 3. 向事件总线发出消息，插件内的监听器会收到并响应
root.Emit(ChatEvents.Message, "hello world");

// ---- 插件定义 ----

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
    // 类型化事件键：键携带负载类型，分发时自动强类型化
    public static readonly EventKey<string> Message = EventKey.Create<string>("chat/message");
}
```

运行输出：

```
hi: hello world
```

## 逐步讲解

### 1. `Context.Create()` 创建根上下文

`Context` 是 CordiSharp 的核心对象，相当于 cordis 的 `ctx`。它拥有：

- 作用域树（`Extend` / `Isolate` / `Intercept` 产生的子上下文）
- 服务注册表（`ReflectService`）
- 事件总线（`EventsService`）
- 插件注册表（`RegistryService`）
- 日志服务（`LoggerService`）
- 根 fiber（生命周期载体）

### 2. `root.Plugin(...)` 加载插件

`Plugin` 接受多种形态：

| 形态 | 写法 |
| --- | --- |
| 实现 `IPlugin<TConfig>` 的类 | `root.Plugin(typeof(GreeterPlugin), config)` |
| 实现 `IAsyncPlugin<TConfig>` 的类 | 同上（异步 `LoadAsync`） |
| 委托 | `root.Plugin((ctx, config) => ...)` |
| 对象（`IPluginObject`） | `root.Plugin(obj)` |
| 泛型强类型 | `root.Plugin<GreeterPlugin, GreeterConfig>(config)` |

返回的 `PluginHandle` 可 `await`（等待插件加载完成），可 `Update(config)` 更新配置，可 `DisposeAsync()` 卸载。

### 3. 插件内部使用 `ctx`

`Load(Context ctx, TConfig config)` 中的 `ctx` 是该插件 fiber 专属的子上下文：

- `ctx.On(key, handler)` —— 注册事件监听（随插件卸载自动移除）
- `ctx.Provide(name, value)` —— 提供服务（可供其他插件注入）
- `ctx.Effect(...)` —— 注册生命周期 effect
- `ctx.Get<T>(name)` —— 解析服务

### 4. `root.Emit(key, args)` 分发事件

同步分发，所有监听器按注册顺序执行。还有 `Parallel`、`Serial`、`Bail`、`Waterfall` 等模式，详见[事件系统](05-events.md)。

## 下一步

- 理解核心概念：[Context 与作用域](02-context-scopes.md)
- 深入插件系统：[插件与 Fiber 生命周期](03-plugins-fibers.md)
- 接入 ASP.NET Core / 通用主机：[MSDI 与 Hosting 集成](09-msdi-hosting.md)

# 常见问题（FAQ）

## 安装与构建

### 需要哪个 .NET 版本？

CordiSharp 目标 `net10.0`。扩展包中的 `ctx.Resolve<T>()` 使用 C# 14 扩展成员语法，需要 .NET 10 SDK（C# 14 编译器）。

### 我只需要核心，不想要 MSDI 集成？

`CordiSharp` 一个包就够了。`CordiSharp.Extensions.DependencyInjection` / `CordiSharp.Extensions.Hosting` 是可选扩展。

### 为什么 `CORDIS003` 报错：插件类需要公共构造函数？

CordiSharp 通过反射实例化插件类。请给插件类加一个公共构造函数（可以是主构造函数），否则无法实例化：

```csharp
[Plugin]
public sealed class MyPlugin : IPlugin<MyConfig>
{
    public MyPlugin() { }   // 必须有公共构造函数
    public void Load(Context ctx, MyConfig config) { }
}
```

## 插件与生命周期

### 插件一直处于 `Pending` 状态不加载？

fiber 保持 `Pending` 说明有 `[Inject]` 依赖未满足：要么服务未提供，要么提供服务的 fiber 不在同一 isolate 作用域、或尚未 `Active`。检查：

1. 依赖名拼写与 `Provide` 的名字一致；
2. 依赖服务是否在 `Isolate` 隔离之外提供；
3. 依赖服务的提供方是否加载成功。

### 更新配置的正确姿势？

```csharp
var handle = root.Plugin(typeof(MyPlugin), config);
handle.Update(newConfig);      // 走 internal/update 钩子链并重启 fiber
```

不要直接改 config 对象的属性 —— 框架不会感知。配置变更可被 `ctx.OnUpdate` 钩子拦截/改写。

### 插件如何按依赖顺序卸载？

不需要手动排序。effect 在 fiber 卸载时逆序执行，且 `ctx.plugin()` 本身是父 fiber 的 effect —— 子插件在父插件其余 disposer 之前卸载，依赖关系自然成立。

## 服务

### `Get` 抛 `ServiceResolutionException` 怎么办？

- 服务未注册：先 `Provide`；
- 服务在隔离作用域外：检查 `Isolate` 的使用；
- 服务提供 fiber 未 `Active`：等它加载完成再取，或用 `strict: false` 调试；
- 在无 inject 声明的上下文中读取未知属性：`Get` 需要注入声明或 extra 属性。

### 同名服务能注册两次吗？

同一 isolate 作用域内不能：`Provide` 检测到同名已注册会抛 `CordisException`（`service "X" has been registered at <fiber>`）。要隔离多实例请用 `Isolate`。

## 事件

### `Serial` / `Bail` 怎么才算"拦截"？

监听器返回 `null` 或 `false` 表示不拦截（继续）；返回其他任意值（含 `0`、`""`）都视为 bail，分发立即停止并把该值作为结果。

### 异步监听器在 `Emit` 里没被 await？

`Emit` 是同步分发；`OnAsync` 的异步监听器在 `Emit` 下以 fire-and-forget 执行（异常写 stderr）。需要等待异步完成请用 `Parallel` / `Serial`。

### 事件监听如何保证随插件卸载？

`ctx.On` 内部注册为 fiber 的 effect，fiber 卸载自动移除，无需手动注销。手动注销用返回的 `IDisposable`。

## Schema

### 配置里的复杂类型被当作 `Schema.Any()` 了？

`CORDIS001` 警告会提示。不受支持的属性类型（如自定义引用类型、非字符串键字典）按 `Any` 处理 —— 不校验但也不报错。用 `[PluginConfig]` + 受支持的类型（基本类型、枚举、数组/列表、字符串键字典、嵌套配置类）可获得完整校验。

### 校验失败会怎样？

插件加载时 `Schema.Parse(config)` 抛 `SchemaValidationException`（`Issues` 列出全部问题），fiber 进入 `Failed`，`await handle` 抛该异常。

## MSDI / Hosting

### 插件构造函数里的服务解析不到？

构造函数参数解析优先级：`Context` → 配置类型 → provider（class/interface，非 `string`）→ 值类型从 provider。确认：

1. 服务已注册到 `ServiceCollection`；
2. 用的是 `AddCordiSharp` / `AddCordiSharpHosting`（它们才会挂 provider 到根 Context）；
3. 参数不是 `string` 等基础类型（它们不会被当作 provider 服务解析）。

### 用 `Host.CreateApplicationBuilder` 时插件没自动加载？

必须调用 `AddCordiSharpHosting`（它会注册 `IHostedService`）。只调 `AddCordiSharp` 时插件不会自动加载。

### 手动启停插件？

```csharp
var host = provider.GetRequiredService<CordiSharpHost>();
await host.StartAsync();
await host.StopAsync();
```

## 其他

### 与 cordis JS 版的差异？

没有 `ctx.foo` 属性代理魔法，服务用 `ctx.Get<T>(name)`；事件用类型化 `EventKey<T>`；配置是类型化 POCO + schema 校验。详见仓库根 README 的"Differences from the JS version"。

### 如何调试 fiber/effect 状态？

- `handle.State` —— fiber 当前状态；
- `fiber.GetEffects()` —— 当前注册的 effect 元数据树（label + children）；
- `ctx.Events.HookSnapshot()` —— 每个事件名下的钩子数量；
- `ctx.LoggerService.Buffer` —— 最近日志（环形缓冲）。

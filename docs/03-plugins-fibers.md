# 插件与 Fiber 生命周期

插件是 CordiSharp 的构建单元。加载一个插件会创建一个 **fiber** —— 该插件的一次实例，带有自己的上下文、注入需求、effect 注册表和生命周期状态机。

## 插件形态

### 1. 类插件（`IPlugin<TConfig>` / `IAsyncPlugin<TConfig>`）

最常用。类需要实现接口，且**至少有一个公共构造函数**（`CORDIS003` 强制）：

```csharp
[Plugin("greeter")]
public sealed class GreeterPlugin : IPlugin<GreeterConfig>
{
    public void Load(Context ctx, GreeterConfig config)
    {
        // ...
    }
}

[Plugin("async-greeter")]
public sealed class AsyncGreeterPlugin : IAsyncPlugin<GreeterConfig>
{
    public async Task LoadAsync(Context ctx, GreeterConfig config)
    {
        await Task.Delay(10);
    }
}
```

插件类还可以通过构造函数接收依赖（`Context`、配置对象、MSDI 服务），见下文"依赖注入"。

### 2. `Service` 子类

继承 `Service` 的类会自动把自己注册为同名服务（或 `[Service("name")]` 指定的名字），并可重写 `Init()` 处理生命周期：

```csharp
[Service("greeter")]
public sealed class GreeterService : Service
{
    public GreeterService(Context ctx) : base(ctx) { }

    protected override object? Init()
    {
        // 返回 disposer 会在卸载时执行；也可返回 Task
        return () => Console.WriteLine("greeter stopped");
    }

    protected override bool Check() => true;   // 可选：服务可用性检查
}
```

`Service` 实现了 `IContextFilter`：作为事件 thisArg 时只匹配同 isolate 作用域的监听（见[事件系统](05-events.md)）。

### 3. 委托插件

```csharp
var handle = root.Plugin((Context ctx) =>
{
    ctx.Effect(() => () => Console.WriteLine("bye"));
    return null;
});

var handle2 = root.Plugin((Context ctx, GreeterConfig config) =>
{
    // config 类型由委托第二参数推断，生成对应 schema
    return null;
});
```

带依赖声明的委托插件用 `ctx.Inject(deps, callback)`：

```csharp
root.Inject(["database"], (ctx, db) =>
{
    // 直到 "database" 服务被提供且处于活跃状态，fiber 才会加载
    return null;
});
```

### 4. 对象插件（`IPluginObject`）

```csharp
public sealed class GreeterObject : IPluginObject
{
    public string? Name => "greeter-obj";
    public object? Apply(Context ctx, object? config) => null;
}
```

## 特性

| 特性 | 目标 | 说明 |
| --- | --- | --- |
| `[Plugin(name?)]` | 类 | 标记插件类；`Name` 缺省时用类名；`ConfigType` 可显式指定配置类型 |
| `[PluginConfig]` | 类 | 标记配置类型，供生成器/`Schema.FromType` 使用 |
| `[Inject(name, config?)]` | 类 / 属性（可重复） | 声明必需的服务注入；加载前该服务必须可用，否则 fiber 保持 `Pending` |
| `[Service(name)]` | 类 | 指定 `Service` 子类的服务名 |
| `[DefaultValue(value)]` | 属性 | 配置属性的默认值 |
| `[Required]` | 属性 | 标记配置属性为必填（不可为 null） |

## 依赖注入

### 类级 `[Inject]`

```csharp
[Plugin("worker")]
[Inject("database")]          // 需要 "database" 服务
[Inject("logger", Level.Debug)]  // 携带注入配置
public sealed class WorkerPlugin : IPlugin<WorkerConfig>
{
    public void Load(Context ctx, WorkerConfig config)
    {
        var db = ctx.Get<IDatabase>("database");
    }
}
```

**关键语义**：fiber 会保持 `Pending`，直到所有注入服务在匹配的 isolate 作用域内可用（且其提供 fiber 处于 `Active`）。服务被移除时，fiber 自动卸载 —— 这就是 Cordis 的核心依赖图。

### 属性级 `[Inject]`

```csharp
public sealed class WorkerPlugin : IPlugin<WorkerConfig>
{
    [Inject("database")]
    public IDatabase? Db { get; set; }
}
```

属性注入在构造后自动完成（`PluginLoader.ApplyInjectProperties`）。

### 构造函数注入

插件类的构造函数按以下规则解析参数：

1. `Context` 类型参数 → 当前 fiber 的 `ctx`
2. 配置类型的实例 → 传入的 config
3. 挂载了 MSDI provider 时（`ctx.ServiceProvider`）→ 从 provider 解析（class/interface，非 `string`）
4. 值类型参数 → 尝试从 provider 解析

```csharp
public sealed class GreeterPlugin(IGreeter greeter, Context ctx, GreeterConfig config)
    : IPlugin<GreeterConfig>
{
    public void Load(Context ctx, GreeterConfig config) { }
}
```

CordiSharp 会尝试所有公共构造函数，选参数最可满足的那个（`CreateInstance` 按参数数量降序尝试）。若全部失败，抛 `CordisException`。

## `PluginHandle`

`ctx.Plugin(...)` 返回 `PluginHandle`，是可 await、可释放、可重启的 fiber 包装：

| 成员 | 说明 |
| --- | --- |
| `Fiber` | 底层 fiber |
| `State` | 当前 `FiberState` |
| `Config` | 当前配置（schema 解析/合并后的值） |
| `Ctx` | fiber 专属上下文 |
| `Await()` / `GetAwaiter()` | 等待 fiber 稳定（无进行中的状态转换且无错误）；加载失败时抛异常 |
| `Update(config, noSave = false)` | 更新配置并重启 fiber（走 `internal/update` 钩子链） |
| `Restart()` | 卸载并重新加载 |
| `DisposeAsync()` / `Dispose()` | 卸载插件（逆序执行 effects 的 disposer） |

```csharp
var handle = root.Plugin(typeof(GreeterPlugin), config);

await handle;                 // 等待加载完成
handle.Update(newConfig);     // 更新配置，重启
await handle.DisposeAsync();  // 卸载
```

## Fiber 状态机

`FiberState` 枚举：

| 状态 | 说明 |
| --- | --- |
| `Pending` | 等待注入服务（或尚未加载） |
| `Loading` | 正在执行插件回调 / `Init()` |
| `Active` | 已完全加载并运行 |
| `Failed` | 加载失败，错误记录在 fiber 上 |
| `Disposed` | 已销毁（uid 已清除） |
| `Unloading` | 正在执行 disposer |

状态转换由 `Refresh`/`SetEpoch` 驱动：epoch 由注入服务的 fiber uid 拼接而成，任一注入服务变化都会导致 `Refresh()` 重新计算 epoch，进而触发 `Reload`（加载）或 `Unload`（卸载）。

- **`Pending` → `Loading` → `Active`**：注入齐备，加载插件体。
- **`Active` → `Unloading` → `Active`（重新加载）**：注入服务变化导致 epoch 变化（服务提供者切换）。
- **加载失败**：`Error` 被记录，状态变 `Failed`，`await handle` 抛异常。
- **`Update(config)`**：走 `internal/update` 钩子链（`OnUpdate` 注册的钩子 + 全局钩子），最后一个钩子调用 `next()` 保存新配置并 `Restart()`。

## 生命周期钩子与卸载顺序

- 插件的 effects 在加载时注册（`ctx.Effect`），卸载时按**逆序**执行 disposer（`DisposableList.DrainReverse`）。
- `Service.Init()` 的返回值（disposer / `Task`）也纳入卸载流程。
- 插件类自身若实现 `IDisposable` / `IAsyncDisposable`，卸载时同样会释放。
- 卸载是异步且顺序的（与 cordis 单线程语义一致，避免并发 disposer 竞态）。
- 父 fiber 卸载会级联卸载其子 fiber（`ctx.plugin()` 本身注册为父 fiber 的一个 effect）。

## 注册表操作

| 成员 | 说明 |
| --- | --- |
| `ctx.RegistryDelete(plugin)` | 注销插件运行时（同步） |
| `ctx.RegistryDeleteAsync(plugin)` | 注销并等待所有 fiber 卸载完成 |
| `ctx.Registry.Size` | 已注册运行时数量 |

```csharp
await root.RegistryDeleteAsync(typeof(GreeterPlugin));
```

## 错误与异常

| 异常 | 触发条件 |
| --- | --- |
| `InvalidPluginException` | `Plugin()` 收到非法插件（非类/委托/对象） |
| `CordisException` | 通用框架错误（如实例化失败、无法更新根 fiber） |
| `InactiveEffectException` | 在已销毁的上下文上创建 effect |
| `ServiceResolutionException` | 无法解析必需服务 |
| `SchemaValidationException` | 配置未通过 schema 校验 |

## 与 JS 版的差异

- 没有透明的属性代理（JS 的 `ctx.foo` 魔法）；服务统一通过 `ctx.Get<T>(name)` / 强类型辅助方法解析。
- 配置对象是类型化 POCO；schema 校验并保留实例（字典会被强制转换为普通对象语义）。
- 插件类的构造函数注入是 C# 特有的便利，JS 版没有对应的静态构造解析。

# 事件系统

事件是插件间通信的主要手段。CordiSharp 使用**类型化事件键**（`EventKey<TArgs>`），并完整移植了 cordis 的多种分发模式：`Emit`、`Parallel`、`Serial`、`Bail`、`Waterfall`。

## 事件键 `EventKey`

事件用命名键区分，键携带负载类型：

```csharp
public static class ChatEvents
{
    public static readonly EventKey<string> Message = EventKey.Create<string>("chat/message");
}
```

- `EventKey.Create<TArgs>(name)` —— 创建类型化键
- `key.Name` —— 键的名字（分发时作为内部事件名）

## 注册监听

### `On` —— 常规监听

```csharp
var disposable = ctx.On(ChatEvents.Message, (c, text) =>
{
    Console.WriteLine(text);
    return null;   // 返回 null = 不拦截；返回非 null = bail（见下）
});
disposable.Dispose();   // 注销（fiber 卸载时也会自动移除）
```

监听器返回 `object?`：返回 `null` 或 `false` 表示"不拦截"，返回其他值表示 **bail**（中断顺序分发并把该值作为结果）。

### `OnAsync` —— 异步监听

```csharp
ctx.OnAsync(ChatEvents.Message, async (c, text) =>
{
    await DoWorkAsync(text);
    return null;
});
```

`Parallel`/`Serial` 会正确 await 异步监听器。

### `Once` —— 一次性监听

```csharp
ctx.Once(ChatEvents.Message, (c, text) =>
{
    Console.WriteLine("first only: " + text);
    return null;
});
```

触发一次后自动注销。

### `OnWaterfall` —— waterfall 监听

```csharp
ctx.OnWaterfall(ChatEvents.Request, (args, next) =>
{
    // 处理或把控制权交给下一个监听
    return null;         // 不处理，调用 next()
    // return SomeValue; // 短路，返回该值
});
```

注意：waterfall 监听器签名是 `(TArgs args, Func<object?> next)`，与 `On` 的 `(Context, TArgs)` 不同。`next()` 调用下一个监听器并返回其结果。

### `OnUpdate` —— 配置变更钩子

拦截 `internal/update` 事件（fiber 配置更新）：

```csharp
ctx.OnUpdate((config, noSave, next) =>
{
    // 可修改 config，或决定是否继续
    return next();
});
```

- 非全局钩子挂到注册它的 fiber 上；
- `EventOptions.Global = true` 时对所有 fiber 生效。

## 分发模式

### `Emit` —— 同步广播

```csharp
root.Emit(ChatEvents.Message, "hello world");
```

所有监听按注册顺序同步执行。异步监听器以 fire-and-forget 方式触发（异常写入 stderr）。可传 thisArg 过滤：

```csharp
root.Emit(service, ChatEvents.Message, "hello");   // 只分发给对 service 可见的监听
```

### `Parallel` —— 并发

```csharp
await root.Parallel(ChatEvents.Message, "hello");
```

所有监听并发执行，全部完成后返回；任一监听抛异常则聚合为 `AggregateException`。

### `Serial` —— 顺序、遇 bail 停止

```csharp
var result = await root.Serial(ChatEvents.Message, "hello");
```

按顺序执行，返回第一个 bail 结果（非 null 且非 false 的返回值）；全部执行完返回 `null`。同步监听在调用线程执行，异步监听被 await。

### `Bail` —— 同步顺序

```csharp
var result = root.Bail(ChatEvents.Message, "hello");
```

与 `Serial` 语义相同，但同步执行（监听器返回的 `Task` 不会被 await）。

### `Waterfall` —— 链式

```csharp
var result = root.Waterfall(ChatEvents.Request, input, () => "fallback");
```

第一个监听器以 `(args, next)` 调用；`next()` 链接到下一个监听器；无监听器时调用 fallback。用于请求处理链（如 RPC 分发）。

## `EventOptions`

| 属性 | 说明 |
| --- | --- |
| `Prepend` | 插到钩子列表头部（先于其他监听执行） |
| `Global` | 全局监听，不被分发 thisArg 过滤 |

```csharp
ctx.On(ChatEvents.Message, listener, new EventOptions { Prepend = true, Global = true });
```

隐式转换：`bool` 可直接作为 `EventOptions`（等价 `Prepend`）：

```csharp
ctx.On(ChatEvents.Message, listener, true);   // Prepend
```

## thisArg 过滤与 `IContextFilter`

分发时可传 `thisArg`（`Emit(thisArg, key, args)` 等）：

- `thisArg` 为 `null` 或监听 `Global`：不过滤；
- `thisArg` 实现 `IContextFilter`：只执行 `FilterContext(监听ctx)` 返回 `true` 的监听；
- `Service` 默认的 `FilterContext` 比较同名 isolate token —— 事件只会被同 isolate 作用域内注册的监听收到。

```csharp
public sealed class Greeter : IContextFilter
{
    public bool FilterContext(Context ctx) => ctx.Isolates.TryGet("greeter", out _);
}
```

## 内部事件

`InternalEvents` 常量（与 cordis `internal/*` 对齐）：

| 常量 | 值 | 触发时机 |
| --- | --- | --- |
| `Plugin` | `internal/plugin` | 插件 fiber 创建/销毁 |
| `Status` | `internal/status` | fiber 状态变化 |
| `Service` | `internal/service` | 服务提供/移除 |
| `Update` | `internal/update` | 配置更新 |
| `Get` | `internal/get` | 属性读取 |
| `Set` | `internal/set` | 属性写入 |
| `Listener` | `internal/listener` | 监听注册 |
| `Dispatch` | `internal/dispatch` | 事件分发 |

内部事件可作为扩展点（例如用 `internal/listener` 拦截监听注册）。

## `EventsService`

`ctx.Events` 是事件服务的完整入口（`Context` 上的便捷方法都转发到这里）：

| 成员 | 说明 |
| --- | --- |
| `On(ctx, key, listener, options)` | 注册监听 |
| `OnAsync(ctx, key, listener, options)` | 注册异步监听 |
| `Once(ctx, key, listener, options)` | 一次性监听 |
| `OnWaterfall(ctx, key, listener, options)` | waterfall 监听 |
| `OnUpdateHook(ctx, hook, options)` | `internal/update` 钩子 |
| `Emit(key, args)` / `Emit(thisArg, key, args)` | 同步分发 |
| `Parallel(key, args)` | 并发分发 |
| `Serial(key, args)` | 顺序分发 |
| `Bail(key, args)` | 同步顺序分发 |
| `Waterfall(key, args, fallback)` | 链式分发 |
| `HookSnapshot()` | 各事件名下已注册钩子数量（诊断） |

## 设计要点

- **监听随 fiber 卸载**：`On` 内部通过 `ctx.Fiber.Effect` 注册，fiber 卸载时自动注销。
- **注册时机**：`On` 要求当前 fiber 处于活跃状态（`AssertActive`），否则抛 `InactiveEffectException`。
- **快照分发**：分发时对钩子列表做快照，监听在分发过程中注销不会影响本轮分发（`Once` 依赖此语义）。
- **bail 语义**：返回 `null` 或 `false` 不算 bail；返回其他任意值（含 `0`、`""` 之外的假值）都算 bail。

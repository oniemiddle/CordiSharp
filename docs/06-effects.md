# Effect 生命周期

Effect 是 CordiSharp 管理资源生命周期的机制：插件通过 `ctx.Effect(setup, label?)` 注册一次性 setup，其返回的 **disposer** 在 fiber 卸载时逆序执行。这保证了插件卸载时资源按依赖顺序释放。

## 基本用法

```csharp
ctx.Effect(() =>
{
    var timer = StartTimer();
    return () => timer.Stop();      // disposer：插件卸载时执行
}, "timer");
```

- setup 立即执行；
- 返回值被**收集**为一个或多个 disposer；
- fiber 卸载时，所有 disposer 按**逆序**执行（`DisposableList.DrainReverse`），异步 disposer 被 await。

## setup 的返回值形式

`Effect` 的 setup 委托（`Func<object?>`）返回以下任一形式，框架自动识别：

| 返回值 | 语义 |
| --- | --- |
| `null` | 无 disposer |
| `Action` | 同步 disposer |
| `Func<ValueTask>` | 异步 disposer |
| `Func<Task>` | 异步 disposer |
| `Task` / `Task<T>` | **异步 setup**：先 await 完成，再收集其返回值 |
| `IDisposable` | 同步 disposer（`Dispose`） |
| `IAsyncDisposable` | 异步 disposer（`DisposeAsync`） |
| `IEnumerable` | 同步生成器：逐个收集产出的 disposer |
| `IAsyncEnumerable` | 异步生成器：逐个收集产出的 disposer（可随事件流注册） |
| `Delegate`（其他） | 当作 disposer 动态调用 |
| `EffectHandle` | 子 effect，卸载时级联释放 |

### 生成器形式

```csharp
// 同步生成器
ctx.Effect(() =>
{
    return YieldDisposers();   // IEnumerable<IDisposable>
});

// 异步生成器：可跨时间收集 disposer
ctx.Effect(async () =>
{
    var e = WatchStream();
    return e;   // IAsyncEnumerable<IDisposable>
});
```

### 异步 setup

```csharp
ctx.Effect(async () =>
{
    var resource = await OpenAsync();
    return () => resource.Close();   // 异步 setup 完成后收集 disposer
});
```

注意：若 setup 返回 `Task`（异步 setup），框架先 await 它，再收集其结果；异步 setup 失败会记录到 effect 上，在 `AwaitDisposed` 时抛出。

## `IEffect` / `EffectHandle`

`ctx.Effect(...)` 返回 `IEffect`（内部为 `EffectHandle`）：

| 成员 | 说明 |
| --- | --- |
| `Label` | 诊断标签（`Effect(setup, label)` 指定，默认 `"anonymous"`） |
| `Children` | 子 effect 元数据（`EffectMeta` 树） |
| `Dispose()` / `DisposeAsync()` | 逆序执行收集的 disposer（幂等） |
| `AwaitDisposed()` | 等待异步 setup 完成并执行所有 disposer；异步 setup 失败时抛出 |
| `GetAwaiter()` | 可 `await effect`（等价 `AwaitDisposed`） |

```csharp
await ctx.Effect(async () => ...);   // 等待 effect 完成（含异步 setup）
```

### 诊断元数据

```csharp
var metas = fiber.GetEffects();   // IReadOnlyList<EffectMeta>
foreach (var meta in metas)
{
    Console.WriteLine(meta.Label);          // "timer"
    foreach (var child in meta.Children) { }
}
```

`Fiber.GetEffects()` 返回该 fiber 当前注册的 effect 元数据树，`EffectMeta` 有 `Label` 与 `Children` 列表。

## 生命周期细节

### 注册即执行

`ctx.Effect` 在调用时立即运行 setup（`RunSetup` 同步执行，异步形式记录 `_setupTask` 后台等待）。因此要在插件加载阶段注册。

### 卸载顺序

fiber 卸载（`Unload`）：

1. 先 `await Task.Yield()`（保证异步一致性，避免同步完成的死锁）；
2. `Disposables.DrainReverse()` 逆序取走所有 effect；
3. 逐个 `await` 其 disposer（`RunDisposerSafe` 捕获并记录异常到日志，不中断卸载）；
4. 清空 `Store`，根据 epoch 决定是彻底卸载还是重新加载。

### 父级级联

`ctx.Plugin()` 本身注册为父 fiber 的一个 effect（`"ctx.plugin()"`），因此父插件卸载时子插件自动卸载，且子插件的卸载**先于**父插件剩余 disposer —— 依赖关系自然成立。

### 非活跃保护

在已销毁的上下文上调用 `ctx.Effect` 会抛 `InactiveEffectException`（`AssertActive`）。`ctx.On`/`ctx.Provide` 等内部也依赖同一机制。

## 实践建议

- 给 effect 起有意义的名字（`label`），便于 `Fiber.GetEffects()` 诊断。
- 资源获取放在 setup 内、释放放在 disposer 内，保证"获取/释放"配对。
- 需要跨事件流持续注册资源时用异步生成器。
- 不要让 disposer 抛出未捕获异常 —— 卸载流程会记录异常但继续执行其余 disposer。

# Context 与作用域

`Context` 是 CordiSharp 的核心对象，对应 cordis 的 `ctx`。它把所有能力聚合在一起：作用域、服务、事件、插件、effects、日志。

## 创建上下文

```csharp
var root = Context.Create();        // 根上下文
var child = root.Extend();          // 子上下文（共享作用域）
```

`Context.Create()` 会创建根上下文并挂载根 fiber、事件/反射/注册表/日志四个服务。

## 属性

| 属性 | 类型 | 说明 |
| --- | --- | --- |
| `Root` | `Context` | 整棵上下文树的根 |
| `Parent` | `Context?` | 创建本上下文的父上下文（根为 `null`） |
| `Fiber` | `Fiber` | 拥有本上下文的 fiber |
| `Events` | `EventsService` | 事件服务（整棵根树共享） |
| `LoggerService` | `LoggerService` | 日志服务（整棵根树共享） |
| `Reflect` | `ReflectService` | 反射/服务注册服务（整棵根树共享） |
| `Registry` | `RegistryService` | 插件注册表服务（整棵根树共享） |
| `ServiceProvider` | `IServiceProvider?` | 挂载的 MSDI provider（用于插件构造） |
| `Name` | `string` | 显示名（派生自所属 fiber） |
| `Filter` | `Func<object?, bool>?` | 上下文过滤器（作为事件 thisArg 时使用） |

静态成员：

- `Context.Is(object value)` —— 判断值是否为 `Context`
- `Context.Create()` —— 创建根上下文

## 作用域：`Extend` / `Isolate` / `Intercept`

作用域是 CordiSharp 组合性的基础，三种方式产生子上下文，语义与 cordis 完全对齐：

### `Extend()`

创建共享 isolate/intercept 作用域的子上下文。子上下文与父共享服务视图：

```csharp
var a = root.Extend();
a.Provide("greeter", new Greeter());
root.Get("greeter");   // ✅ 可见（共享 isolate 作用域）
```

也可携带额外属性 `Extend(IReadOnlyDictionary<string, object?>)`：

```csharp
var a = root.Extend(new Dictionary<string, object?> { ["filter"] = (Func<object?, bool>)(x => ...) });
```

其中 `filter` 键会同时设置 `Context.Filter`。

### `Isolate(name)`

为指定**服务名**创建隔离作用域：父作用域提供的该服务对子不可见，反之亦然。这是 cordis 依赖图的边界：

```csharp
var a = root.Extend();
a.Provide("greeter", new Greeter());

var b = root.Isolate("greeter");   // 隔离 "greeter" 这个名字
b.Get("greeter");                  // ❌ 抛 ServiceResolutionException
```

隔离是按名字的：隔离 `"greeter"` 不影响其他服务名。`Isolate(name, label)` 可显式指定 `IsolateToken`，两个作用域共享同一 token 时视为同一隔离。

### `Intercept(name, config)`

为指定服务名注入"拦截配置"，常用于给某个服务/插件准备参数：

```csharp
var a = root.Intercept("greeter", new GreeterConfig { Message = "hi" });
```

## 服务访问

`Context` 提供与 cordis 代理语义等价的强类型 API：

| 成员 | 说明 |
| --- | --- |
| `this[string name]` | 索引器，get 走 `Get`，set 走 `Set` |
| `Get(string name, bool strict = true)` | 解析服务；解析失败抛 `ServiceResolutionException` |
| `Get<T>(string name, bool strict = true)` | 强类型解析 |
| `Set(string name, object? value)` / `Set<T>` | 更新已存在服务的值 |
| `Provide(name, value, check?)` / `Provide<T>` | 注册服务，返回可释放句柄 |
| `Accessor(name, options)` | 声明访问器属性（get/set 重定向） |
| `Mixin(source, mixins)` | 把某个服务的成员暴露为访问器 |

```csharp
ctx.Provide("greeter", this);
var g = ctx.Get<Greeter>("greeter");
ctx.Set("greeter", newGreeter);
```

服务的完整语义见[服务系统](04-services.md)。

## 插件 API

| 成员 | 说明 |
| --- | --- |
| `Plugin(object plugin, object? config = null)` | 加载插件（类/委托/对象） |
| `Plugin<TConfig>(Action<Context, TConfig> plugin, config)` | 委托插件（无返回值） |
| `Plugin<TConfig>(Func<Context, TConfig, object?> plugin, config)` | 委托插件（可返回 disposer） |
| `Plugin(Func<Context, object?> plugin)` | 无配置委托插件 |
| `Plugin(Action<Context> plugin)` | 无配置委托插件 |
| `Plugin<TPlugin, TConfig>(config)` | 泛型强类型插件，`TPlugin : IPlugin<TConfig>, new()` |
| `Inject(IEnumerable<string> deps, Func<Context, object?, object?> callback)` | 声明依赖的委托插件 |
| `RegistryDelete(object plugin)` | 注销插件运行时（同步触发 fiber 卸载） |
| `RegistryDeleteAsync(object plugin)` | 注销并等待所有 fiber 卸载完成 |

## Effect API

| 成员 | 说明 |
| --- | --- |
| `Effect(Func<object?> setup, string? label = null)` | 创建 effect |
| `Effect(Func<IEnumerable<object?>> setup, label?)` | 同步生成器 effect |
| `Effect(Func<IAsyncEnumerable<object?>> setup, label?)` | 异步生成器 effect |

详见 [Effect 生命周期](06-effects.md)。

## 事件 API

| 成员 | 说明 |
| --- | --- |
| `On<TArgs>(EventKey<TArgs>, Func<Context, TArgs, object?>, options?)` | 注册监听，返回可释放句柄 |
| `OnAsync<TArgs>(key, Func<Context, TArgs, Task<object?>>, options?)` | 注册异步监听 |
| `Once<TArgs>(key, listener, options?)` | 一次性监听 |
| `OnWaterfall<TArgs>(key, Func<TArgs, Func<object?>, object?>, options?)` | 注册 waterfall 监听 |
| `OnUpdate(Func<object?, bool, Func<object?>, object?> hook, options?)` | 注册 `internal/update` 钩子（配置变更拦截） |
| `Emit<TArgs>(key, args)` / `Emit(thisArg, key, args)` | 同步分发 |
| `Parallel<TArgs>(key, args)` | 并发分发，聚合错误 |
| `Serial<TArgs>(key, args)` | 顺序分发，遇 bail 停止 |
| `Bail<TArgs>(key, args)` | 同步顺序分发 |
| `Waterfall<TArgs>(key, args, fallback)` | 链式分发（`next()`） |

详见 [事件系统](05-events.md)。

## 日志 API

| 成员 | 说明 |
| --- | --- |
| `Logger(name = null)` | 获取命名日志器（默认以上下文名命名） |

```csharp
ctx.Logger().Info("hello %s", "world");
```

详见 [日志系统](10-logging.md)。

## 注意事项

- **服务共享**：`Events`、`LoggerService`、`Reflect`、`Registry` 挂在根上下文上，所有子上下文共享同一实例；`Isolate` 只隔离服务值，不隔离这些服务本身。
- **上下文与 fiber**：每个插件 fiber 拥有自己的 `ctx`（`ExtendForFiber` 创建），插件内拿到的 `ctx` 与 `root` 不同；`ctx.Fiber` 指向所属 fiber。
- **`Context.Is`**：静态判断，等价于 `value is Context`。
- **`Get` 的 strict 语义**：`strict: true`（默认）时，若服务的提供 fiber 尚未 `Active`，也会被当作不可解析；调试时可传 `strict: false` 获取原始值。

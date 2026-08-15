# 服务系统

服务是插件之间共享状态与能力的机制。服务按**名字**注册，按**隔离 token** 作用域隔离；提供/移除服务会通知依赖它的 fiber（触发加载/卸载）。

## 核心 API

### `ctx.Provide(name, value, check?)`

在**当前 fiber** 上注册一个服务实现，返回可释放句柄（fiber 卸载或句柄释放时自动注销并通知依赖方）：

```csharp
var disposable = ctx.Provide("greeter", greeterInstance);
disposable.Dispose();   // 注销服务
```

`check` 参数是可选的可用性函数：返回 `false` 时该服务视为不可用（依赖它的 fiber 不会加载）。

### `ctx.Get<T>(name, strict = true)`

解析服务。在插件上下文内，服务从 fiber 链上查找：

- 先在 fiber 自身的 `Store` 中找；
- 再沿父上下文链向上找，校验 isolate token 一致；
- 严格模式下，提供服务的 fiber 必须 `Active`；
- 解析失败抛 `ServiceResolutionException`。

```csharp
var greeter = ctx.Get<Greeter>("greeter");
```

`Get` 会先查 `Extra`（`Extend(meta)` 带来的额外属性），再查访问器属性，最后查服务链。

### `ctx.Set(name, value)`

更新**已存在**服务的值（服务必须已注册且属于当前 fiber，否则抛异常）。`Set` 走 `internal/set` waterfall，可被钩子拦截：

```csharp
ctx.Set("greeter", newGreeter);
```

### 索引器

```csharp
ctx["greeter"] = greeter;     // Set
var g = ctx["greeter"];       // Get
```

## 隔离：`IsolateToken`

服务的可见性由 `IsolateToken` 决定。`ctx.Isolate(name)` 会为这个名字创建一个新 token；两个作用域共享同一 token 时，服务互相可见。

```csharp
var a = root.Extend();
a.Provide("greeter", g1);

var b = root.Isolate("greeter");   // 新 token
b.Get("greeter");                  // ❌ 抛异常

var c = root.Isolate("greeter", tokenFromA);  // 共享 token → 可见
```

`Service.FilterContext` 与事件 thisArg 过滤同样基于 isolate token 的引用相等比较。

## `Accessor`：访问器属性

声明一个"属性"（get/set 重定向），`Get` 命中访问器时调用其 `Get` 委托：

```csharp
ctx.Accessor("temperature", new AccessorOptions
{
    Get = c => sensor.Current,
    Set = (c, v) => { sensor.Target = (double)v!; return true; },
});
```

## `Mixin`：成员转发

把一个服务的成员（属性/字段/方法）暴露为访问器属性：

```csharp
ctx.Mixin("config", ["port", "host"]);
// 之后 ctx.Get("port") 等价于 ((Config)ctx.Get("config")).Port
```

`GetMember`/`SetMember` 支持字典、属性、字段与无参方法。

## `Service` 基类

继承 `Service` 的类在构造时自动 `ctx.Provide(Name, this, Check)`：

```csharp
public sealed class Greeter : Service
{
    public Greeter(Context ctx) : base(ctx) { }

    protected override bool Check() => true;         // 可用性
    protected override object? Init() => () => Cleanup();  // 生命周期（可返回 disposer / Task）
    public override bool FilterContext(Context target) => /* 默认按同名 isolate 过滤 */ true;
}
```

- `Ctx`：创建时的上下文；`Name`：服务名（`[Service("name")]` 或类名）。
- `Init()` 在构造后由框架调用（`RunInit`），返回值纳入 fiber 卸载流程。
- 服务实现 `IContextFilter`，作为事件 thisArg 时只把事件分发给同 isolate 的监听。

## `ReflectService` 与 `Impl`

`ctx.Reflect` 暴露底层服务注册表：

| 成员 | 说明 |
| --- | --- |
| `Get(ctx, name, strict)` | 解析服务值 |
| `Set(ctx, name, value)` | 更新服务值 |
| `Provide(ctx, name, value, check)` | 注册服务 |
| `Accessor(ctx, name, options)` | 声明访问器 |
| `Mixin(ctx, source, mixins)` | 声明成员转发 |

`Impl` 描述一个已注册的服务实现：

| 成员 | 说明 |
| --- | --- |
| `Name` | 服务名 |
| `Fiber` | 提供该服务的 fiber |
| `Value` | 服务值 |
| `Check` | 可用性检查 |

内部实现：`Store` 是 `IsolateToken → Impl` 的字典；`Props` 是 `名字 → PropertyDef`（`service` 或 `accessor`）。

## 依赖通知（依赖图）

`Provide` 注册服务且 fiber `Active` 时触发 `Notify`：

1. 遍历所有插件 runtime 的 fibers；
2. 对每个注入（`Inject.ContainsKey(name)`）了该服务名且处于**同一 isolate** 的 fiber，执行 `CheckImpl` + `Refresh`；
3. `Refresh` 重新计算 epoch，触发依赖 fiber 加载/卸载；
4. 发出 `internal/service` 事件。

注销服务（`Provide` 返回的句柄释放）同样通知依赖方，并等待依赖 fiber 稳定（`Await`）。

## 服务生命周期要点

- 服务跟随提供它的 fiber 生存：fiber 卸载 → 服务注销 → 依赖它的 fiber 卸载。
- 同名服务在**同一 isolate 作用域**内只能注册一次，重复注册抛 `CordisException`。
- `strict` 模式（默认）下 `Get` 要求提供服务的 fiber 处于 `Active` 状态；调试时可传 `false`。
- 通过 `ctx.Extend()` 扩展的上下文共享服务视图；`Isolate` 是唯一的隔离手段。

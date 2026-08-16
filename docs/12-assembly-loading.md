# 外部程序集加载与卸载（AssemblyLoader）

CordiSharp 支持把**外部插件程序集**加载进一个**可回收的
`AssemblyLoadContext`**（collectible ALC），用完可以整包卸载、回收其类型。
关键是：**"加载外部程序集"本身也是一个插件** —— 由
`AssemblyLoaderService`（服务名 `"loader"`）提供，其他插件用
`[Inject("loader")]` 即可依赖它。

## 快速上手

```csharp
using CordiSharp;
using CordiSharp.Loading;

var root = Context.Create();

// 1. 加载 loader 插件（普通插件，走正常 fiber 管线）
await root.Plugin(typeof(AssemblyLoaderService));

// 2. 其他插件注入 loader 服务（类级 [Inject("loader")] 或 ctx.Get）
var loader = root.Get<AssemblyLoaderService>("loader")!;

// 3. 把外部程序集加载进一个可回收 ALC，并发现其中的插件
var set = loader.LoadAssembly(@"plugins/MyPlugin.dll");
foreach (var info in set.Plugins) Console.WriteLine(info.Name);   // [Plugin] 类 / Service 子类 / IPlugin 实现

// 4. 创建插件实例（返回可 await 的 AssemblyPluginHandle）
var handle = set.LoadPlugin("my-plugin", new Dictionary<string, object?> { ["Foo"] = 1 });
await handle;

// 5. 卸载整个程序集：释放引用并 Unload ALC
await set.UnloadAsync();
```

## 设计要点

### ALC 隔离与共享程序集

- 每个 `LoadAssembly` 调用创建一个**独立可回收 ALC**，程序集及其类型与宿主隔离。
- **共享程序集**（CordiSharp 核心、BCL、宿主依赖）解析回默认 ALC，因此
  `IPlugin<T>`、`Service`、`Context` 等在两边是**同一个类型身份**，
  反射分发与依赖注入跨边界正常工作。
- 插件**自己的依赖**从插件目录探测（`Resolving` 事件）。

### 元数据不进静态注册表

ALC 里的插件类型**不会**被注册进静态的
`PluginMetadataRegistry`（那是为编译期生成元数据设计的）。加载时通过反射
构建插件名、配置类型、schema 与 inject 列表，作用域限定在
`AssemblyPluginSet` 内。这样静态字典永远不会锚定程序集的类型，
ALC 卸载才可能成功。

### 卸载编排（`UnloadAsync`）

1. 逆序释放该程序集创建的所有 fiber（插件实例、effects、服务、事件订阅随之清理）；
2. 清空引用：`AssemblyPluginSet` 置空内部程序集/ALC/描述符，所有
   `AssemblyPluginHandle` **自动脱离**（内部 fiber 引用置 null）；
3. 调用 `ALC.Unload()`；
4. （`verify` 参数，默认开启）有界的强制 GC 循环 + `WeakReference`
   校验，若仍存活抛 `AssemblyUnloadException`（这是开发期排查强引用的手段；
   `DisposeAsync()` 走非严格路径，不抛）。

> 注意：某些沙箱 / CI 环境（例如受限的进程包装）连"空 collectible ALC"都无法完成
> 回收，此时 `verify: true` 会误报。遇到这种情况可先用一个空 ALC 自检，
> 或改用 `verify: false`。

### 级联卸载

loader 插件本身被卸载（如 `RegistryDeleteAsync(typeof(AssemblyLoaderService))`
或宿主关闭）时，它加载的所有程序集被级联卸载。

## API

| 成员 | 说明 |
| --- | --- |
| `AssemblyLoaderService` | loader 插件；`[Service("loader")]` 注册为服务 |
| `LoadAssembly(path)` | 加载外部程序集到新 ALC，返回 `AssemblyPluginSet` |
| `AssemblyPluginSet.Plugins` | 发现的插件描述符（`AssemblyPluginInfo`） |
| `AssemblyPluginSet.LoadPlugin(name, config?)` | 创建插件 fiber，返回 `AssemblyPluginHandle` |
| `AssemblyPluginSet.UnloadAsync(verify = true)` | 卸载整包；verify 开启时做 GC 回收校验并抛 `AssemblyUnloadException` |
| `loader.UnloadAsync(set)` / `set.DisposeAsync()` | 卸载整包（Dispose 为非严格清理路径） |
| `AssemblyPluginHandle` | 可 await/Update/Restart/Dispose；卸载后自动脱离 |
| `AssemblyUnloadException` | 卸载后仍有强引用时抛出 |

配置对象可以是：插件自身的配置类型实例、宿主侧的 POCO（属性同名会被拷贝成插件
类型实例）、或 `IDictionary<string, object?>`。都会先经 schema 校验。

## 弱引用桥（获取服务只拿接口）

为了让其他插件/宿主代码**不会意外持有外部程序集的类型**，加载外部程序集的
fiber 提供的服务通过 **弱引用桥** 获取：

```csharp
// 宿主侧定义契约接口（放在共享/宿主程序集，插件无需实现它）
public interface IGreeter { string Greet(string name); }

var greeter = set.GetService<IGreeter>("greeter");   // 返回桥，不是插件实例
greeter.Greet("cordis");                             // 桥按方法名/参数个数转发到插件内部服务

await set.UnloadAsync();
greeter.Greet("cordis");   // ❌ 抛 PluginUnloadedException（桥已被撤销）
```

要点：

- `GetService<T>(name)` 要求 `T` 是接口（契约定义在宿主侧，插件**不需要**
  实现它 —— 插件实现它反而会产生编译期引用，钉死程序集）。桥内部通过反射按
  **方法名 + 参数个数**转发到插件内部类型的方法（属性 get/set 同样支持）。
- 桥只持有插件服务的 **`WeakReference`**，不持有任何插件类型/方法句柄：
  即使忘记释放桥，它也不会阻止 ALC 卸载。
- 卸载时 loader 撤销该程序集所有已发出的桥；撤销后（或插件实例已被回收后）任何
  调用抛 `PluginUnloadedException`。
- 返回值/参数使用可跨程序集的类型（框架类型、共享 DTO）；契约方法签名尽量与
  插件方法一致（名字 + 参数个数一致即可，参数类型尽量用相同共享类型）。

> 这一层对应设计中的"包装接口"：引用在获取服务时只拿到接口，接口内部桥接到
> 程序集内部类型。若你的契约接口被插件**直接实现**（共享契约程序集模式），
> 桥仍然可用（转发到实现方法）；此时注意释放强引用即可。

## 服务目录（源生成器，接口→内部类型映射）

随包附带的**服务目录源生成器**（`CordiSharpServiceCatalogGenerator`）为插件
程序集中的 `[Service]` 子类生成
`CordiSharp.Generated.PluginServiceCatalog`：把**跨程序集的契约接口**映射到
插件内部的实现类型与服务名。这样宿主无需知道服务名，直接按契约解析：

```csharp
// 共享契约程序集（插件与宿主都引用）：public interface IGreeter { string Greet(string name); }
// 插件内部：[Service("greeter")] public sealed class GreeterService(Context ctx) : Service(ctx), IGreeter

var set = loader.LoadAssembly(@"plugins/MyPlugin.dll");
await set.LoadPlugin("GreeterService");

IGreeter greeter = set.GetService<IGreeter>();   // 目录自动解析服务名 "greeter"，返回桥
greeter.Greet("cordis");
```

生成规则：

- 只映射**契约接口声明在插件程序集之外**的（共享/宿主契约）；插件本地接口与
  CordiSharp 框架接口（`IPlugin`、`IContextFilter` 等）被跳过。
- 目录条目为 `ServiceCatalogEntry(Contract, ServiceName, Impl)`，可通过
  `set.ServiceCatalog` 查看；`Impl` 是插件内部类型（卸载后即失效，
  不要长期持有）。
- 目录数据由 loader 在加载时**反射读取、作用域限定在 `AssemblyPluginSet`**，
  不进入任何静态注册表，卸载时随 set 一起释放。
- `GetService<T>()`（免名）依赖目录；`GetService<T>(name)`（具名）
  不依赖目录，适用于插件未用生成器、或契约完全由宿主临时定义（按方法名适配）的场景。

## [Import] / [Inject]：契约接口由源生成器生成

共享契约（如手写 `IGreeter`）不是必须的：**源生成器在引用方侧**生成一切。

### 宿主入口 —— `[assembly: Import(name, Alias?)]`（程序集级）

宿主（应用根 Context 的所有者）引用插件库，在程序集级标注：

```csharp
[assembly: Import("greeter")]
[assembly: Import("echo", Alias = "Echo")]

// 源生成器（CordiSharpImportGenerator）：
//   1. 在插件库里找到 [Service("greeter")] 的实现类型 GreeterService（T1）
//   2. 生成镜像接口 IGreeterService（T1 的公共方法/属性）+ 弱引用桥实现
//   3. 生成 C#14 扩展属性 ctx.Greeter / ctx.Echo（名称 = 服务名 PascalCase，或别名）

await set.LoadPlugin("GreeterService");
var greeting = ctx.Greeter.Greet("cordis");   // root 解析（宿主视角，忽略 isolate）
```

### 插件侧 —— `[Inject(name, Alias)]`（类级，别名触发访问器）

"注入即导入"：既有依赖语义（fiber 保持 Pending 直到服务可用、服务移除自动卸载），
又额外生成类型安全访问器，解析走 **fiber 链（isolate 感知）**：

```csharp
[Inject("greeter", Alias = "Greeter")]
public class DependentPlugin : IPlugin<...>
{
    public void Load(Context ctx, ...) => ctx.Greeter.Greet("cordis");
}
```

要点（两者共用）：

- `ctx.XX` 是 C#14 扩展属性；接口名 = `I` + 实现类型名；访问器名默认 =
  服务名 **PascalCase**（`"greeter"` → `ctx.Greeter`），**别名**可覆盖
  （`Alias = "Echo"` → `ctx.Echo`），服务仍按原名解析。
- 生成的桥持有插件服务的 **`WeakReference`**（不 pin），按方法名转发；
  卸载后调用抛 `PluginUnloadedException`。
- 生成接口**不引用插件库的本地类型**：签名中出现插件库类型的成员会被跳过。
- 宿主仅编译期引用插件库，运行时从不触碰其类型 → 插件库只存在于可回收 ALC 中。
- 找不到实现类型报 `CORDIS004`；别名非法标识符报 `CORDIS005`。

## 卸载规则（重要）

**卸载时不能有目标程序集的任何强引用**。以下引用必须在使用结束后释放：

- `AssemblyPluginSet` / `AssemblyPluginInfo` / `AssemblyPluginHandle`
  （handle 会自动脱离，set/info 卸载后成员置空，但不要长期持有）；
- 从插件拿到的**实例**、**配置对象**、`Type`（如 `info.ConfigType`）；
- 插件注册的**事件监听器**、**服务值**（fiber 卸载时自动反注册，但别的插件若
  一直持有返回值/服务实例也会 pin）；
- 插件代码里**还在运行的异步任务**（卸载前应让插件停止后台工作）。

若 `UnloadAsync`（verify=true）抛出 `AssemblyUnloadException`，说明仍有强引用，
按上述清单排查。

## 注意事项

- 日志缓冲（`LoggerService.Buffer`）会短暂持有 fiber 引用；卸载时 loader 会清除
  目标 fiber 的缓冲条目。
- 与宿主同名的共享依赖取宿主版本（简单名解析）；版本冲突是插件系统的通用边界。
- 通过 loader 创建的 fiber 已注册进 `RegistryService`，因此 inject/isolate 的
  服务变更通知（依赖图刷新）在 ALC 插件之间正常生效。
- 插件程序集若也是用源生成器编译的，其生成的 `PluginRegistrations` 会被静态
  注册扫描**跳过**（collectible ALC 守卫），不会污染静态注册表。

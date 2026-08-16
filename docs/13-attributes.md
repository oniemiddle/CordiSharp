# 特性（Attribute）参考

CordiSharp 的特性分为两类：**插件/服务声明**（运行时 + 编译期生成器/分析器消费）与
**配置 schema**（编译期生成器消费）。全部特性随 `CordiSharp` 包提供。

## 总览

| 特性 | 命名空间 | 目标 | 作用 |
| --- | --- | --- | --- |
| `[Plugin]` | `CordiSharp.Registry` | 类 | 标记插件类（名称 + 配置类型） |
| `[PluginConfig]` | `CordiSharp.Schema` | 类 | 标记配置类型（供生成器构建 schema） |
| `[Inject]` | `CordiSharp.Registry` | 类 / 属性（可重复） | 声明必需的服务注入 |
| `[Service]` | `CordiSharp.Registry` | 类 | 指定 `Service` 子类的服务名 |
| `[Import]` | `CordiSharp.Registry` | **程序集**（可重复） | 宿主入口导入外部插件服务（生成器生成契约接口，root 解析） |
| `[Inject]` 的 `Alias` | `CordiSharp.Registry` | 类级 | 附加类型安全访问器（isolate 感知） |
| `[DefaultValue]` | `CordiSharp.Schema` | 属性 | 配置属性的默认值 |
| `[Required]` | `CordiSharp.Schema` | 属性 | 标记配置属性为必填 |

## 插件与服务声明

### `[Plugin(name?)]` —— 标记插件类

- **目标**：类（不可继承）
- **参数**：`name`（可选，插件名，缺省用类名）；属性 `ConfigType`（可选，显式指定配置类型）
- **作用**：声明一个插件类。源生成器（`CordiSharpPluginGenerator`）为其生成编译期元数据
  （插件名、配置类型、schema、注入列表），运行时优先使用；分析器校验公共构造函数（`CORDIS003`）。

```csharp
[Plugin("greeter")]                          // 插件名 "greeter"
[Plugin]                                     // 插件名 = 类名 GreeterPlugin
[Plugin("x", ConfigType = typeof(XConfig))]  // 显式配置类型（缺省从 IPlugin<TConfig> 推断）
public sealed class GreeterPlugin : IPlugin<GreeterConfig>
{
    public void Load(Context ctx, GreeterConfig config) { }
}
```

### `[Inject(name, config?, Alias?)]` —— 声明必需的服务注入

- **目标**：类 / 属性（可重复）
- **参数**：`name`（服务名）；`config`（可选，注入配置）；`Alias`（可选，**类级**，
  请求额外生成类型安全访问器）
- **作用**：插件加载前，`name` 服务必须在匹配的 isolate 作用域内可用，否则 fiber 保持
  `Pending`；服务被移除时插件自动卸载（依赖图核心）。属性级注入在构造后自动赋值。
  **`Alias` 触发访问器**：源生成器额外生成 `ctx.<Alias>`（镜像接口 + 弱桥），解析走
  **fiber 链（isolate 感知）**——"注入即导入"。

```csharp
[Inject("database")]                          // 类级：需要 "database" 服务（仅依赖）
[Inject("greeter", Alias = "Greeter")]        // 类级：依赖 + ctx.Greeter 访问器
[Inject("logger", LogLevel.Debug)]            // 携带注入配置
public class WorkerPlugin : IPlugin<WorkerConfig>
{
    [Inject("database")]                       // 属性级：构造后自动注入
    public IDatabase? Db { get; set; }
}
```

- 空名称报 `CORDIS002`（错误）；`Alias` 非法标识符报 `CORDIS005`（错误）。

### `[Service(name)]` —— `Service` 子类的服务名

- **目标**：类
- **参数**：`name`（服务名）
- **作用**：`Service` 子类自动把自己注册为服务；缺省用类名，`[Service(name)]` 显式改名。
  该特性也是**服务目录生成器**（`CordiSharpServiceCatalogGenerator`）与 **`[Import]`
  宿主生成器**（`CordiSharpImportGenerator`）定位实现类型的依据。

```csharp
[Service("greeter")]
public sealed class GreeterService(Context ctx) : Service(ctx)
{
    public string Greet(string name) => $"Hello, {name}!";
}
```

### `[Import(name, Alias?)]` —— 宿主入口导入外部插件服务（程序集级）

- **目标**：**程序集**（可重复）—— `[assembly: Import(...)]`
- **参数**：`name`（服务名，须匹配插件库中的 `[Service(name)]`）；`Alias`（可选，生成
  的 `ctx.<Alias>` 属性名，缺省用 `name`）
- **作用**：宿主入口（应用根 Context 的所有者）导入外部插件服务；源生成器在宿主侧找到
  实现类型，生成**镜像接口**、**弱引用桥**（不 pin）和 **C#14 扩展属性** `ctx.<name>`。
  解析走 **root 上下文**（宿主视角，忽略 isolate）。
- 插件侧请用 `[Inject(name, Alias)]`（isolate 感知）。

```csharp
[assembly: Import("greeter")]
[assembly: Import("echo", Alias = "Echo")]   // 别名：访问器为 ctx.Echo，服务仍按 "echo" 解析

// 生成：IGreeterService / IEchoService 接口 + ctx.greeter / ctx.Echo 访问器
var greeting = ctx.greeter.Greet("cordis");
```

- 找不到实现类型报 `CORDIS004`（生成器，错误）；别名非法标识符报 `CORDIS005`
  （分析器，错误，程序集级与 Inject 别名都查）。

## 配置 schema

### `[PluginConfig]` —— 标记配置类型

- **目标**：类
- **作用**：标记配置 POCO，供源生成器构建编译期 schema（`[DefaultValue]`/`[Required]`
  一并处理），运行时经 `Schema` 校验/合并配置。配置类型的可写属性必须能被 schema
  表示（`CORDIS001` 警告不能表示的属性）。

```csharp
[PluginConfig]
public sealed class GreeterConfig
{
    [DefaultValue("hi")]  public string? Message { get; set; }
    [Required]            public string? Target { get; set; }
}
```

### `[DefaultValue(value)]` —— 配置属性默认值

- **目标**：属性
- **参数**：`value`（默认值，编译期字面量：字符串、数字、布尔、枚举）
- **作用**：配置缺省时使用该值；生成器写入 schema（`WithDefault`），运行时校验生效。

### `[Required]` —— 配置属性必填

- **目标**：属性
- **作用**：标记配置属性为必填（不可为 null）；缺失时 schema 校验报错。

## 与生成器/分析器的关系

| 特性 | 生成器 | 分析器 |
| --- | --- | --- |
| `[Plugin]` | `CordiSharpPluginGenerator` 生成注册元数据 | `CORDIS003` 公共构造函数 |
| `[PluginConfig]` | 生成配置 schema | `CORDIS001` 无法表示的属性类型 |
| `[Inject]` | 收集注入列表 | `CORDIS002` 空名称 |
| `[Service]` | `CordiSharpServiceCatalogGenerator` 目录 / `CordiSharpImportGenerator` 定位实现 | — |
| `[Import]` | `CordiSharpImportGenerator` 生成接口 + 桥 + 访问器 | `CORDIS004`（生成器）/ `CORDIS005` 别名 |
| `[DefaultValue]` | schema 默认值 | — |
| `[Required]` | schema 必填 | — |

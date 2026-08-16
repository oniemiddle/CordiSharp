# 源生成器与分析器

`CordiSharp` 包内附 Roslyn 源生成器（`CordiSharp.Generators`）与 Roslyn 分析器（`CordiSharp.Analyzers`），随包自动生效，无需额外引用。它们提供编译期元数据与编译期诊断。

## 源生成器

### 工作方式

对每个标注 `[Plugin]` 的类，生成器：

1. 读取插件名（`[Plugin("name")]`，缺省用类名）；
2. 从 `IPlugin<TConfig>` / `IAsyncPlugin<TConfig>` 提取配置类型；
3. 从 `[PluginConfig]` 配置类的公开可写属性**编译期生成 schema 表达式**（含 `[DefaultValue]`）；
4. 收集类级 `[Inject]` 列表（名称 + 注入配置）；
5. 生成 `CordiSharp.Generated.PluginRegistrations.RegisterAll()`，调用 `PluginMetadataRegistry.Register` 注册。

生成的注册在运行时由 `PluginMetadataRegistry.EnsureGeneratedRegistrations()` 触发（首次解析插件时扫描所有已加载程序集寻找 `CordiSharp.Generated.PluginRegistrations` 并调用 `RegisterAll`）。

### 生成内容示例

对于：

```csharp
[Plugin("greeter")]
public sealed class GreeterPlugin : IPlugin<GreeterConfig> { ... }

[PluginConfig]
public sealed class GreeterConfig
{
    [DefaultValue("hi")]
    public string? Message { get; set; }
}
```

生成器产生（示意）：

```csharp
global::CordiSharp.PluginMetadataRegistry.Register(
    typeof(global::GreeterPlugin),
    new global::CordiSharp.PluginMetadata(
        "greeter",
        typeof(global::GreeterConfig),
        global::CordiSharp.Schema.Schema.Object(
            new Dictionary<string, global::CordiSharp.Schema.Schema> {
                ["Message"] = global::CordiSharp.Schema.Schema.String().WithDefault("hi")
            }),
        new KeyValuePair<string, object?>[] { }));
```

### 运行时优先顺序

`RegistryService.BuildRuntimeFromType` 构建插件运行时：

1. 查 `PluginMetadataRegistry`（生成器元数据）；
2. 元数据缺省时回退反射读取 `[Plugin]` / `[Inject]` 特性；
3. schema 缺省时用 `Schema.FromType(configType)` 反射构建。

因此**生成器元数据优先于反射**，且 `[Inject]` 同时来自元数据与反射扫描（合并）。

## `PluginMetadata` / `PluginMetadataRegistry`

| 成员 | 说明 |
| --- | --- |
| `PluginMetadata.Name` | 插件名 |
| `PluginMetadata.ConfigType` | 配置类型 |
| `PluginMetadata.ConfigSchema` | 编译期生成的 schema |
| `PluginMetadata.Inject` | 注入列表（名字 → 配置） |
| `PluginMetadataRegistry.Register(type, metadata)` | 注册元数据（生成代码调用） |

```csharp
PluginMetadataRegistry.Register(typeof(MyPlugin), new PluginMetadata(
    "my-plugin", typeof(MyConfig), Schema.FromType(typeof(MyConfig))));
```

## 分析器

分析器验证插件声明，产生四个诊断：

### CORDIS001 —— 配置属性类型无法用 schema 表示（警告）

`[PluginConfig]` 类的某个可写属性类型不被任何 schema 支持（会按 `Schema.Any()` 处理）：

```
CORDIS001: Property 'Foo' of config type 'MyConfig' has a type that cannot be represented
           by a CordiSharp schema; it will be treated as Schema.Any()
```

### CORDIS002 —— 空的 `[Inject]` 名称（错误）

```csharp
[Inject("")]          // ❌ CORDIS002
public class BadPlugin : IPlugin<Config> { }
```

`[Inject]` 名称不得为空或空白。

### CORDIS003 —— 插件类缺少公共构造函数（错误）

```csharp
[Plugin]
public class BadPlugin : IPlugin<Config>
{
    private BadPlugin() { }   // ❌ CORDIS003：需要公共构造函数
}
```

插件类（标注 `[Plugin]` 或实现 `IPlugin<T>`）必须至少有一个公共构造函数，否则 CordiSharp 无法实例化。

### CORDIS005 —— `[Import]` 的 `Alias` 不是合法标识符（错误）

`[Import]` 的可选别名用作生成的 `ctx.&lt;Alias&gt;` 扩展属性名，必须是合法的 C# 标识符：

```csharp
[Import("greeter", Alias = "123 bad")]   // ❌ CORDIS005
[Import("greeter", Alias = "Greeter")]   // ✅
```

## 禁用 / 关闭

按标准 Roslyn 方式处理：

- 关闭特定诊断：`NoWarn` 或 `#pragma warning disable CORDIS001`
- 完全关闭分析器/生成器：在 csproj 中排除（不建议，会失去编译期元数据）：

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\CordiSharp\CordiSharp.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="true" />
</ItemGroup>
```

## 注意事项

- 生成器/分析器以 `netstandard2.0` 目标打入 `analyzers/dotnet/cs`，对消费者透明。
- 生成器只在类实现 `IPlugin<TConfig>`/`IAsyncPlugin<TConfig>` 时生成 schema；纯委托/对象插件不生成元数据，运行时的 schema 由委托参数类型或 `PluginAttribute.ConfigType` 推断。
- `EnsureGeneratedRegistrations` 在首次解析时执行一次；若插件程序集是延迟加载的，确保解析前程序集已加载（宿主通常无需关心）。

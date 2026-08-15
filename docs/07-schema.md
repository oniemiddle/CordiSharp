# 配置 Schema

CordiSharp 为插件配置提供运行时校验与强制转换（`CordiSharp.Schema` 命名空间）。配置对象是类型化 POCO；schema 校验并保留实例（字典会被强制转换为普通对象语义）。

## 从 CLR 类型构建

最常见的用法是让框架自动从配置类型构建 schema：

```csharp
using CordiSharp.Schema;

[PluginConfig]
public sealed class GreeterConfig
{
    [DefaultValue("hi")]
    public string? Message { get; set; }
}

// 显式构建
var schema = Schema.FromType(typeof(GreeterConfig));

// 或隐式转换
Schema schema2 = typeof(GreeterConfig);
```

- 类型标注 `[PluginConfig]` 后，源生成器会在编译期生成 schema 表达式（见[源生成器与分析器](08-generator-analyzers.md)）；
- 没有生成器时回退到 `Schema.FromType` 反射构建；
- 插件加载时 `Fiber.ResolveConfig` 用 `runtime.ConfigSchema.Parse(config)` 校验/合并配置，失败抛 `SchemaValidationException`。

## 类型映射（`Schema.FromType`）

| CLR 类型 | Schema |
| --- | --- |
| `string` | `Schema.String()` |
| `bool` | `Schema.Boolean()` |
| `int` / `long` / `short` / `byte` | `Schema.Integer()` |
| `double` / `float` / `decimal` | `Schema.Number()` |
| 枚举 | `Union` 的各字面量（`Literal`） |
| `T?`（`Nullable<T>`） | 内部 schema 的 `.AsOptional()` |
| 数组 / `List<T>` / `IList<T>` / `IReadOnlyList<T>` | `Schema.Array(元素)` |
| `Dictionary<string, V>` / `IDictionary<K,V>` | `Schema.Record(值)` |
| 嵌套配置类 | `Schema.Object(各属性)`（可读写属性逐个映射） |

嵌套对象属性若带 `[DefaultValue]`，会包一层 `Schema.Default(inner, value)`。递归深度上限 8 层（超出返回 `Any`）。

## Schema 工厂

`Schema` 静态工厂方法：

| 方法 | 说明 |
| --- | --- |
| `Schema.String()` | 字符串 |
| `Schema.Number()` | 数字（`Convert.ToDouble`） |
| `Schema.Integer()` | 整数（`Convert.ToInt64`） |
| `Schema.Boolean()` | 布尔 |
| `Schema.Any()` | 任意值 |
| `Schema.Object(fields, strict = false)` | 对象；`strict` 时额外字段报 `unexpected key` |
| `Schema.Array(item)` | 数组 |
| `Schema.Tuple(params Schema[])` | 元组 |
| `Schema.Union(params Schema[])` | 联合：任一子 schema 通过即可 |
| `Schema.Optional(inner)` | 允许 null |
| `Schema.Default(inner, value)` | null 时使用默认值 |
| `Schema.Transform(inner, fn)` | 校验后转换 |
| `Schema.Record(value)` | 字符串键字典 |
| `Schema.Literal(value)` | 字面量相等校验 |

组合包装器（实例方法）：

- `schema.WithDefault(value)` —— null 时用默认值
- `schema.AsOptional()` —— 允许 null
- `schema.Transform(fn)` —— 校验后转换

## 校验 API

```csharp
// 校验并强制转换；失败抛 SchemaValidationException
var cfg = schema.Parse(input);              // object?
var cfg2 = schema.Parse<GreeterConfig>(input);

// 不抛异常：收集问题
var issues = new List<SchemaIssue>();
var value = schema.Validate(input, issues, "");
if (issues.Count > 0) { /* 处理 */ }
```

| 类型 | 说明 |
| --- | --- |
| `SchemaIssue` | 单条校验问题：`Message` + `Path`（如 `"port"`、`"a.b.0"`） |
| `SchemaValidationException` | `Parse` 失败时抛出；`Issues` 列出全部问题；继承 `CordisException` |

```csharp
try
{
    var config = schema.Parse<GreeterConfig>(raw);
}
catch (SchemaValidationException e)
{
    foreach (var issue in e.Issues) Console.WriteLine(issue);   // "Message (at Path)"
}
```

## `Merge`

对象 schema 的 `Merge(configs...)` 浅合并多个字典（后者覆盖前者同名键）；标量 schema 取最后一个非 null 值。插件 `Update(config)` 时会用 `ResolveConfig` 对配置做 schema 解析（含默认值补全）。

## 特性

| 特性 | 目标 | 说明 |
| --- | --- | --- |
| `[PluginConfig]` | 类 | 标记配置类型，供生成器与 `Schema.FromType` 使用 |
| `[DefaultValue(value)]` | 属性 | 声明默认值，schema 包装为 `Default(inner, value)` |
| `[Required]` | 属性 | 标记必填（不可为 null） |

## 注意事项

- **类型化保留**：对象 schema 校验 POCO 时保留原实例（不产生字典副本）；输入为字典时输出普通 `Dictionary<string, object?>`。
- **强转语义**：字符串 schema 会把输入 `Convert.ToString`；数字/整数无法转换时记为问题并返回 `0`；布尔接受 `bool` 或字符串解析。
- **`CORDIS001`**：`[PluginConfig]` 中存在无法用 schema 表示的类型时，编译器给出警告，该属性按 `Schema.Any()` 处理（见[源生成器与分析器](08-generator-analyzers.md)）。

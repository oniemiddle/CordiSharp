# 日志系统

CordiSharp 内置日志服务（`CordiSharp.LoggerService`），移植自 cordis 的 LoggerService：命名日志器、级别过滤、exporter 与环形缓冲。

## 基本用法

```csharp
// 通过 Context 获取命名日志器（默认以上下文名命名）
ctx.Logger().Info("hello %s", "world");
ctx.Logger("chat").Debug("value = %d", 42);

// 或直接用 LoggerService（等价 ctx 命名）
ctx.LoggerService.Info("booted");
ctx.LoggerService.Warn("config missing, using default");
```

级别方法（`Logger` 与 `LoggerService` 都有）：

| 方法 | 级别 |
| --- | --- |
| `Error(format, args...)` / `Error(Exception)` | `LogLevel.Error` |
| `Warn(format, args...)` | `LogLevel.Warn` |
| `Info(format, args...)` | `LogLevel.Info` |
| `Debug(format, args...)` | `LogLevel.Debug` |

## 格式化占位符

`LoggerService.Format` 支持 cordis 风格占位符：`%s`（字符串）、`%d`/`%i`（整数）、`%f`（浮点）、`%o`/`%O`（JSON 序列化）、`%c`/`%C`（空）、`%%`（百分号）。多余的参数以空格拼接：

```csharp
ctx.Logger().Info("user %s logged in %d times", "alice", 3);
// → user alice logged in 3 times
```

## `LogMessage`

每条日志生成一个 `LogMessage`：

| 属性 | 说明 |
| --- | --- |
| `Sn` | 全局自增序号 |
| `Ts` | 时间戳（`DateTimeOffset`） |
| `Name` | 日志器名 |
| `Level` | 级别 |
| `Args` | 参数数组（首元素为格式串，`Error(Exception)` 时为首个栈帧信息） |
| `Fiber` | 产生日志的 fiber |

## exporter 与缓冲

```csharp
public interface ILogExporter
{
    void Export(LogMessage message);
}
```

- `LoggerService.Exporter(exporter)` —— 注册 exporter（返回可释放句柄，fiber 卸载自动移除；本身注册为 fiber 的 effect）；
- 默认注册一个 `BufferExporter`：追加到 `Buffer`（`List<LogMessage>`），超过 `BufferSize`（默认 1000）时截断；
- `Buffer` / `BufferSize` 可读写，方便诊断与测试。

```csharp
ctx.LoggerService.Exporter(new MyExporter());   // 自定义导出（如写文件、转发到 ILogger）
var recent = ctx.LoggerService.Buffer;          // 环形缓冲
```

## 实践建议

- 插件内用 `ctx.Logger()` 获得与插件同名的日志器，方便按名过滤；
- 自定义 exporter 可把 CordiSharp 日志桥接到 `Microsoft.Extensions.Logging` 或 Serilog；
- 框架内部错误（disposer 异常、异步监听异常、fiber 加载失败）会记录到日志，排查时先看 `Buffer`。

## 桥接到 Microsoft.Extensions.Logging

`CordiSharp.Extensions.Logging` 扩展包把日志系统接入标准的 .NET 日志管线（MEL），支持两个方向：

```bash
dotnet add package CordiSharp.Extensions.Logging
```

### MEL → CordiSharp：`AddCordiSharpLogging`

注册一个 `ILoggerProvider`（`CordiSharpLoggerProvider`），让 `ILogger<T>` 的日志写入根上下文的
`LoggerService`（MEL 的 category 就是 CordiSharp 日志器名）：

```csharp
using CordiSharp.Extensions.Logging;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddCordiSharpLogging();        // 隐含调用 AddCordiSharp()
// 纯 ServiceCollection 也可：
// services.AddLogging(b => b.AddCordiSharpLogging());
// services.AddCordiSharpLogging();
```

级别映射：`Trace/Debug → Debug`、`Information → Info`、`Warning → Warn`、`Error/Critical → Error`。

### CordiSharp → MEL：`UseLoggerFactory`

给根上下文挂一个 exporter（`CordiSharpLogExporter`），把 `ctx.Logger()` 的输出转发到 MEL 管线
（控制台、文件等）：

```csharp
using CordiSharp.Extensions.Logging;

var ctx = provider.GetRequiredService<Context>();
using var bridge = ctx.UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>());
// 或直接：ctx.LoggerService.Exporter(new CordiSharpLogExporter(factory));
```

- 消息文本 = `[日志器名] 格式化结果`（如 `[chat] n = 42`）；
- 级别映射：`Error → Error`、`Warn → Warning`、`Info → Information`、`Debug → Debug`；
- 返回的句柄可释放，dispose 后解除桥接。

两个方向可以同时使用：桥内部有重入保护（AsyncLocal），经 MEL 转发回来的消息不会被再次导出，
不会无限循环。


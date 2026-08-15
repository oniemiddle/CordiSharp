# CordiSharp 文档

[CordiSharp](https://github.com/cordiverse/cordis) 是 [Cordis](https://github.com/cordiverse/cordis) —— 时空可组合性元框架 —— 的 C# 移植版。它把 Cordis 基于上下文/作用域的插件系统、依赖注入服务、类型化事件与 fiber 生命周期管理带到 .NET，并原生集成 Microsoft.Extensions.DependencyInjection、Roslyn 源生成器与 Roslyn 分析器。

本目录是 CordiSharp 的**详细中文 API 参考文档**。快速概览与示例请见仓库根目录的 [README](../README.md) 与 [README_zh](../README_zh.md)。

## 包结构

| 包 | 说明 | 文档 |
| --- | --- | --- |
| `CordiSharp` | 运行时核心（`Context`、插件、服务、事件、effects、schema、日志），打包内附源生成器与分析器 | [Context 与作用域](02-context-scopes.md) 起全部章节 |
| `CordiSharp.Extensions.DependencyInjection` | `AddCordiSharp`、`CordiSharpOptions`、`ctx.Resolve<T>()` | [MSDI 集成](09-msdi-hosting.md) |
| `CordiSharp.Extensions.Hosting` | `CordiSharpHost`（`IHostedService`）、`AddCordiSharpHosting` | [MSDI 集成](09-msdi-hosting.md) |
| `CordiSharp.Extensions.Logging` | `AddCordiSharpLogging`、`CordiSharpLogExporter`、`UseLoggerFactory` | [日志系统](10-logging.md) |

## 文档地图

### 入门

- [快速上手](01-quickstart.md) —— 安装、最小示例、逐步讲解

### 核心框架（`CordiSharp` 包）

- [Context 与作用域](02-context-scopes.md) —— 核心对象、`Extend`/`Isolate`/`Intercept`、API 全表
- [插件与 Fiber 生命周期](03-plugins-fibers.md) —— 插件形态、`[Plugin]`、依赖注入、`PluginHandle`、状态机
- [服务系统](04-services.md) —— `Provide`/`Get`/`Set`、`Accessor`、`Mixin`、`Service` 基类、隔离语义
- [事件系统](05-events.md) —— `EventKey`、注册与分发模式、`EventOptions`、thisArg 过滤
- [Effect 生命周期](06-effects.md) —— setup 返回值形式、收集与逆序卸载、诊断元数据
- [配置 Schema](07-schema.md) —— `Schema` 工厂、校验、`FromType` 映射、`[PluginConfig]`/`[DefaultValue]`/`[Required]`
- [日志系统](10-logging.md) —— `LoggerService`、`Logger`、`ILogExporter`、格式化、MEL 桥接

### 编译期工具

- [源生成器与分析器](08-generator-analyzers.md) —— 生成元数据、`CORDIS001`–`CORDIS003`

### 生态集成

- [MSDI 与 Hosting 集成](09-msdi-hosting.md) —— `CordiSharp.Extensions.*` 扩展包

### 参考

- [常见问题 FAQ](11-faq.md)

## 快速安装

```bash
dotnet add package CordiSharp
dotnet add package CordiSharp.Extensions.DependencyInjection   # 可选
dotnet add package CordiSharp.Extensions.Hosting               # 可选
dotnet add package CordiSharp.Extensions.Logging               # 可选
```

## 环境要求

- .NET 10（`net10.0`）
- 扩展包中的 `ctx.Resolve<T>()` 使用 C# 14 扩展成员（`extension` 关键字）语法，需要 C# 14 编译器（.NET 10 SDK）

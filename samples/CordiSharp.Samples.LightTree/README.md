# CordiSharp.Samples.LightTree — 点灯树（Fiber 状态级联可视化）

一个 Avalonia 桌面应用，用真实的 CordiSharp（`src/CordiSharp`，cordis 的 C# 移植）
驱动一棵"点灯树"：每个节点是一个真实的插件 fiber，颜色跟随 `FiberState`，
依赖边驱动真实的级联（provide → notify → epoch → reload/unload）。

## 运行

```bash
dotnet run --project samples/CordiSharp.Samples.LightTree
```

## 诊断模式

```bash
# headless 级联自测：直接驱动 CordiSharp，打印每步 fiber 状态
dotnet run --project samples/CordiSharp.Samples.LightTree -- --selftest

# GUI 自动剧本：自动执行 停用/启动/故障/恢复/造环 并写入
# %TEMP%/cordisharp-lighttree-autodemo.log（用于排查"级联没传递"类问题）
dotnet run --project samples/CordiSharp.Samples.LightTree -- --autodemo
```

## 操作

| 操作 | 说明 |
| --- | --- |
| 左键拖拽空白处 | 平移画布 |
| 滚轮 | 缩放（以光标为中心，0.2x–4x） |
| 左键拖拽节点 | 移动节点（**左键只负责移动，不改变选中状态**） |
| **Ctrl+左键点击节点** | **连线/删线（取反）**：第一次点击选中【依赖方】（橙色高亮），再 Ctrl+点击【提供方】——无该边则创建（A→B 表示 A 依赖 B），已有该边则删除；Ctrl+点击已选中的节点取消选中 |
| Ctrl+左键点击空白 | 取消选中 |
| 右键节点 | 菜单：启动 / 停用 / 注入故障 / 恢复 / 删除 |
| 右键连线 | 删除连线（与 Ctrl 取反等效） |
| 「添加节点」 | 在视口中心新增一个未加载（灰）节点 |
| 「示例图」 | 重建 N1 → {N2, N3} → N4 并逐个启动，观察绿色级联 |

## 颜色 ↔ FiberState

| 颜色 | 状态 | 含义 |
| --- | --- | --- |
| 绿 | `Active` | 依赖满足、插件 body 已运行、提供的服务对外可见 |
| 黄 | `Pending` | 已注册但依赖未满足，停靠等待（派生状态，非错误） |
| 浅绿 | `Loading` | 插件 body 执行中（Reload 在途） |
| 浅黄 | `Unloading` | disposers 逆序执行中（Unload 在途） |
| 红 | `Failed` | 加载失败（含"注入故障"），服务不可见，等 `Update` 恢复 |
| 灰 | `Disposed` | 未加载 / 已停用 |

所有颜色变化通过 `BrushTransition` 以 0.3 s 过渡，方便观察级联。

## 级联是怎么"真实"的

- 每个节点 = `ctx.Inject(providers, callback)` 创建的**真实 fiber**；callback 里
  `ctx.Provide($"svc:{id}", ...)`。
- 严格解析（`ReflectService.GetImpl`：提供者必须 `Active`）保证：提供者 Active →
  `Notify` → 依赖方 `CheckImpl` → epoch 变化 → `Reload`；提供者停用/失败 →
  依赖方 `Unload` → `Pending`。
- 状态变化通过 `internal/status` 事件实时推送（与 JS cordis 宿主同构），
  `FiberHost` 把它转成节点颜色。

## 状态机不支持的状态图：停止并告知

真实 CordiSharp 对依赖环的行为是**静默死锁**（环上节点互相等待 Active，
epoch 永远 `INACTIVE`，无错误、无事件）。`GraphAnalyzer`（Tarjan SCC）在每次
级联收敛后检查"已加载且非 Active"的子图：

- 发现环（SCC ≥ 2 或自环）→ 停止（不再推进任何迁移）并在信息栏以红色告警，
  环上节点显示 `!` 徽标：*"fiber 状态机不支持该状态图，环上节点永远 Pending"*。
- 其余情况给出黄色提示（如 `N4 等待依赖 N3`）。

## 文件

- `FiberHost.cs` — 真实 CordiSharp 宿主：根 Context、fiber 创建、`internal/status` 订阅
- `GraphAnalyzer.cs` — 依赖环（死岛）检测
- `GraphViewModel.cs` — 用户操作 → 真实 fiber 操作（Start/Stop/Fail/Recover）→ 收敛 → 分析
- `GraphCanvas.cs` — 画布：平移/缩放/网格/有向边/命中测试
- `FiberNodeView.cs` — 节点灯：0.3 s 颜色过渡、拖拽、点击（连线模式）
- `NodeViewModel.cs` / `EdgeViewModel.cs` / `StateColors.cs` — 数据模型

using System.Collections.ObjectModel;
using Avalonia.Media;
using CordiSharp;

namespace CordiSharp.Samples.LightTree;

/// <summary>
/// Orchestrates the demo: owns the node/edge collections, maps every user action to a
/// real CordiSharp operation (start / stop / inject fault / recover / remove), waits for
/// the fiber cascade to settle, then validates the resulting state graph — if the fiber
/// state machine cannot support it (dependency cycle / dead island), it stops and reports.
/// </summary>
public sealed class GraphViewModel : ObservableObject, IDisposable
{
    public ObservableCollection<NodeViewModel> Nodes { get; } = new();
    public ObservableCollection<EdgeViewModel> Edges { get; } = new();

    private readonly FiberHost _host = new();
    private int _nextId = 1;

    /// <summary>Raised when the visual graph needs repainting (node/edge topology changes).</summary>
    public event Action? GraphChanged;

    // ---- connection selection (Ctrl+left click) ----

    public NodeViewModel? LinkSource
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field?.IsLinkSource = false;
            field = value;
            value?.IsLinkSource = true;
            OnPropertyChanged();
        }
    }

    /// <summary>Cancels the pending connection selection (Ctrl+click on empty canvas).</summary>
    public void CancelLink() => LinkSource = null;

    // ---- info bar ----

    public string Message
    {
        get;
        private set
        {
            if (Set(ref field, value)) OnPropertyChanged(nameof(HasMessage));
        }
    } = "";

    public bool HasMessage => Message.Length > 0;

    public IBrush MessageBrush
    {
        get;
        private set => Set(ref field, value);
    } = Brushes.Black;

    // ---- node & edge management ----

    public NodeViewModel AddNode(double x, double y)
    {
        var node = new NodeViewModel(_nextId++, Math.Max(0, x), Math.Max(0, y));
        Nodes.Add(node);
        GraphChanged?.Invoke();
        return node;
    }

    private IReadOnlyList<string> ProvidersOf(NodeViewModel node)
        => Edges.Where(e => e.From == node).Select(e => $"svc:{e.To.Id}").ToList();

    /// <summary>Creates a directed edge From(dependent) → To(provider). Reloads the dependent
    /// fiber if it is already loaded, so its inject set reflects the new dependency.</summary>
    public async Task AddEdgeAsync(NodeViewModel dep, NodeViewModel prov)
    {
        if (Edges.Any(e => e.From == dep && e.To == prov))
        {
            SetMessage($"连线 {dep.Name}→{prov.Name} 已存在。", Brushes.Black);
            return;
        }
        Edges.Add(new EdgeViewModel(dep, prov));
        GraphChanged?.Invoke();

        if (dep.Handle is not null)
        {
            var wasFailed = dep.State == FiberState.Failed;
            await StopCoreAsync(dep);
            dep.FailRequested = wasFailed;
            await StartCoreAsync(dep);
        }
        LinkSource = null;
        await SettleAsync();
    }

    public async Task RemoveEdgeAsync(EdgeViewModel edge)
    {
        Edges.Remove(edge);
        GraphChanged?.Invoke();

        if (edge.From.Handle is not null)
        {
            var wasFailed = edge.From.State == FiberState.Failed;
            await StopCoreAsync(edge.From);
            edge.From.FailRequested = wasFailed;
            await StartCoreAsync(edge.From);
        }
        await SettleAsync();
    }

    /// <summary>Ctrl+click connection semantics: toggles edge existence (bool inversion).
    /// Creates dep→prov when absent, removes it when already present.</summary>
    public async Task ToggleEdgeAsync(NodeViewModel dep, NodeViewModel prov)
    {
        var existing = Edges.FirstOrDefault(e => e.From == dep && e.To == prov);
        if (existing is not null)
        {
            await RemoveEdgeAsync(existing);
            ShowHint($"已删除连线 {dep.Name}→{prov.Name}。");
        }
        else
        {
            await AddEdgeAsync(dep, prov);
            ShowHint($"已创建连线 {dep.Name}→{prov.Name}（{dep.Name} 依赖 {prov.Name}）。再次 Ctrl+点击可删除。");
        }
        LinkSource = null;
    }

    // ---- user actions (mapped to real CordiSharp operations) ----

    /// <summary>启动：加载插件（或清错重启）。依赖未满足时机器保持 Pending，由分析器告知。</summary>
    public async Task StartAsync(NodeViewModel node)
    {
        node.FailRequested = false;
        await StartCoreAsync(node);
        await SettleAsync();
    }

    private async Task StartCoreAsync(NodeViewModel node)
    {
        // Failed fibers cannot be revived via Update() in the current CordiSharp port:
        // Unload() clears the whole dependency cache (Store = null), so Refresh() after
        // a failed restart can never recompute a valid epoch. Rebuilding the fiber is
        // the reliable path — the constructor re-runs CheckImpl for every inject.
        if (node.Handle is null || node.State == FiberState.Failed)
        {
            if (node.Handle is not null) await StopCoreAsync(node);
            node.ProviderNames = ProvidersOf(node);
            node.Handle = _host.Load(node);
        }
        else
        {
            // Update clears the recorded error and restarts (Active/Pending → reload).
            node.Handle.Update(null);
        }
    }

    /// <summary>停用：dispose fiber（逆序清空该 fiber 的全部 effect，依赖方级联卸载）。</summary>
    public async Task StopAsync(NodeViewModel node)
    {
        await StopCoreAsync(node);
        await SettleAsync();
    }

    private async Task StopCoreAsync(NodeViewModel node)
    {
        if (node.Handle is null) return;
        await node.Handle.DisposeAsync();
        _host.Unmap(node.Handle.Fiber);
        node.Handle = null;
        node.State = FiberState.Disposed;
    }

    /// <summary>注入故障：让插件 body 在重启时抛错 → fiber Failed（失败即清场，服务立刻不可见）。</summary>
    public async Task FailAsync(NodeViewModel node)
    {
        if (node.Handle is null)
        {
            SetMessage($"{node.Name} 未加载，无法注入故障。", Brushes.Black);
            return;
        }
        if (node.State == FiberState.Pending)
        {
            SetMessage($"{node.Name} 处于 Pending（依赖未满足，body 从未执行），无法注入故障——这正是 Pending 的语义。", Brushes.Black);
            return;
        }
        node.FailRequested = true;
        try
        {
            await node.Handle.Restart(); // body 抛错 → Failed；Await 会重抛，属预期
        }
        catch
        {
            // expected: the injected fault
        }
        await SettleAsync();
    }

    /// <summary>恢复：清除故障标志并重启（Failed → Active，若依赖满足）。</summary>
    public Task RecoverAsync(NodeViewModel node) => StartAsync(node);

    public async Task RemoveAsync(NodeViewModel node)
    {
        await StopCoreAsync(node);
        for (var i = Edges.Count - 1; i >= 0; i--)
        {
            if (Edges[i].From == node || Edges[i].To == node) Edges.RemoveAt(i);
        }
        if (LinkSource == node) LinkSource = null;
        Nodes.Remove(node);
        GraphChanged?.Invoke();
        await SettleAsync();
    }

    // ---- bulk actions & demo ----

    public async Task StartAllAsync()
    {
        foreach (var node in Nodes.ToList()) await StartCoreAsync(node);
        await SettleAsync();
    }

    public async Task StopAllAsync()
    {
        foreach (var node in Nodes.ToList()) await StopCoreAsync(node);
        await SettleAsync();
    }

    public async Task ClearAsync()
    {
        foreach (var node in Nodes.ToList()) await StopCoreAsync(node);
        Edges.Clear();
        Nodes.Clear();
        LinkSource = null;
        Message = "";
        GraphChanged?.Invoke();
    }

    /// <summary>Loads a small dependency chain (N1 → N2,N3 → N4) and starts it so the
    /// green cascade is immediately observable.</summary>
    public async Task LoadDemoAsync()
    {
        var n1 = AddNode(200, 380);
        var n2 = AddNode(540, 180);
        var n3 = AddNode(540, 620);
        var n4 = AddNode(880, 380);

        await AddEdgeAsync(n2, n1);
        await AddEdgeAsync(n3, n1);
        await AddEdgeAsync(n4, n2);

        foreach (var node in new[] { n1, n2, n3, n4 })
        {
            await StartAsync(node);
            await Task.Delay(150); // let the green cascade be observed node by node
        }

        SetMessage("示例图已加载：N1 → {N2, N3} → N4。试试：右键 N1 停用（级联变黄）/注入故障（变红）；连线模式造一个环看告警。",
            Brushes.Black);
    }

    // ---- cascade settle + state graph validation ----

    private async Task SettleAsync()
    {
        // let the fiber machine's async continuations (Task.Yield / dispatcher) flush
        await Task.Delay(50);
        foreach (var node in Nodes)
        {
            if (node.Handle is null) continue;
            try { await node.Handle.Await(); }
            catch { /* Failed fibers throw here; that's the expected outcome */ }
        }
        await Task.Delay(50);

        // final sync: in case any status event was coalesced
        foreach (var node in Nodes)
        {
            if (node.Handle is not null) node.State = node.Handle.State;
        }
        Analyze();
    }

    private void Analyze()
    {
        foreach (var node in Nodes) node.IsWarning = false;

        var islands = GraphAnalyzer.FindDeadIslands(Nodes, Edges);
        if (islands.Count > 0)
        {
            var path = string.Join(" → ", islands[0].Select(n => n.Name));
            foreach (var island in islands)
                foreach (var node in island) node.IsWarning = true;
            SetMessage(
                $"⚠ 检测到依赖环 {path}：fiber 状态机不支持该状态图（环上节点互相等待 Active，永远保持 Pending，且无任何错误）。" +
                "级联已停止。请先停用环上节点或断开连线。",
                Brushes.DarkRed);
            return;
        }

        var pending = Nodes.Where(n => n.Handle is not null && n.State == FiberState.Pending).ToList();
        if (pending.Count > 0)
        {
            var parts = pending.Select(n =>
            {
                var missing = Edges
                    .Where(e => e.From == n && e.To.Handle?.State != FiberState.Active)
                    .Select(e => e.To.Name)
                    .ToList();
                return missing.Count > 0 ? $"{n.Name} 等待依赖 {string.Join(", ", missing)}" : $"{n.Name} 等待加载";
            });
            SetMessage(string.Join("；", parts) + "。", Brushes.Black);
            return;
        }

        Message = "";
    }

    private void SetMessage(string text, IBrush brush)
    {
        Message = text;
        MessageBrush = brush;
    }

    /// <summary>Transient hint (e.g. link-mode instructions) without running analysis.</summary>
    public void ShowHint(string text)
    {
        Message = text;
        MessageBrush = Brushes.Black;
    }

    public void Dispose() => _host.Dispose();
}

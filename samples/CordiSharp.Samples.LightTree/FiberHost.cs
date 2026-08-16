using Avalonia.Threading;
using CordiSharp;
using CordiSharp.Events;
using CordiSharp.Registry;

namespace CordiSharp.Samples.LightTree;

/// <summary>Marker object provided as a node's service value.</summary>
internal sealed record ServiceMarker(int NodeId);

/// <summary>
/// Wraps a real CordiSharp root <see cref="Context"/> and drives the cascade:
/// each visual node is backed by a real plugin fiber created via
/// <c>ctx.Inject(providers, callback)</c>; the fiber provides its own service so
/// that dependents resolve only while it is Active. State changes are observed
/// through the <c>internal/status</c> event, exactly like the JS cordis host does.
/// </summary>
public sealed class FiberHost : IDisposable
{
    private readonly Context _root = Context.Create();
    private readonly Dictionary<Fiber, NodeViewModel> _fiberMap = new();
    private readonly List<IDisposable> _subscriptions = new();

    public FiberHost()
    {
        // Payload of internal/status is [fiber, oldState]; typed listeners receive args[0] = fiber.
        _subscriptions.Add(_root.On(EventKey.Create<Fiber>(InternalEvents.Status), (_, fiber) =>
        {
            if (!_fiberMap.TryGetValue(fiber, out var node)) return null;
            var state = node.Handle?.State ?? FiberState.Disposed;
            Dispatcher.UIThread.Post(() => node.State = state);
            return null;
        }));
    }

    /// <summary>Creates a real plugin fiber for the node. The callback throws when
    /// <see cref="NodeViewModel.FailRequested"/> is set (demonstrates Failed), and
    /// provides "svc:{id}" so dependents see it only while this fiber is Active.</summary>
    public PluginHandle Load(NodeViewModel node)
    {
        var handle = _root.Inject(node.ProviderNames, (ctx, _) =>
        {
            if (node.FailRequested)
            {
                throw new InvalidOperationException($"plugin {node.Name} 注入故障（用户触发）");
            }
            ctx.Provide($"svc:{node.Id}", new ServiceMarker(node.Id));
            return null;
        });
        _fiberMap[handle.Fiber] = node;
        // capture the initial state synchronously: the Loading event fired inside
        // Inject (before the mapping existed) would otherwise be missed
        node.State = handle.State;
        return handle;
    }

    public void Unmap(Fiber fiber) => _fiberMap.Remove(fiber);

    public void Dispose()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _fiberMap.Clear();
    }
}

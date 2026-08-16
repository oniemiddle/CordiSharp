using CordiSharp;

namespace CordiSharp.Samples.LightTree;

/// <summary>
/// Static analysis of the *live* dependency graph. The CordiSharp fiber machine
/// (strict resolution: providers must be Active) cannot converge when a set of
/// loaded fibers form a dependency cycle: none of them can ever enter Active, so
/// none provides anything, and no notification wave ever reaches them — a "dead
/// island" that stays Pending forever with no error. This is the state graph the
/// fiber state machine does NOT support; we detect it and report it.
/// </summary>
public static class GraphAnalyzer
{
    /// <summary>Returns the dead islands (cycles) among loaded fibers, if any.</summary>
    public static List<List<NodeViewModel>> FindDeadIslands(
        IEnumerable<NodeViewModel> nodes,
        IEnumerable<EdgeViewModel> edges)
    {
        var loaded = nodes.Where(n => n.Handle is not null).ToHashSet();
        var adj = new Dictionary<NodeViewModel, List<NodeViewModel>>();
        foreach (var node in loaded) adj[node] = [];

        foreach (var edge in edges)
        {
            if (loaded.Contains(edge.From) && loaded.Contains(edge.To))
            {
                adj[edge.From].Add(edge.To);
            }
        }

        var islands = new List<List<NodeViewModel>>();
        foreach (var scc in Tarjan(adj))
        {
            var selfLoop = scc.Count == 1 && adj[scc[0]].Contains(scc[0]);
            if (scc.Count < 2 && !selfLoop) continue;          // not a cycle
            if (scc.Any(n => n.State == FiberState.Active)) continue; // cycle already broken
            islands.Add(scc);
        }
        return islands;
    }

    // Tarjan strongly-connected components (recursive; graphs here are small).
    private static List<List<NodeViewModel>> Tarjan(Dictionary<NodeViewModel, List<NodeViewModel>> adj)
    {
        var index = 0;
        var indices = new Dictionary<NodeViewModel, int>();
        var lowlink = new Dictionary<NodeViewModel, int>();
        var onStack = new HashSet<NodeViewModel>();
        var stack = new Stack<NodeViewModel>();
        var result = new List<List<NodeViewModel>>();

        void StrongConnect(NodeViewModel v)
        {
            indices[v] = lowlink[v] = index++;
            stack.Push(v);
            onStack.Add(v);

            foreach (var w in adj[v])
            {
                if (!indices.ContainsKey(w))
                {
                    StrongConnect(w);
                    lowlink[v] = Math.Min(lowlink[v], lowlink[w]);
                }
                else if (onStack.Contains(w))
                {
                    lowlink[v] = Math.Min(lowlink[v], indices[w]);
                }
            }

            if (lowlink[v] == indices[v])
            {
                var scc = new List<NodeViewModel>();
                NodeViewModel w;
                do
                {
                    w = stack.Pop();
                    onStack.Remove(w);
                    scc.Add(w);
                } while (!ReferenceEquals(w, v));
                result.Add(scc);
            }
        }

        foreach (var v in adj.Keys)
        {
            if (!indices.ContainsKey(v)) StrongConnect(v);
        }
        return result;
    }
}

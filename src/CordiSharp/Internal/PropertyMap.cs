namespace CordiSharp.Internal;

/// <summary>A prototype-chained name-&gt;value map (models JS prototype inheritance of
/// <c>ctx[symbols.isolate]</c> / <c>ctx[symbols.intercept]</c>).</summary>
public sealed class PropertyMap<TValue>(PropertyMap<TValue>? parent = null)
{
    public PropertyMap<TValue>? Parent { get; } = parent;
    private readonly Dictionary<string, TValue> _own = new();

    public bool HasOwn(string name) => _own.ContainsKey(name);

    public bool TryGet(string name, out TValue? value)
    {
        if (_own.TryGetValue(name, out value!)) return true;
        if (Parent is not null && Parent.TryGet(name, out value)) return true;
        value = default;
        return false;
    }

    /// <summary>Reads the token visible at this scope, creating one on the ROOT map
    /// if none exists anywhere (mirrors <c>ctx.root[isolate][name] ??= Symbol(name)</c>).</summary>
    public TValue GetOrCreateRoot(string name, Func<TValue> factory)
    {
        if (_own.TryGetValue(name, out var value) || Parent is not null && Parent.TryGet(name, out value!)) return value;
        // walk to the very top (root map) and create there
        var root = this;
        while (root.Parent is not null) root = root.Parent;
        var created = factory();
        root._own[name] = created;
        return created;
    }

    public void Set(string name, TValue value) => _own[name] = value;

    /// <summary>Enumerates own entries (for intercept merging).</summary>
    public IEnumerable<KeyValuePair<string, TValue>> EnumerateOwn() => _own;
}
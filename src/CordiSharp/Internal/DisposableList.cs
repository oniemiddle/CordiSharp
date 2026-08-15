namespace CordiSharp.Internal;

/// <summary>A tracked list of disposables; <see cref="DrainReverse"/> empties it in
/// reverse insertion order (mirrors cordis DisposableList.clear()).</summary>
internal sealed class DisposableList<T> where T : class
{
    private readonly List<T> _items = [];

    public int Count => _items.Count;

    public void Add(T value) => _items.Add(value);

    public bool Remove(T value) => _items.Remove(value);

    public T[] DrainReverse()
    {
        var array = _items.ToArray();
        _items.Clear();
        Array.Reverse(array);
        return array;
    }

    public void Clear() => _items.Clear();

    public T[] Snapshot() => _items.ToArray();

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
}
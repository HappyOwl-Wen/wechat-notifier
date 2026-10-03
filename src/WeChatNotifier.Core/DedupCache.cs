namespace WeChatNotifier.Core;

/// <summary>
/// Fixed-size LRU set of recent message fingerprints. Bounded by design: chat history is never accumulated.
/// Not thread-safe; owned by the listener thread.
/// </summary>
public sealed class DedupCache
{
    private readonly int _capacity;
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.Ordinal);

    public DedupCache(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Count => _nodes.Count;

    /// <summary>Returns true when <paramref name="fingerprint"/> was not seen recently (and remembers it).</summary>
    public bool TryAdd(string fingerprint)
    {
        if (_nodes.TryGetValue(fingerprint, out var existing))
        {
            _order.Remove(existing);
            _order.AddFirst(existing);
            return false;
        }

        _nodes[fingerprint] = _order.AddFirst(fingerprint);
        if (_nodes.Count > _capacity)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _nodes.Remove(oldest.Value);
        }
        return true;
    }

    public void Clear()
    {
        _order.Clear();
        _nodes.Clear();
    }
}

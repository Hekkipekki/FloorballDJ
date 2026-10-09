namespace FloorballDJ.Infrastructure;

/// <summary>Small in-memory LRU for immutable UI resources.</summary>
internal sealed class BoundedCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _items = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _lru = new();
    internal int Count { get { lock (_gate) return _items.Count; } }

    internal TValue GetOrAdd(TKey key, Func<TKey, TValue> create)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var cached))
            {
                _lru.Remove(cached);
                _lru.AddFirst(cached);
                return cached.Value.Value;
            }
            var value = create(key);
            if (capacity <= 0) return value;
            var added = _lru.AddFirst((key, value));
            _items.Add(key, added);
            while (_items.Count > capacity)
            {
                var last = _lru.Last!;
                _items.Remove(last.Value.Key);
                _lru.RemoveLast();
            }
            return value;
        }
    }
}

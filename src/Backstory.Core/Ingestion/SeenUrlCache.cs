namespace Backstory.Core.Ingestion;

/// <summary>
/// Remembers which URLs we already handled, so each poll only processes new feed items.
/// Bounded (oldest entries are forgotten first) so memory can't grow forever.
///
/// In-memory on purpose for now: after a restart items are re-published, which is harmless because
/// the Indexer overwrites by DocumentId (idempotent). A persistent cursor can replace this later.
/// </summary>
public sealed class SeenUrlCache(int capacity = 50_000)
{
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Lock _lock = new();

    public bool Contains(string key)
    {
        lock (_lock) return _set.Contains(key);
    }

    public void Add(string key)
    {
        lock (_lock)
        {
            if (!_set.Add(key)) return;
            _order.Enqueue(key);
            while (_order.Count > capacity)
                _set.Remove(_order.Dequeue());
        }
    }

    public int Count
    {
        get { lock (_lock) return _set.Count; }
    }
}

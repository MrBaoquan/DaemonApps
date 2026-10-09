namespace AndX.Edge;

/// <summary>单次被代理请求的日志条目。</summary>
public sealed record EdgeLogEntry(
    DateTimeOffset Timestamp,
    string Method,
    string Path,
    int Status,
    long ElapsedMs,
    string? RequestId);

/// <summary>线程安全的定长请求日志环形缓冲（诊断页展示用）。</summary>
public sealed class EdgeRequestLog
{
    private readonly int _capacity;
    private readonly Queue<EdgeLogEntry> _items = new();
    private readonly object _gate = new();

    public EdgeRequestLog(int capacity = 500)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>新增条目时触发（可能在任意线程回调，UI 层需自行切线程）。</summary>
    public event Action<EdgeLogEntry>? EntryAdded;

    /// <summary>追加一条记录，超出容量时丢弃最旧。</summary>
    public void Add(EdgeLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _items.Enqueue(entry);
            while (_items.Count > _capacity)
            {
                _items.Dequeue();
            }
        }
        EntryAdded?.Invoke(entry);
    }

    /// <summary>获取当前快照（按时间升序）。</summary>
    public IReadOnlyList<EdgeLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }

    /// <summary>清空日志。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }

    /// <summary>当前条目数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }
}

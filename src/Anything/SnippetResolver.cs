using Anything.Core.Content;

namespace Anything;

/// <summary>
/// The Matching text of the rows on screen, from the text index only, off the UI thread (a cold index on a sleeping disk
/// must not freeze the window): the newest request first, since the rows painted last are the ones on screen. Results
/// are kept by (path, query terms), so they don't depend on index ids, survive a compaction and work for every tab.
/// Each request belongs to the window that asked, so one window's new search drops only its own rows' lookups.
/// </summary>
internal sealed class SnippetResolver : IDisposable
{
    /// <summary>What the index says about one file for one query.</summary>
    /// <param name="Reason">Why the file's words aren't searchable, when there is no snippet and a reason (null: nothing matched inside).</param>
    public sealed record Result(ContentSnippet? Snippet, DocStatus? Reason);

    /// <summary>A result and the generation it was looked up in (older than the current one: shown, and looked up again).</summary>
    readonly record struct Entry(Result Result, int Generation);

    /// <summary>One lookup asked for: a file, the query's terms, and the window whose row asked.</summary>
    readonly record struct Request(string Path, ContentHighlighter Highlighter, object Owner);

    const int MaxCached = 2000, MaxQueued = 512;
    /// <summary>While results keep coming, windows are told at most this often (and once the queue is empty).</summary>
    const int ResolvedEveryMs = 100;

    readonly IContentIndex _content;
    readonly object _gate = new();
    readonly Dictionary<(string Path, string Key), Entry> _cache = new(KeyComparer.Instance);
    readonly LinkedList<Request> _queue = new(); // newest first
    readonly HashSet<Request> _queued = new(RequestComparer.Instance);
    readonly SemaphoreSlim _work = new(0);
    readonly CancellationTokenSource _stop = new();
    int _generation;
    Thread? _worker;

    public SnippetResolver(IContentIndex content) => _content = content;

    /// <summary>
    /// Raised on the reader thread when snippets came in: once the queue is empty, and every 100 ms or so while it isn't,
    /// so the rows on screen (looked up first) show without waiting for the rows scrolled past. Windows repaint.
    /// </summary>
    public event Action? Resolved;

    /// <summary>
    /// The snippet of a file for a query, if looked up already; else queues the lookup (for <paramref name="owner"/>, the
    /// window asking) and returns false. <paramref name="stale"/>: the result is from before new words came in; it is
    /// shown meanwhile (no blank flicker), and looked up again.
    /// </summary>
    public bool TryGet(string path, ContentHighlighter highlighter, object owner, out Result result, out bool stale)
    {
        var key = (path, highlighter.Key);
        bool found;
        lock (_gate)
        {
            found = _cache.TryGetValue(key, out var entry);
            result = entry.Result;
            stale = found && entry.Generation != _generation;
            if (found && !stale) return true;
            if (_stop.IsCancellationRequested || !_queued.Add(new Request(path, highlighter, owner))) return found;
            _queue.AddFirst(new Request(path, highlighter, owner));
            while (_queue.Count > MaxQueued)
            {
                _queued.Remove(_queue.Last!.Value);
                _queue.RemoveLast();
            }
            EnsureWorker();
        }
        _work.Release();
        return found;
    }

    /// <summary>
    /// A window's new search: the rows it asked for before are no longer on screen, so their lookups are dropped (not what
    /// was found). Other windows' lookups stay.
    /// </summary>
    public void ClearQueue(object owner)
    {
        lock (_gate)
        {
            for (var node = _queue.First; node is not null;)
            {
                var next = node.Next;
                if (ReferenceEquals(node.Value.Owner, owner))
                {
                    _queued.Remove(node.Value);
                    _queue.Remove(node);
                }
                node = next;
            }
        }
    }

    /// <summary>New words are in the index: what was looked up may have changed. It stays on screen until looked up again.</summary>
    public void MarkStale()
    {
        lock (_gate) _generation++;
    }

    /// <summary>What to search inside changed (Settings): nothing looked up before is shown again.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _cache.Clear();
        }
    }

    void EnsureWorker()
    {
        if (_worker != null) return;
        _worker = new Thread(Work) { IsBackground = true, Name = "Ultimate File Explorer matching text", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    void Work()
    {
        var ct = _stop.Token;
        long lastRaised = 0;
        bool unsaid = false; // results came in that no window was told about yet
        try
        {
            while (true)
            {
                _work.Wait(ct);
                Request next;
                int generation;
                bool known;
                lock (_gate)
                {
                    if (_queue.First is not { } first)
                    {
                        // Dropped by a new search meanwhile; what came in before still has to be told.
                        if (unsaid && _queue.Count == 0) { unsaid = false; lastRaised = Environment.TickCount64; RaiseResolved(); }
                        continue;
                    }
                    next = first.Value;
                    _queue.RemoveFirst();
                    _queued.Remove(next);
                    generation = _generation;
                    // Another window asked for the same line and it came in meanwhile: nothing to look up.
                    known = _cache.TryGetValue((next.Path, next.Highlighter.Key), out var have) && have.Generation == generation;
                }
                if (!known)
                {
                    Result result;
                    try
                    {
                        var snippet = _content.GetSnippet(next.Path, next.Highlighter);
                        result = new Result(snippet, snippet is null ? _content.GetStatus(next.Path) : null);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        result = new Result(null, null); // the index changed underneath (or is stopping): shown as nothing matched
                    }
                    lock (_gate)
                    {
                        if (_cache.Count >= MaxCached) Trim();
                        _cache[(next.Path, next.Highlighter.Key)] = new Entry(result, generation);
                    }
                    unsaid = true;
                }
                bool empty;
                lock (_gate) empty = _queue.Count == 0;
                long now = Environment.TickCount64;
                if (unsaid && (empty || now - lastRaised >= ResolvedEveryMs))
                {
                    unsaid = false;
                    lastRaised = now;
                    RaiseResolved();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    void RaiseResolved()
    {
        try { Resolved?.Invoke(); }
        catch (Exception e) when (e is not OutOfMemoryException) { } // a window closing meanwhile
    }

    /// <summary>Makes room: results from before new words came in go first, then everything (under the lock).</summary>
    void Trim()
    {
        foreach (var key in _cache.Where(e => e.Value.Generation != _generation).Select(e => e.Key).ToList()) _cache.Remove(key);
        if (_cache.Count >= MaxCached) _cache.Clear();
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_gate)
        {
            _queue.Clear();
            _queued.Clear();
        }
    }

    /// <summary>Paths compare like Windows' (case ignored); query keys exactly.</summary>
    sealed class KeyComparer : IEqualityComparer<(string Path, string Key)>
    {
        public static readonly KeyComparer Instance = new();
        public bool Equals((string Path, string Key) a, (string Path, string Key) b)
            => string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Key, b.Key, StringComparison.Ordinal);
        public int GetHashCode((string Path, string Key) k)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Path), StringComparer.Ordinal.GetHashCode(k.Key));
    }

    /// <summary>A request is the same file and query terms for the same window.</summary>
    sealed class RequestComparer : IEqualityComparer<Request>
    {
        public static readonly RequestComparer Instance = new();
        public bool Equals(Request a, Request b)
            => ReferenceEquals(a.Owner, b.Owner) && KeyComparer.Instance.Equals((a.Path, a.Highlighter.Key), (b.Path, b.Highlighter.Key));
        public int GetHashCode(Request r)
            => HashCode.Combine(KeyComparer.Instance.GetHashCode((r.Path, r.Highlighter.Key)), System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r.Owner));
    }
}

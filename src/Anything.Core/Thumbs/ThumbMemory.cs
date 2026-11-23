namespace Anything.Core.Thumbs;

/// <summary>
/// A cache of values with a size, the least recently used dropped first once the sizes add up to more than
/// <see cref="Budget"/> (the grid's pictures at display size, step 1 of the ladder). The value set last is never the
/// one dropped for it, so a single value larger than the budget still stays until the next. <paramref name="dropped"/>
/// is called for every value that leaves (dropped, replaced, removed or cleared): the grid disposes its bitmap there.
/// Not thread-safe: one owner, one thread (the grid's UI thread).
/// </summary>
public sealed class ThumbMemory<TKey, TValue>(long budget, Action<TValue>? dropped = null) where TKey : notnull
{
    readonly record struct Entry(TKey Key, TValue Value, long Bytes);

    readonly Dictionary<TKey, LinkedListNode<Entry>> _map = [];
    readonly LinkedList<Entry> _order = new(); // first: least recently used
    long _budget = Math.Max(0, budget);

    /// <summary>The sizes may add up to this much before the least recently used are dropped.</summary>
    public long Budget
    {
        get => _budget;
        set
        {
            _budget = Math.Max(0, value);
            Trim(keep: null);
        }
    }

    /// <summary>
    /// Raised with the key of each value dropped to make room (not for one replaced, removed or cleared), after it left:
    /// a grid whose cell showed it asks for it again.
    /// </summary>
    public event Action<TKey>? Evicted;

    /// <summary>What the values held add up to.</summary>
    public long Bytes { get; private set; }

    /// <summary>How many values are held.</summary>
    public int Count => _map.Count;

    /// <summary>The value for <paramref name="key"/>, which becomes the most recently used.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddLast(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>The value for <paramref name="key"/>, leaving the order as it is.</summary>
    public bool Peek(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>Whether a value is held for <paramref name="key"/> (the order is left as it is).</summary>
    public bool Contains(TKey key) => _map.ContainsKey(key);

    /// <summary>
    /// Holds <paramref name="value"/> for <paramref name="key"/> as the most recently used (a value held before for it
    /// leaves), then drops the least recently used others until the sizes fit the budget.
    /// </summary>
    public void Set(TKey key, TValue value, long bytes)
    {
        bytes = Math.Max(0, bytes);
        if (_map.Remove(key, out var old))
        {
            _order.Remove(old);
            Bytes -= old.Value.Bytes;
            if (!ReferenceEquals(old.Value.Value, value)) dropped?.Invoke(old.Value.Value);
        }
        var node = _order.AddLast(new Entry(key, value, bytes));
        _map[key] = node;
        Bytes += bytes;
        Trim(keep: node);
    }

    /// <summary>Lets the value for <paramref name="key"/> go (it is passed to the dropped callback).</summary>
    public bool Remove(TKey key)
    {
        if (!_map.Remove(key, out var node)) return false;
        _order.Remove(node);
        Bytes -= node.Value.Bytes;
        dropped?.Invoke(node.Value.Value);
        return true;
    }

    /// <summary>Lets every value go, least recently used first.</summary>
    public void Clear()
    {
        var all = _order.ToList();
        _order.Clear();
        _map.Clear();
        Bytes = 0;
        if (dropped != null) foreach (var e in all) dropped(e.Value);
    }

    /// <summary>The keys held, least recently used first (tests, diagnostics).</summary>
    public IEnumerable<TKey> Keys => _order.Select(e => e.Key);

    void Trim(LinkedListNode<Entry>? keep)
    {
        while (Bytes > _budget && _order.First is { } first && first != keep)
        {
            _order.RemoveFirst();
            _map.Remove(first.Value.Key);
            Bytes -= first.Value.Bytes;
            dropped?.Invoke(first.Value.Value);
            Evicted?.Invoke(first.Value.Key);
        }
    }
}

/// <summary>Which picture a grid cell shows and how it is drawn (<see cref="ThumbPaint.Choose"/>).</summary>
public enum ThumbDrawing : byte
{
    /// <summary>Nothing to show yet: the empty frame.</summary>
    Nothing,
    /// <summary>The picture made at this size, drawn as it is (centred, never scaled).</summary>
    Exact,
    /// <summary>A stand-in at this size, scaled to fill the cell until the real one comes.</summary>
    StandIn,
    /// <summary>The picture made for another size (the thumbnail size changed, or the file changed since), scaled by the sizes' ratio.</summary>
    Other,
}

/// <summary>The grid's choice of what to draw in a cell. Pure.</summary>
public static class ThumbPaint
{
    /// <summary>
    /// What a cell shows: the picture or icon made at its size (<paramref name="exact"/>) when it is the real one; while
    /// that is only a stand-in, the real picture of another size (<paramref name="other"/>) if there is one, else the
    /// stand-in; with nothing at this size, whatever there is of another size; else nothing (the empty frame).
    /// </summary>
    public static ThumbDrawing Choose(ThumbQuality? exact, ThumbQuality? other)
    {
        if (exact is ThumbQuality.Final or ThumbQuality.None) return ThumbDrawing.Exact;
        if (exact is ThumbQuality.Low) return other is ThumbQuality.Final ? ThumbDrawing.Other : ThumbDrawing.StandIn;
        return other is null ? ThumbDrawing.Nothing : ThumbDrawing.Other;
    }

    /// <summary>
    /// The scale a picture is drawn at: <paramref name="drawing"/> Exact 1 (only ever shrunk to fit), StandIn whatever fills
    /// the <paramref name="box"/> with its long side, Other the ratio of the cell's size to the size it was made for.
    /// </summary>
    public static float Scale(ThumbDrawing drawing, int width, int height, int box, int madeFor)
    {
        if (width <= 0 || height <= 0 || box <= 0) return 1f;
        float fit = Math.Min((float)box / width, (float)box / height);
        return drawing switch
        {
            ThumbDrawing.StandIn => fit,
            ThumbDrawing.Other => Math.Min(fit, madeFor > 0 ? (float)box / madeFor : 1f),
            _ => Math.Min(1f, fit),
        };
    }
}

namespace Anything.Core.Search;

/// <summary>
/// A persistent, presorted order of every live entry for one sort key, the way Everything keeps sorted indexes.
/// Searches over a large index filter this order instead of sorting their results, so the default "everything"
/// view costs a copy instead of a multi-hundred-megabyte sort, and results arrive already ordered.
/// Kept current from <see cref="FileIndex"/>'s change log: small change sets are merged in by binary-search
/// insertion; large ones (scans, compaction, log overflow) trigger a rebuild. Not thread-safe: the engine
/// serializes access and calls it while holding the index read lock.
/// </summary>
internal sealed class SortOrderCache(SortKey key)
{
    public SortKey Key { get; } = key;

    long _generation = -1;
    long _sequence;
    int[] _order = [];
    int[] _rank = [];      // rank[id] = position in _order, -1 when absent
    readonly List<int> _touched = [];

    public int Count => _order.Length;

    /// <summary>Brings the order up to date with the index.</summary>
    public void Refresh(FileIndex index, SearchContext ctx, CancellationToken ct)
    {
        _touched.Clear();
        if (_generation == index.Generation && index.TryGetChangesSince(_sequence, _touched))
        {
            if (_touched.Count == 0) return;
            if (_touched.Count <= Math.Max(2000, _order.Length / 20) && CanMergeIncrementally(index))
            {
                Merge(index, ctx);
                _sequence = index.ChangeSequence;
                return;
            }
        }
        Rebuild(index, ctx, ct);
    }

    // Renaming or moving a folder changes the path of everything below it, which a path order cannot patch locally.
    bool CanMergeIncrementally(FileIndex index)
    {
        if (Key != SortKey.Path) return true;
        foreach (int id in _touched)
            if (index.IsAlive(id) && index.IsDirectory(id) && (uint)id < (uint)_rank.Length && _rank[id] >= 0) return false;
        return true;
    }

    void Rebuild(FileIndex index, SearchContext ctx, CancellationToken ct)
    {
        int n = index.Count;
        var ids = new int[index.LiveCount];
        int k = 0;
        for (int i = 0; i < n && k < ids.Length; i++) if (index.IsAlive(i)) ids[k++] = i;
        if (k != ids.Length) Array.Resize(ref ids, k);
        ResultSorter.Sort(index, ctx, ids, Key, descending: false, ct);
        _order = ids;
        BuildRank(n);
        _generation = index.Generation;
        _sequence = index.ChangeSequence;
    }

    void Merge(FileIndex index, SearchContext ctx)
    {
        var cmp = new EntryComparer(index, ctx, Key);

        // Positions of touched entries in the current order (their keys may have changed), and the live ones to re-insert.
        var positions = new List<int>(_touched.Count);
        var add = new List<int>(_touched.Count);
        var seen = new HashSet<int>(_touched.Count);
        foreach (int id in _touched)
        {
            if (!seen.Add(id)) continue;
            if ((uint)id < (uint)_rank.Length && _rank[id] >= 0) positions.Add(_rank[id]);
            if (index.IsAlive(id)) add.Add(id);
        }
        positions.Sort();

        // Existing order without the touched entries.
        var kept = new int[_order.Length - positions.Count];
        int src = 0, dst = 0;
        foreach (int pos in positions)
        {
            int len = pos - src;
            Array.Copy(_order, src, kept, dst, len);
            dst += len;
            src = pos + 1;
        }
        Array.Copy(_order, src, kept, dst, _order.Length - src);

        // Insert the touched live entries at their sorted positions.
        var adds = add.ToArray();
        Array.Sort(adds, cmp);
        var merged = new int[kept.Length + adds.Length];
        int from = 0, outPos = 0;
        foreach (int a in adds)
        {
            int lo = from, hi = kept.Length; // first position whose entry sorts after a
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (cmp.Compare(kept[mid], a) <= 0) lo = mid + 1; else hi = mid;
            }
            Array.Copy(kept, from, merged, outPos, lo - from);
            outPos += lo - from;
            merged[outPos++] = a;
            from = lo;
        }
        Array.Copy(kept, from, merged, outPos, kept.Length - from);

        _order = merged;
        BuildRank(index.Count);
    }

    void BuildRank(int indexCount)
    {
        if (_rank.Length < indexCount) _rank = new int[indexCount + indexCount / 32];
        Array.Fill(_rank, -1);
        for (int i = 0; i < _order.Length; i++) _rank[_order[i]] = i;
    }

    /// <summary>Every live entry in order (a fresh array the caller owns).</summary>
    public int[] All(bool descending)
    {
        var result = (int[])_order.Clone();
        if (descending) Array.Reverse(result);
        return result;
    }

    /// <summary>Orders a subset of live ids (as produced by a scan) by this key.</summary>
    public int[] Order(int[] ids, bool descending)
    {
        int k = ids.Length;
        if (k <= 1) return ids;
        if (k >= _order.Length / 16)
        {
            // Dense: walk the presorted order and keep the members of the subset.
            var member = new ulong[(_rank.Length + 63) >> 6];
            foreach (int id in ids) if ((uint)id < (uint)_rank.Length) member[id >> 6] |= 1UL << (id & 63);
            var result = new int[k];
            int j = 0;
            if (!descending)
            {
                for (int i = 0; i < _order.Length && j < k; i++)
                {
                    int id = _order[i];
                    if ((member[id >> 6] & (1UL << (id & 63))) != 0) result[j++] = id;
                }
            }
            else
            {
                for (int i = _order.Length - 1; i >= 0 && j < k; i--)
                {
                    int id = _order[i];
                    if ((member[id >> 6] & (1UL << (id & 63))) != 0) result[j++] = id;
                }
            }
            if (j == k) return result;
            // Fall through if some ids were not in the order (should not happen).
        }
        var keys = new int[k];
        for (int i = 0; i < k; i++)
        {
            int id = ids[i];
            keys[i] = (uint)id < (uint)_rank.Length && _rank[id] >= 0 ? _rank[id] : int.MaxValue;
        }
        Array.Sort(keys, ids);
        if (descending) Array.Reverse(ids);
        return ids;
    }

    /// <summary>
    /// Direct comparison equivalent to <see cref="ResultSorter"/>'s ascending order (same keys, same tie-breaks),
    /// used to place changed entries without rebuilding the precomputed sort keys.
    /// </summary>
    readonly struct EntryComparer(FileIndex index, SearchContext ctx, SortKey key) : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            if (a == b) return 0;
            int c = key switch
            {
                SortKey.Name => Group(a).CompareTo(Group(b)),
                SortKey.Path => Group(a) != Group(b) ? Group(a).CompareTo(Group(b)) : ComparePaths(index.Parent(a), index.Parent(b)),
                SortKey.Size => index.Size(a).CompareTo(index.Size(b)),
                SortKey.Extension => CompareExt(a, b),
                SortKey.Type => Group(a) != Group(b) ? Group(a).CompareTo(Group(b)) : CompareExt(a, b),
                SortKey.DateModified => index.Modified(a).CompareTo(index.Modified(b)),
                SortKey.DateCreated => index.Created(a).CompareTo(index.Created(b)),
                SortKey.Attributes => ((long)index.Attributes(a)).CompareTo((long)index.Attributes(b)),
                _ => 0,
            };
            if (c == 0)
            {
                string na = index.Name(a), nb = index.Name(b);
                c = string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
                if (c == 0) c = string.CompareOrdinal(na, nb);
            }
            return c != 0 ? c : a.CompareTo(b);
        }

        int Group(int id) => index.IsDirectory(id) ? 0 : 1;

        // Same as ResultSorter's parent ranks: roots first, then folder paths (ignore-case, ordinal, id).
        int ComparePaths(int pa, int pb)
        {
            if (pa == pb) return 0;
            if (pa == FileIndex.NoParent) return -1;
            if (pb == FileIndex.NoParent) return 1;
            string sa = ctx.Paths.Prefix(pa), sb = ctx.Paths.Prefix(pb);
            int c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = string.CompareOrdinal(sa, sb);
            return c != 0 ? c : pa.CompareTo(pb);
        }

        // Same as ResultSorter's extension ranks: case-insensitive extension order ("" first).
        int CompareExt(int a, int b)
            => ExtMatcher.ExtensionOf(index, a).CompareTo(ExtMatcher.ExtensionOf(index, b), StringComparison.OrdinalIgnoreCase);
    }
}

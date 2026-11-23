namespace Anything.Core.Search;

/// <summary>Sorts result ids with precomputed keys and a parallel merge sort.</summary>
internal static class ResultSorter
{
    internal struct Item
    {
        public long K1;
        public long K2;
        public ulong P0, P1, P2; // first 12 chars of the name in name order (NameOrder.Prefix)
        public int Id;
    }

    // Ascending only: descending turns this order round (Descend), as the presorted order does, so both agree exactly.
    readonly struct ItemComparer(FileIndex index) : IComparer<Item>
    {
        public int Compare(Item a, Item b)
        {
            int c = a.K1.CompareTo(b.K1);
            if (c == 0) c = a.K2.CompareTo(b.K2);
            if (c == 0) c = a.P0.CompareTo(b.P0);
            if (c == 0) c = a.P1.CompareTo(b.P1);
            if (c == 0) c = a.P2.CompareTo(b.P2);
            if (c == 0 && a.Id != b.Id)
            {
                c = NameOrder.CompareWithTieBreak(index.Name(a.Id), index.Name(b.Id));
            }
            return c != 0 ? c : a.Id.CompareTo(b.Id); // deterministic tie-break
        }
    }

    /// <summary>
    /// Keys whose equal values stay in name order, A to Z, whichever way the column is sorted, as in File Explorer (files
    /// with the same date, size or type). Name and Path are the name order themselves: descending turns it round whole.
    /// </summary>
    internal static bool TiesByName(SortKey sort) => sort is not (SortKey.Name or SortKey.Path);

    /// <summary>
    /// Turns an ascending order round for descending. With <paramref name="sameKey"/> (neighbours with equal keys) each
    /// run of equal keys keeps its ascending order inside, so ties stay A to Z; without it the order is reversed whole.
    /// </summary>
    internal static void Descend<T>(T[] order, Func<T, T, bool>? sameKey)
    {
        Array.Reverse(order);
        if (sameKey == null) return;
        int start = 0;
        for (int i = 1; i <= order.Length; i++)
        {
            if (i < order.Length && sameKey(order[i - 1], order[i])) continue;
            if (i - start > 1) Array.Reverse(order, start, i - start);
            start = i;
        }
    }

    public static void Sort(FileIndex index, SearchContext ctx, int[] ids, SortKey sort, bool descending, CancellationToken ct)
        => Sort(index, ctx, ids, sort, descending, ct, runs: null);

    /// <summary>
    /// As <see cref="Sort(FileIndex, SearchContext, int[], SortKey, bool, CancellationToken)"/>; ascending, it can also fill
    /// <paramref name="runs"/> (as long as <paramref name="ids"/>) with a number per position that neighbours share exactly when
    /// their keys are equal, so the presorted order can turn round for descending without comparing keys again.
    /// </summary>
    internal static void Sort(FileIndex index, SearchContext ctx, int[] ids, SortKey sort, bool descending, CancellationToken ct,
        int[]? runs)
    {
        int n = ids.Length;
        if (runs != null && descending) throw new ArgumentException("Runs are numbered in ascending order.", nameof(runs));
        if (n <= 1)
        {
            if (runs != null && n == 1) runs[0] = 0;
            return;
        }
        var items = new Item[n];

        Dictionary<string, int>? extRank = null;
        if (sort is SortKey.Extension or SortKey.Type) extRank = BuildExtRanks(index, ids);
        Dictionary<int, int>? parentRank = null;
        if (sort == SortKey.Path) parentRank = BuildParentRanks(index, ctx, ids);

        void Fill(int i)
        {
            int id = ids[i];
            string name = index.Name(id);
            long group = index.IsDirectory(id) ? 0 : 1;
            ref var it = ref items[i];
            it.Id = id;
            // The prefix keys must agree with the full name comparison, or ties broken by name would disagree with it.
            NameOrder.Prefix(name, out it.P0, out it.P1, out it.P2);
            switch (sort)
            {
                case SortKey.Name: it.K1 = group; break;
                case SortKey.Path:
                {
                    int p = index.Parent(id);
                    it.K1 = group;
                    it.K2 = p == FileIndex.NoParent ? -1 : parentRank![p];
                    break;
                }
                case SortKey.Size: it.K1 = index.Size(id); break;
                case SortKey.Extension: it.K1 = ExtRank(extRank!, index, id); break;
                case SortKey.Type: it.K1 = group; it.K2 = ExtRank(extRank!, index, id); break;
                case SortKey.DateModified: it.K1 = index.Modified(id); break;
                case SortKey.DateCreated: it.K1 = index.Created(id); break;
                case SortKey.Attributes: it.K1 = (long)index.Attributes(id); break;
                case SortKey.DateAccessed: it.K1 = ctx.Accessed(id); break;
                case SortKey.DateRecentlyChanged: it.K1 = index.RecentChange(id); break;
                case SortKey.RunCount: it.K1 = ctx.Runs?.Get(id).Count ?? 0; break;
                case SortKey.DateRun: it.K1 = ctx.Runs?.Get(id).LastRun ?? 0; break;
            }
        }

        if (n < 65536) for (int i = 0; i < n; i++) Fill(i);
        else Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, Fill);

        ParallelSort(items, new ItemComparer(index), ct);
        if (descending) Descend<Item>(items, TiesByName(sort) ? static (a, b) => a.K1 == b.K1 && a.K2 == b.K2 : null);
        for (int i = 0; i < n; i++) ids[i] = items[i].Id;
        if (runs != null)
        {
            int r = 0;
            runs[0] = 0;
            for (int i = 1; i < n; i++)
            {
                if (items[i].K1 != items[i - 1].K1 || items[i].K2 != items[i - 1].K2) r++;
                runs[i] = r;
            }
        }
    }

    static int ExtRank(Dictionary<string, int> ranks, FileIndex index, int id)
    {
        var ext = ExtMatcher.ExtensionOf(index, id);
        return ranks.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(ext, out int r) ? r : 0;
    }

    static Dictionary<string, int> BuildExtRanks(FileIndex index, int[] ids)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        var lookup = set.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (int id in ids)
        {
            var ext = ExtMatcher.ExtensionOf(index, id);
            if (!lookup.Contains(ext)) set.Add(ext.ToString());
        }
        var sorted = set.ToList();
        sorted.Sort((a, b) =>
        {
            int c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a, b);
        });
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sorted.Count; i++) ranks.TryAdd(sorted[i], i);
        return ranks;
    }

    static Dictionary<int, int> BuildParentRanks(FileIndex index, SearchContext ctx, int[] ids)
    {
        var parents = new HashSet<int>();
        foreach (int id in ids)
        {
            int p = index.Parent(id);
            if (p != FileIndex.NoParent) parents.Add(p);
        }
        var list = parents.Select(p => (Id: p, Path: ctx.Paths.Prefix(p))).ToList();
        list.Sort((a, b) =>
        {
            int c = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            if (c == 0) c = string.CompareOrdinal(a.Path, b.Path);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        });
        var ranks = new Dictionary<int, int>(list.Count);
        for (int i = 0; i < list.Count; i++) ranks[list[i].Id] = i;
        return ranks;
    }

    internal static void ParallelSort<T, TComparer>(T[] items, TComparer cmp, CancellationToken ct)
        where TComparer : struct, IComparer<T>
    {
        int n = items.Length;
        if (n < 32768)
        {
            items.AsSpan().Sort(cmp);
            return;
        }
        int chunks = 1;
        int procs = Math.Clamp(Environment.ProcessorCount, 1, 64);
        while (chunks < procs) chunks <<= 1;
        int chunkSize = (n + chunks - 1) / chunks;
        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, chunks, po, c =>
        {
            int lo = c * chunkSize, hi = Math.Min(n, lo + chunkSize);
            if (lo < hi) items.AsSpan(lo, hi - lo).Sort(cmp);
        });
        var src = items;
        var dst = new T[n];
        for (long width = chunkSize; width < n; width *= 2)
        {
            ct.ThrowIfCancellationRequested();
            int w = (int)width;
            int pairs = (int)((n + 2 * width - 1) / (2 * width));
            var s = src; var d = dst;
            Parallel.For(0, pairs, po, k =>
            {
                long lo = (long)k * 2 * w;
                int mid = (int)Math.Min(lo + w, n), hi = (int)Math.Min(lo + 2L * w, n);
                Merge(s, (int)lo, mid, hi, d, cmp);
            });
            (src, dst) = (dst, src);
        }
        if (!ReferenceEquals(src, items)) Array.Copy(src, items, n);
    }

    static void Merge<T, TComparer>(T[] src, int lo, int mid, int hi, T[] dst, TComparer cmp)
        where TComparer : struct, IComparer<T>
    {
        int i = lo, j = mid, k = lo;
        while (i < mid && j < hi) dst[k++] = cmp.Compare(src[j], src[i]) < 0 ? src[j++] : src[i++];
        while (i < mid) dst[k++] = src[i++];
        while (j < hi) dst[k++] = src[j++];
    }
}

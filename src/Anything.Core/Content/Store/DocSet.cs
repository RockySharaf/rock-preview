using System.Numerics;

namespace Anything.Core.Content.Store;

/// <summary>
/// Documents of one store view: per segment ordinal, a sorted int[] or a bitmap when dense; immutable. Sets built on the
/// same view combine; a segment one set lacks counts as empty there.
/// </summary>
internal sealed class DocSet
{
    /// <summary>One segment's documents: sorted docs, or a bitmap with its count; null parts are empty.</summary>
    internal sealed class Part
    {
        public readonly int DocCount;
        public readonly int Count;
        public readonly int[]? Docs;    // sorted, distinct
        public readonly ulong[]? Bits;  // (DocCount + 63) / 64 words

        Part(int docCount, int count, int[]? docs, ulong[]? bits)
        {
            DocCount = docCount;
            Count = count;
            Docs = docs;
            Bits = bits;
        }

        public static Part? Sparse(int docCount, int[] docs, int count)
        {
            if (count == 0) return null;
            if (IsDense(count, docCount))
            {
                var bits = new ulong[Words(docCount)];
                for (int i = 0; i < count; i++) bits[docs[i] >> 6] |= 1UL << docs[i];
                return new Part(docCount, count, null, bits);
            }
            return new Part(docCount, count, docs.Length == count ? docs : docs[..count], null);
        }

        public static Part? Dense(int docCount, ulong[] bits)
        {
            int count = 0;
            foreach (ulong w in bits) count += BitOperations.PopCount(w);
            if (count == 0) return null;
            if (IsDense(count, docCount)) return new Part(docCount, count, null, bits);
            var docs = new int[count];
            int k = 0;
            for (int w = 0; w < bits.Length; w++)
                for (ulong v = bits[w]; v != 0; v &= v - 1) docs[k++] = (w << 6) + BitOperations.TrailingZeroCount(v);
            return new Part(docCount, count, docs, null);
        }

        public bool Contains(int doc)
        {
            if (Bits != null) return (uint)doc < (uint)DocCount && (Bits[doc >> 6] & (1UL << doc)) != 0;
            return Array.BinarySearch(Docs!, 0, Count, doc) >= 0;
        }

        /// <summary>The bitmap of this part, widened to <paramref name="docCount"/> documents.</summary>
        public ulong[] ToBits(int docCount)
        {
            var bits = new ulong[Words(docCount)];
            if (Bits != null) Bits.AsSpan(0, Math.Min(Bits.Length, bits.Length)).CopyTo(bits);
            else foreach (int d in Docs!) if (d < docCount) bits[d >> 6] |= 1UL << d;
            return bits;
        }
    }

    readonly Part?[] _parts;

    DocSet(Part?[] parts, int count)
    {
        _parts = parts;
        Count = count;
    }

    internal static DocSet FromParts(Part?[] parts)
    {
        int count = 0;
        foreach (var p in parts) count += p?.Count ?? 0;
        return new DocSet(parts, count);
    }

    /// <summary>No documents.</summary>
    public static DocSet Empty { get; } = new([], 0);

    /// <summary>Documents in the set.</summary>
    public int Count { get; }
    /// <summary>Segment ordinals the set spans (the view's segment count when built for it).</summary>
    public int SegmentCount => _parts.Length;
    /// <summary>Documents in one segment of the set.</summary>
    public int CountIn(int segment) => (uint)segment < (uint)_parts.Length ? _parts[segment]?.Count ?? 0 : 0;

    internal Part? PartAt(int segment) => (uint)segment < (uint)_parts.Length ? _parts[segment] : null;

    public bool Contains(DocRef doc) => PartAt(doc.Segment) is { } p && p.Contains(doc.Doc);

    /// <summary>Calls visit(segmentOrdinal, doc) in order; checks ct every 65,536 documents.</summary>
    public void ForEach(Action<int, int> visit, CancellationToken ct)
    {
        int n = 0;
        for (int s = 0; s < _parts.Length; s++)
        {
            if (_parts[s] is not { } p) continue;
            if (p.Docs != null)
            {
                for (int i = 0; i < p.Count; i++)
                {
                    if ((++n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                    visit(s, p.Docs[i]);
                }
            }
            else
            {
                var bits = p.Bits!;
                for (int w = 0; w < bits.Length; w++)
                    for (ulong v = bits[w]; v != 0; v &= v - 1)
                    {
                        if ((++n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                        visit(s, (w << 6) + BitOperations.TrailingZeroCount(v));
                    }
            }
        }
    }

    /// <summary>The documents of one segment as a one-segment set (ordinal 0), for caching per term and segment.</summary>
    public DocSet Slice(int segment) => PartAt(segment) is { } p ? new DocSet([p], p.Count) : Empty;

    /// <summary>One-segment sets side by side: parts[i]'s documents become segment ordinal i (null = none).</summary>
    public static DocSet Join(IReadOnlyList<DocSet?> parts)
    {
        var all = new Part?[parts.Count];
        for (int i = 0; i < all.Length; i++) all[i] = parts[i]?.PartAt(0);
        return FromParts(all);
    }

    /// <summary>Documents in both sets.</summary>
    public DocSet And(DocSet other)
    {
        var parts = new Part?[Math.Min(_parts.Length, other._parts.Length)];
        for (int s = 0; s < parts.Length; s++)
            if (_parts[s] is { } a && other._parts[s] is { } b) parts[s] = And(a, b);
        return FromParts(parts);
    }

    /// <summary>Documents in either set.</summary>
    public DocSet Or(DocSet other)
    {
        var parts = new Part?[Math.Max(_parts.Length, other._parts.Length)];
        for (int s = 0; s < parts.Length; s++)
        {
            Part? a = PartAt(s), b = other.PartAt(s);
            parts[s] = a == null ? b : b == null ? a : Or(a, b);
        }
        return FromParts(parts);
    }

    /// <summary>Documents in this set and not in the other.</summary>
    public DocSet Except(DocSet other)
    {
        var parts = new Part?[_parts.Length];
        for (int s = 0; s < parts.Length; s++)
        {
            Part? a = _parts[s], b = other.PartAt(s);
            parts[s] = a == null || b == null ? a : Except(a, b);
        }
        return FromParts(parts);
    }

    static Part? And(Part a, Part b)
    {
        int docCount = Math.Max(a.DocCount, b.DocCount);
        if (a.Bits != null && b.Bits != null)
        {
            var bits = new ulong[Words(docCount)];
            int n = Math.Min(a.Bits.Length, b.Bits.Length);
            for (int i = 0; i < n; i++) bits[i] = a.Bits[i] & b.Bits[i];
            return Part.Dense(docCount, bits);
        }
        if (a.Docs != null && b.Docs != null)
        {
            var docs = new int[Math.Min(a.Count, b.Count)];
            int i = 0, j = 0, k = 0;
            while (i < a.Count && j < b.Count)
            {
                int x = a.Docs[i], y = b.Docs[j];
                if (x == y) { docs[k++] = x; i++; j++; }
                else if (x < y) i++;
                else j++;
            }
            return Part.Sparse(docCount, docs, k);
        }
        // One sparse, one dense: keep the sparse documents the bitmap has.
        var sparse = a.Docs != null ? a : b;
        var dense = a.Docs != null ? b : a;
        var kept = new int[sparse.Count];
        int m = 0;
        foreach (int d in sparse.Docs!) if (dense.Contains(d)) kept[m++] = d;
        return Part.Sparse(docCount, kept, m);
    }

    static Part? Or(Part a, Part b)
    {
        int docCount = Math.Max(a.DocCount, b.DocCount);
        if (a.Docs != null && b.Docs != null && !IsDense(a.Count + b.Count, docCount))
        {
            var docs = new int[a.Count + b.Count];
            int i = 0, j = 0, k = 0;
            while (i < a.Count || j < b.Count)
            {
                if (j >= b.Count || (i < a.Count && a.Docs[i] < b.Docs[j])) docs[k++] = a.Docs[i++];
                else if (i >= a.Count || b.Docs[j] < a.Docs[i]) docs[k++] = b.Docs[j++];
                else { docs[k++] = a.Docs[i]; i++; j++; }
            }
            return Part.Sparse(docCount, docs, k);
        }
        var bits = a.ToBits(docCount);
        if (b.Bits != null) for (int w = 0; w < b.Bits.Length && w < bits.Length; w++) bits[w] |= b.Bits[w];
        else foreach (int d in b.Docs!) bits[d >> 6] |= 1UL << d;
        return Part.Dense(docCount, bits);
    }

    static Part? Except(Part a, Part b)
    {
        if (a.Docs != null)
        {
            var kept = new int[a.Count];
            int m = 0;
            foreach (int d in a.Docs) if (!b.Contains(d)) kept[m++] = d;
            return Part.Sparse(a.DocCount, kept, m);
        }
        var bits = (ulong[])a.Bits!.Clone();
        if (b.Bits != null) for (int w = 0; w < bits.Length && w < b.Bits.Length; w++) bits[w] &= ~b.Bits[w];
        else foreach (int d in b.Docs!) if (d < a.DocCount) bits[d >> 6] &= ~(1UL << d);
        return Part.Dense(a.DocCount, bits);
    }

    /// <summary>A bitmap (DocCount / 8 bytes) is smaller than a sorted list (4 bytes each) above one document in 32.</summary>
    internal static bool IsDense(int count, int docCount) => docCount > 0 && count > docCount / 32;

    internal static int Words(int docCount) => (docCount + 63) >> 6;
}

/// <summary>
/// Collects documents per segment in any order, repeats allowed (a prefix's terms are added one after another), then
/// freezes them once (the set takes over the buffers: don't add after Build). Documents outside their segment's range
/// are dropped.
/// </summary>
internal sealed class DocSetBuilder
{
    readonly int[] _docCounts;
    readonly int[]?[] _docs;
    readonly int[] _counts;
    readonly bool[] _sorted;
    readonly ulong[]?[] _bits;

    public DocSetBuilder(IReadOnlyList<int> segmentDocCounts)
    {
        _docCounts = [.. segmentDocCounts];
        int n = _docCounts.Length;
        _docs = new int[]?[n];
        _counts = new int[n];
        _sorted = new bool[n];
        Array.Fill(_sorted, true);
        _bits = new ulong[]?[n];
    }

    public void Add(int segment, int doc)
    {
        if ((uint)segment >= (uint)_docCounts.Length || (uint)doc >= (uint)_docCounts[segment]) return;
        if (_bits[segment] is { } bits) { bits[doc >> 6] |= 1UL << doc; return; }
        var docs = _docs[segment];
        int count = _counts[segment];
        if (docs == null || count == docs.Length)
        {
            if (DocSet.IsDense(count + 1, _docCounts[segment])) { ToBits(segment)[doc >> 6] |= 1UL << doc; return; }
            Array.Resize(ref docs, Math.Max(16, Math.Min(count * 2, _docCounts[segment])));
            _docs[segment] = docs;
        }
        if (count > 0 && docs[count - 1] >= doc) _sorted[segment] = false;
        docs[count] = doc;
        _counts[segment] = count + 1;
    }

    public void AddSorted(int segment, ReadOnlySpan<int> docs)
    {
        foreach (int d in docs) Add(segment, d);
    }

    public DocSet Build()
    {
        var parts = new DocSet.Part?[_docCounts.Length];
        for (int s = 0; s < parts.Length; s++)
        {
            if (_bits[s] is { } bits) { parts[s] = DocSet.Part.Dense(_docCounts[s], bits); continue; }
            if (_docs[s] is not { } docs) continue;
            int count = _counts[s];
            if (!_sorted[s])
            {
                Array.Sort(docs, 0, count);
                int k = 0;
                for (int i = 0; i < count; i++)
                    if (k == 0 || docs[k - 1] != docs[i]) docs[k++] = docs[i];
                count = k;
            }
            parts[s] = DocSet.Part.Sparse(_docCounts[s], docs, count);
        }
        return DocSet.FromParts(parts);
    }

    ulong[] ToBits(int segment)
    {
        var bits = new ulong[DocSet.Words(_docCounts[segment])];
        if (_docs[segment] is { } docs)
            for (int i = 0; i < _counts[segment]; i++) bits[docs[i] >> 6] |= 1UL << docs[i];
        _docs[segment] = null;
        _counts[segment] = 0;
        _bits[segment] = bits;
        return bits;
    }
}

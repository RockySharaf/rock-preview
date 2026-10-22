namespace Anything.Core.Content;

/// <summary>
/// FileIndex ids of stored documents for one index generation; immutable once built (copy-on-write per 4,096-entry chunk,
/// so a builder shares every chunk it doesn't change). -1 = not mapped.
/// </summary>
internal sealed class DocIdMap
{
    internal const int ChunkBits = 12, ChunkSize = 1 << ChunkBits, ChunkMask = ChunkSize - 1;

    readonly Dictionary<uint, SegmentIds> _segments;
    readonly uint[] _order; // segment ids ascending, for ForEachMapped

    DocIdMap(Dictionary<uint, SegmentIds> segments, long generation)
    {
        _segments = segments;
        Generation = generation;
        _order = [.. segments.Keys.Order()];
    }

    /// <summary>A map with no documents for a generation.</summary>
    public static DocIdMap Empty(long generation) => new([], generation);

    /// <summary>The FileIndex generation the ids belong to.</summary>
    public long Generation { get; }

    /// <summary>Segments the map has entries for, ascending.</summary>
    public IReadOnlyList<uint> SegmentIdList => _order;

    /// <summary>Id of a document, -1 when not mapped or the segment is unknown.</summary>
    public int FileId(uint segmentId, int doc) => _segments.TryGetValue(segmentId, out var s) ? s.FileId(doc) : -1;

    /// <summary>The map has entries for this segment.</summary>
    public bool HasSegment(uint segmentId) => _segments.ContainsKey(segmentId);

    /// <summary>One segment's ids (one lookup per segment when mapping many documents); null when unknown.</summary>
    public SegmentIds? Segment(uint segmentId) => _segments.GetValueOrDefault(segmentId);

    /// <summary>Calls visit(segmentId, doc, fileId) for every mapped document, segments ascending, documents ascending.</summary>
    public void ForEachMapped(Action<uint, int, int> visit)
    {
        foreach (uint id in _order)
        {
            var s = _segments[id];
            for (int c = 0; c < s.Chunks.Length; c++)
            {
                if (s.Chunks[c] is not { } chunk) continue;
                int baseDoc = c << ChunkBits;
                for (int i = 0; i < chunk.Length; i++)
                    if (chunk[i] >= 0) visit(id, baseDoc + i, chunk[i]);
            }
        }
    }

    /// <summary>A builder starting from this map (unchanged chunks shared).</summary>
    public Builder ToBuilder() => new(this);

    /// <summary>The ids of one segment's documents, in 4,096-entry chunks (null chunk = none mapped); never changed once built.</summary>
    internal sealed class SegmentIds
    {
        internal SegmentIds(int docCount, int[]?[] chunks)
        {
            DocCount = docCount;
            Chunks = chunks;
        }

        /// <summary>Documents in the segment.</summary>
        public int DocCount { get; }
        internal int[]?[] Chunks { get; }

        /// <summary>Id of a document, -1 when not mapped or out of range.</summary>
        public int FileId(int doc)
        {
            if ((uint)doc >= (uint)DocCount) return -1;
            var chunk = Chunks[doc >> ChunkBits];
            return chunk == null ? -1 : chunk[doc & ChunkMask];
        }
    }

    /// <summary>Changes a copy of a map; not thread-safe. Build leaves the builder usable (it copies chunks again on the next change).</summary>
    internal sealed class Builder
    {
        readonly Dictionary<uint, Seg> _segs = [];

        sealed class Seg(int docCount, int[]?[] chunks, bool[] owned)
        {
            public readonly int DocCount = docCount;
            public readonly int[]?[] Chunks = chunks;
            public readonly bool[] Owned = owned;
        }

        internal Builder(DocIdMap from)
        {
            foreach (var (id, s) in from._segments)
                _segs[id] = new Seg(s.DocCount, (int[]?[])s.Chunks.Clone(), new bool[s.Chunks.Length]);
        }

        /// <summary>Adds a segment with no document mapped (replacing one with the same id).</summary>
        public void AddSegment(uint segmentId, int docCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(docCount);
            int chunks = (docCount + ChunkMask) >> ChunkBits;
            _segs[segmentId] = new Seg(docCount, new int[]?[chunks], new bool[chunks]);
        }

        /// <summary>Drops a segment and its ids.</summary>
        public void RemoveSegment(uint segmentId) => _segs.Remove(segmentId);

        /// <summary>The builder has this segment.</summary>
        public bool HasSegment(uint segmentId) => _segs.ContainsKey(segmentId);

        /// <summary>Id of a document as the builder has it now (-1 = not mapped or unknown).</summary>
        public int FileId(uint segmentId, int doc)
        {
            if (!_segs.TryGetValue(segmentId, out var s) || (uint)doc >= (uint)s.DocCount) return -1;
            var chunk = s.Chunks[doc >> ChunkBits];
            return chunk == null ? -1 : chunk[doc & ChunkMask];
        }

        /// <summary>Maps a document to an id (-1 unmaps it).</summary>
        public void Set(uint segmentId, int doc, int fileId)
        {
            if (!_segs.TryGetValue(segmentId, out var s)) throw new KeyNotFoundException($"Segment {segmentId} is not in the map.");
            if ((uint)doc >= (uint)s.DocCount) throw new ArgumentOutOfRangeException(nameof(doc));
            int c = doc >> ChunkBits;
            if (fileId < 0 && s.Chunks[c] == null) return;
            Writable(s, c)[doc & ChunkMask] = Math.Max(fileId, -1);
        }

        /// <summary>Moves a merged segment's ids to the new segment through old-to-new local remaps (-1 = dropped).</summary>
        public void Merge(IReadOnlyList<(uint OldSegment, int[] OldToNew)> remaps, uint newSegment, int newDocCount)
        {
            var moved = new List<(int Doc, int FileId)>();
            foreach (var (old, oldToNew) in remaps)
            {
                if (!_segs.TryGetValue(old, out var s)) continue;
                for (int c = 0; c < s.Chunks.Length; c++)
                {
                    if (s.Chunks[c] is not { } chunk) continue;
                    int baseDoc = c << ChunkBits;
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        int doc = baseDoc + i;
                        if (chunk[i] < 0 || doc >= oldToNew.Length) continue;
                        int to = oldToNew[doc];
                        if ((uint)to < (uint)newDocCount) moved.Add((to, chunk[i]));
                    }
                }
            }
            foreach (var (old, _) in remaps) _segs.Remove(old);
            AddSegment(newSegment, newDocCount);
            foreach (var (doc, fileId) in moved) Set(newSegment, doc, fileId);
        }

        /// <summary>Translates every id through a FileIndex.Compact remap (-1 = removed, ids past its end are removed too).</summary>
        public void RemapIds(int[] remap)
        {
            foreach (var s in _segs.Values)
                for (int c = 0; c < s.Chunks.Length; c++)
                {
                    if (s.Chunks[c] is not { } chunk) continue;
                    // Every entry is overwritten: a chunk the builder owns is translated in place, a shared one into a fresh
                    // array (never copied first).
                    var to = s.Owned[c] ? chunk : new int[chunk.Length];
                    bool any = false;
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        int id = chunk[i];
                        int n = (uint)id < (uint)remap.Length ? remap[id] : -1;
                        to[i] = n;
                        any |= n >= 0;
                    }
                    if (!any) { s.Chunks[c] = null; s.Owned[c] = false; }
                    else { s.Chunks[c] = to; s.Owned[c] = true; }
                }
        }

        /// <summary>The finished map for a generation; later changes to the builder never reach it.</summary>
        public DocIdMap Build(long generation)
        {
            var segments = new Dictionary<uint, SegmentIds>(_segs.Count);
            foreach (var (id, s) in _segs)
            {
                segments[id] = new SegmentIds(s.DocCount, (int[]?[])s.Chunks.Clone());
                Array.Clear(s.Owned); // the map shares these chunks now: copy again before writing
            }
            return new DocIdMap(segments, generation);
        }

        static int[] Writable(Seg s, int c)
        {
            if (s.Owned[c] && s.Chunks[c] is { } own) return own;
            int length = Math.Min(ChunkSize, s.DocCount - (c << ChunkBits));
            var copy = new int[length];
            if (s.Chunks[c] is { } shared) shared.AsSpan().CopyTo(copy);
            else copy.AsSpan().Fill(-1);
            s.Chunks[c] = copy;
            s.Owned[c] = true;
            return copy;
        }
    }
}

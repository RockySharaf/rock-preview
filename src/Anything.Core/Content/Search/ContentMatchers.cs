using Anything.Core.Content.Store;
using Anything.Core.Search;

namespace Anything.Core.Content.Search;

/// <summary>
/// Documents of a DocSet mapped to ids in Prepare (bits, O(matches)): one array read per document through the id map,
/// deleted documents skipped, nothing decompressed. IsMatch is a bounds-checked bit test, Cost 2.
/// </summary>
internal sealed class ContentSetMatcher(DocSet set) : Matcher
{
    ulong[] _bits = [];

    /// <summary>The documents it matches.</summary>
    internal DocSet Set { get; } = set;
    public override int Cost => 2;

    public override void Prepare(SearchContext ctx, CancellationToken ct)
    {
        var bits = new ulong[(ctx.Count + 63) >> 6];
        if (ctx.Content?.View is { } view && ctx.ContentMap is { } map)
        {
            var store = view.Store;
            var segments = store.Segments;
            int count = ctx.Count;
            int visited = 0;
            int n = Math.Min(Set.SegmentCount, segments.Count);
            for (int s = 0; s < n; s++)
            {
                if (Set.PartAt(s) is not { } part) continue;
                if (map.Segment(segments[s].Id) is not { } ids)
                {
                    // A segment the map doesn't know yet (merged or committed since it was published): ask again later.
                    ctx.ContentStale = true;
                    continue;
                }
                if (part.Docs is { } docs)
                {
                    for (int i = 0; i < part.Count; i++)
                    {
                        if ((++visited & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                        Mark(bits, store, ids, s, docs[i], count);
                    }
                }
                else
                {
                    var words = part.Bits!;
                    for (int w = 0; w < words.Length; w++)
                        for (ulong v = words[w]; v != 0; v &= v - 1)
                        {
                            if ((++visited & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                            Mark(bits, store, ids, s, (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(v), count);
                        }
                }
            }
        }
        _bits = bits;
    }

    static void Mark(ulong[] bits, IStoreView store, DocIdMap.SegmentIds ids, int segment, int doc, int count)
    {
        int id = ids.FileId(doc);
        if ((uint)id >= (uint)count || store.IsDeleted(new DocRef(segment, doc))) return;
        bits[id >> 6] |= 1UL << id;
    }

    public override bool IsMatch(int id, SearchContext ctx)
    {
        var bits = _bits;
        int w = id >> 6;
        return (uint)w < (uint)bits.Length && (bits[w] & (1UL << id)) != 0;
    }
}

/// <summary>Name or content; content is skipped while ctx.NameOnlyPass (the name-first partition).</summary>
internal sealed class NameOrContentMatcher(Matcher name, ContentSetMatcher content) : Matcher
{
    /// <summary>The name test.</summary>
    internal Matcher Name { get; } = name;
    /// <summary>The content test.</summary>
    internal ContentSetMatcher Content { get; } = content;
    public override int Cost => Name.Cost + Content.Cost;
    public override bool IsMatch(int id, SearchContext ctx) => Name.IsMatch(id, ctx) || (!ctx.NameOnlyPass && Content.IsMatch(id, ctx));
}

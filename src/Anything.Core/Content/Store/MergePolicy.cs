namespace Anything.Core.Content.Store;

/// <summary>
/// Tier = ⌊log8(live docs / 4,096)⌋ (tier 0 is under 32,768 documents, tier 1 under 262,144, tier 2 under 2,097,152).
/// Merges go by level = ⌊log8(live docs)⌋, which is tier + 4 from 4,096 documents up and splits tier 0 into finer levels
/// below it: when a level holds 8 or more segments its 8 smallest merge. (With tier 0 merged as one, commits of a few
/// documents would rewrite its largest segment, up to 32,767 documents, every seventh commit; by level, every document is
/// merged about log8(N) times.) Full segments (≥ 1,000,000 documents or ≥ 1 GiB) are left out of the counts. A segment
/// with 30% or more of its documents deleted is rewritten alone; one with every document deleted goes first (it costs
/// nothing). A merge of more than 65,536 live documents is Large: only planned when allowLarge (idle for 2 minutes and on
/// mains power).
/// </summary>
internal static class MergePolicy
{
    public const int BaseDocs = 4096, Factor = 8, SegmentsPerMerge = 8, LargeDocs = 65_536, FullDocs = 1_000_000;
    public const long FullBytes = 1L << 30;
    public const double DeletedShare = 0.30;

    /// <summary>The tier of a segment with this many live documents (the manifest's tier byte).</summary>
    public static int Tier(long liveDocs)
    {
        int tier = 0;
        for (long limit = (long)BaseDocs * Factor; liveDocs >= limit && tier < 16; limit *= Factor) tier++;
        return tier;
    }

    /// <summary>The merge level of a segment: ⌊log8(live docs)⌋ (tier + 4 from 4,096 documents up).</summary>
    public static int Level(long liveDocs)
    {
        int level = 0;
        for (long limit = Factor; liveDocs >= limit && level < 24; limit *= Factor) level++;
        return level;
    }

    /// <summary>A full segment: never merged with others (only rewritten alone once 30% of it is deleted).</summary>
    public static bool IsFull(int docs, long bytes) => docs >= FullDocs || bytes >= FullBytes;

    /// <summary>The next merge, or null when none is due (allowLarge = idle for 2 minutes and on mains power).</summary>
    public static MergePlan? Plan(IReadOnlyList<(uint Id, int Docs, int Deleted, long Bytes)> segments, bool allowLarge)
    {
        // Wholly deleted segments: dropping them is free.
        var empty = segments.Where(s => s.Docs > 0 && s.Deleted >= s.Docs).Select(s => s.Id).ToList();
        if (empty.Count > 0) return new MergePlan(empty, 0, false);

        var levels = new SortedDictionary<int, List<(uint Id, int Live)>>();
        foreach (var s in segments)
        {
            if (IsFull(s.Docs, s.Bytes)) continue;
            int live = Math.Max(0, s.Docs - s.Deleted);
            int level = Level(live);
            if (!levels.TryGetValue(level, out var list)) levels[level] = list = [];
            list.Add((s.Id, live));
        }
        foreach (var (_, list) in levels)
        {
            if (list.Count < SegmentsPerMerge) continue;
            var chosen = list.OrderBy(s => s.Live).ThenBy(s => s.Id).Take(SegmentsPerMerge).ToList();
            long docs = chosen.Sum(s => (long)s.Live);
            bool large = docs > LargeDocs;
            if (large && !allowLarge) continue;
            return new MergePlan([.. chosen.Select(s => s.Id).Order()], (int)Math.Min(int.MaxValue, docs), large);
        }

        (uint Id, int Live, double Share)? best = null;
        foreach (var s in segments)
        {
            if (s.Docs <= 0 || s.Deleted < DeletedShare * s.Docs) continue;
            int live = s.Docs - s.Deleted;
            if (live > LargeDocs && !allowLarge) continue;
            double share = (double)s.Deleted / s.Docs;
            if (best == null || share > best.Value.Share || (share == best.Value.Share && s.Id < best.Value.Id)) best = (s.Id, live, share);
        }
        return best is { } b ? new MergePlan([b.Id], b.Live, b.Live > LargeDocs) : null;
    }
}

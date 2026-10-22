using Anything.Core.Content.Store;

namespace Anything.Core.Content.Indexing;

/// <summary>
/// Pure merge-join of sorted candidates with store rows → bind, queue, re-key, delete and keep decisions (testable). The
/// follower streams its full reconcile through <see cref="Join"/> with compact candidate arrays (12 bytes a file, stamps
/// read from the index in slices); <see cref="Plan"/> is the same join over <see cref="Candidate"/> records, for tests and
/// small sets.
/// </summary>
internal static class Reconciler
{
    /// <summary>
    /// Decides what to do with every candidate and every stored document. Candidates must be sorted by key with no repeats
    /// (the follower keeps the lowest id per key). A candidate whose key a live row has is bound (and queued as well when
    /// the stamps differ or the candidate's date is unknown); a candidate with no row is re-keyed from the one orphan that
    /// has its name, size and date, else queued; an orphan nobody took is deleted when it is out of scope or its volume's
    /// verdict allows it, else kept.
    /// </summary>
    public static ReconcilePlan Plan(IReadOnlyList<Candidate> sortedCandidates, IEnumerable<(DocRef Ref, DocRow Row)> rowsByKey,
        Func<ushort, VolumeVerdict> volume, Func<DocRef, (uint NameHash, bool InScope)> orphanInfo)
    {
        var bind = new List<(DocRef, int)>();
        var queue = new List<Candidate>();
        var unmatched = new List<int>();
        var orphans = new List<(DocRef Ref, DocRow Row)>();
        Join(sortedCandidates.Count, i => sortedCandidates[i].Key, rowsByKey, new DelegateSink(
            (i, doc, row) =>
            {
                var c = sortedCandidates[i];
                bind.Add((doc, c.FileId));
                if (c.Modified == 0 || c.Size != row.Size || c.Modified != row.Modified) queue.Add(c);
            },
            unmatched.Add,
            (doc, row) => orphans.Add((doc, row))));

        var rekey = new List<(DocRef, Candidate)>();
        var taken = new HashSet<DocRef>();
        if (unmatched.Count > 0 && orphans.Count > 0)
        {
            var orphanTriples = new (uint, long, long)[orphans.Count];
            for (int i = 0; i < orphans.Count; i++)
                orphanTriples[i] = (orphanInfo(orphans[i].Ref).NameHash, orphans[i].Row.Size, orphans[i].Row.Modified);
            var candTriples = new (uint, long, long)[unmatched.Count];
            for (int i = 0; i < unmatched.Count; i++)
            {
                var c = sortedCandidates[unmatched[i]];
                candTriples[i] = (c.NameHash, c.Size, c.Modified);
            }
            foreach (var (ci, oi) in MatchOrphans(candTriples, orphanTriples))
            {
                rekey.Add((orphans[oi].Ref, sortedCandidates[unmatched[ci]]));
                taken.Add(orphans[oi].Ref);
                unmatched[ci] = -1;
            }
        }
        foreach (int i in unmatched)
            if (i >= 0) queue.Add(sortedCandidates[i]);

        var delete = new List<DocRef>();
        var kept = new List<DocRef>();
        foreach (var (doc, row) in orphans)
        {
            if (taken.Contains(doc)) continue;
            var verdict = volume(row.Volume);
            if (verdict != VolumeVerdict.Keep || !orphanInfo(doc).InScope) delete.Add(doc);
            else kept.Add(doc);
        }
        return new ReconcilePlan(bind, queue, rekey, delete, kept);
    }

    /// <summary>
    /// The streaming merge-join: candidate keys (ascending, unique, read through <paramref name="keyAt"/>) against live rows
    /// in key order. Each candidate and each row is reported once: matched, candidate only, or row only.
    /// </summary>
    public static void Join(int candidates, Func<int, ulong> keyAt, IEnumerable<(DocRef Ref, DocRow Row)> rowsByKey, IJoinSink sink, CancellationToken ct = default)
    {
        int i = 0;
        int polled = 0;
        foreach (var (doc, row) in rowsByKey)
        {
            if ((++polled & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            while (i < candidates && keyAt(i) < row.Key) sink.CandidateOnly(i++);
            if (i < candidates && keyAt(i) == row.Key) sink.Matched(i++, doc, row);
            else sink.RowOnly(doc, row);
        }
        while (i < candidates)
        {
            if ((++polled & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            sink.CandidateOnly(i++);
        }
    }

    /// <summary>
    /// Pairs candidates with orphans that have the same (name hash, size, date), only where the triple is unique on both
    /// sides: two copies of one file, or two orphans of one name, size and date, are read instead of guessed. Unknown
    /// dates (0) never match.
    /// </summary>
    public static List<(int Candidate, int Orphan)> MatchOrphans(IReadOnlyList<(uint NameHash, long Size, long Modified)> candidates,
        IReadOnlyList<(uint NameHash, long Size, long Modified)> orphans)
    {
        var result = new List<(int, int)>();
        if (candidates.Count == 0 || orphans.Count == 0) return result;
        var byTriple = new Dictionary<(uint, long, long), int>();
        for (int i = 0; i < orphans.Count; i++)
        {
            if (orphans[i].Modified == 0) continue;
            byTriple[orphans[i]] = byTriple.ContainsKey(orphans[i]) ? -1 : i;
        }
        var seen = new Dictionary<(uint, long, long), int>();
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Modified == 0) continue;
            seen[candidates[i]] = seen.ContainsKey(candidates[i]) ? -1 : i;
        }
        foreach (var (triple, ci) in seen)
            if (ci >= 0 && byTriple.TryGetValue(triple, out int oi) && oi >= 0) result.Add((ci, oi));
        result.Sort();
        return result;
    }

    sealed class DelegateSink(Action<int, DocRef, DocRow> matched, Action<int> candidateOnly, Action<DocRef, DocRow> rowOnly) : IJoinSink
    {
        public void Matched(int candidate, DocRef doc, in DocRow row) => matched(candidate, doc, row);
        public void CandidateOnly(int candidate) => candidateOnly(candidate);
        public void RowOnly(DocRef doc, in DocRow row) => rowOnly(doc, row);
    }
}

/// <summary>Receives the merge-join's decisions in key order.</summary>
internal interface IJoinSink
{
    /// <summary>A candidate and the live document with its key.</summary>
    void Matched(int candidate, DocRef doc, in DocRow row);
    /// <summary>A candidate with no live document.</summary>
    void CandidateOnly(int candidate);
    /// <summary>A live document with no candidate (an orphan).</summary>
    void RowOnly(DocRef doc, in DocRow row);
}

/// <summary>A file in scope, from the name index (the plan form; the follower's full reconcile keeps 12 bytes a file instead).</summary>
internal readonly record struct Candidate(ulong Key, int FileId, long Size, long Modified, ContentKind Kind, ushort Volume, uint NameHash);

/// <summary>What may happen to orphans of a volume.</summary>
internal enum VolumeVerdict : byte { Keep, MayDelete, Purge }

/// <summary>What one reconcile decided.</summary>
internal sealed record ReconcilePlan(IReadOnlyList<(DocRef Doc, int FileId)> Bind, IReadOnlyList<Candidate> Queue,
    IReadOnlyList<(DocRef Doc, Candidate To)> Rekey, IReadOnlyList<DocRef> Delete, IReadOnlyList<DocRef> Orphans);

/// <summary>
/// The mass-delete guard: when one reconcile would delete more than 20% of a volume's documents and more than 1,000 of
/// them (an empty mount, a share that briefly lists nothing), those deletes wait 10 minutes and only what a second
/// reconcile finds again is deleted. Keyed by document key, so it survives Compact and Clear.
/// </summary>
internal sealed class MassDeleteGuard
{
    /// <summary>The share of a volume's documents above which deletes wait.</summary>
    public const double Fraction = 0.2;
    /// <summary>The count above which deletes wait.</summary>
    public const int Minimum = 1000;
    /// <summary>How long they wait for a second reconcile.</summary>
    public const long DelayMs = 600_000;

    readonly Dictionary<ushort, (long Since, HashSet<ulong> Keys)> _postponed = [];

    /// <summary>Volumes with deletes waiting.</summary>
    public int Postponed => _postponed.Count;

    /// <summary>When a reconcile should run again to confirm waiting deletes; long.MaxValue when none wait.</summary>
    public long RecheckAt => _postponed.Count == 0 ? long.MaxValue : _postponed.Values.Min(p => p.Since) + DelayMs;

    /// <summary>
    /// The deletes of one volume that may go ahead now (the rest wait). <paramref name="volumeDocs"/> is the volume's live
    /// document count before deleting.
    /// </summary>
    public List<T> Filter<T>(ushort volume, int volumeDocs, IReadOnlyList<(ulong Key, T Item)> deletes, long nowMs)
    {
        bool large = deletes.Count > Minimum && deletes.Count > volumeDocs * Fraction;
        if (!_postponed.TryGetValue(volume, out var waiting))
        {
            if (!large) return [.. deletes.Select(d => d.Item)];
            _postponed[volume] = (nowMs, [.. deletes.Select(d => d.Key)]);
            return [];
        }
        if (nowMs < waiting.Since + DelayMs)
        {
            // Still waiting: forget the ones that came back; new ones wait with the rest.
            var keys = deletes.Select(d => d.Key).ToHashSet();
            waiting.Keys.IntersectWith(keys);
            if (!large)
            {
                _postponed.Remove(volume);
                return [.. deletes.Select(d => d.Item)];
            }
            foreach (var k in keys) waiting.Keys.Add(k);
            return [];
        }
        // A second reconcile after the wait: what it finds again is confirmed.
        var confirmed = deletes.Where(d => waiting.Keys.Contains(d.Key)).Select(d => d.Item).ToList();
        var fresh = deletes.Where(d => !waiting.Keys.Contains(d.Key)).ToList();
        _postponed.Remove(volume);
        if (fresh.Count > Minimum && fresh.Count > volumeDocs * Fraction) _postponed[volume] = (nowMs, [.. fresh.Select(d => d.Key)]);
        else confirmed.AddRange(fresh.Select(d => d.Item));
        return confirmed;
    }

    /// <summary>A volume whose deletes came out small: nothing waits for it any more.</summary>
    public void Clear(ushort volume) => _postponed.Remove(volume);
}

using Anything.Core.Content;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Indexing;

public class ReconcilerTests
{
    static DocRow Row(ulong key, long size = 10, long modified = 100, ushort volume = 0)
        => new(key, modified, size, volume, 0, ContentKind.Text, DocStatus.Ok, 1, DocFlags.None);

    static Candidate Cand(ulong key, int id, long size = 10, long modified = 100, uint name = 1, ushort volume = 0)
        => new(key, id, size, modified, ContentKind.Text, volume, name);

    [Fact]
    public void Same_stamp_binds_a_different_stamp_binds_and_queues_and_an_unknown_date_queues()
    {
        var rows = new[] { (new DocRef(0, 0), Row(1)), (new DocRef(0, 1), Row(2)), (new DocRef(0, 2), Row(3)) };
        var cands = new[] { Cand(1, 10), Cand(2, 20, size: 11), Cand(3, 30, modified: 0) };
        var plan = Reconciler.Plan(cands, rows, _ => VolumeVerdict.Keep, _ => (0, true));
        Assert.Equal([(new DocRef(0, 0), 10), (new DocRef(0, 1), 20), (new DocRef(0, 2), 30)], plan.Bind);
        Assert.Equal([20, 30], plan.Queue.Select(q => q.FileId));
        Assert.Empty(plan.Delete);
        Assert.Empty(plan.Orphans);
    }

    [Fact]
    public void A_candidate_with_no_row_takes_the_one_orphan_with_its_name_size_and_date()
    {
        var rows = new[] { (new DocRef(0, 0), Row(5, size: 42, modified: 7)) };
        var plan = Reconciler.Plan([Cand(9, 1, size: 42, modified: 7, name: 77)], rows, _ => VolumeVerdict.MayDelete, _ => (77, true));
        Assert.Single(plan.Rekey);
        Assert.Equal(new DocRef(0, 0), plan.Rekey[0].Doc);
        Assert.Equal(1, plan.Rekey[0].To.FileId);
        Assert.Empty(plan.Queue);
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void Two_orphans_or_two_candidates_alike_are_read_instead_of_guessed()
    {
        var rows = new[] { (new DocRef(0, 0), Row(5, 42, 7)), (new DocRef(0, 1), Row(6, 42, 7)) };
        var plan = Reconciler.Plan([Cand(9, 1, 42, 7, 77)], rows, _ => VolumeVerdict.MayDelete, _ => (77, true));
        Assert.Empty(plan.Rekey);
        Assert.Single(plan.Queue);
        Assert.Equal(2, plan.Delete.Count);

        var one = new[] { (new DocRef(0, 0), Row(5, 42, 7)) };
        var twoCands = Reconciler.Plan([Cand(8, 1, 42, 7, 77), Cand(9, 2, 42, 7, 77)], one, _ => VolumeVerdict.MayDelete, _ => (77, true));
        Assert.Empty(twoCands.Rekey);
        Assert.Equal(2, twoCands.Queue.Count);
    }

    [Fact]
    public void Orphans_are_deleted_by_verdict_or_scope_and_kept_otherwise()
    {
        var rows = new[]
        {
            (new DocRef(0, 0), Row(1, volume: 0)), // volume Keep, in scope: kept
            (new DocRef(0, 1), Row(2, volume: 1)), // MayDelete
            (new DocRef(0, 2), Row(3, volume: 2)), // Purge
            (new DocRef(0, 3), Row(4, volume: 0)), // Keep but out of scope
        };
        var plan = Reconciler.Plan([], rows, v => v switch { 0 => VolumeVerdict.Keep, 1 => VolumeVerdict.MayDelete, _ => VolumeVerdict.Purge },
            doc => (0, doc.Doc != 3));
        Assert.Equal([new DocRef(0, 0)], plan.Orphans);
        Assert.Equal([new DocRef(0, 1), new DocRef(0, 2), new DocRef(0, 3)], plan.Delete);
    }

    [Fact]
    public void Join_reports_every_candidate_and_row_once_in_key_order()
    {
        ulong[] keys = [1, 3, 5, 7];
        var rows = new[] { (new DocRef(0, 0), Row(2)), (new DocRef(0, 1), Row(3)), (new DocRef(1, 0), Row(7)), (new DocRef(1, 1), Row(9)) };
        var seen = new List<string>();
        Reconciler.Join(keys.Length, i => keys[i], rows, new Sink(seen));
        Assert.Equal(["c1", "r2", "m3", "c5", "m7", "r9"], seen);
    }

    sealed class Sink(List<string> seen) : IJoinSink
    {
        public void Matched(int candidate, DocRef doc, in DocRow row) => seen.Add("m" + row.Key);
        public void CandidateOnly(int candidate) => seen.Add("c" + (2 * candidate + 1));
        public void RowOnly(DocRef doc, in DocRow row) => seen.Add("r" + row.Key);
    }

    [Fact]
    public void Unknown_dates_never_match_an_orphan()
    {
        var matches = Reconciler.MatchOrphans([(1u, 10L, 0L)], [(1u, 10L, 0L)]);
        Assert.Empty(matches);
    }

    [Fact]
    public void The_mass_delete_guard_holds_large_deletes_and_lets_small_ones_through()
    {
        var guard = new MassDeleteGuard();
        var small = Enumerable.Range(0, 1000).Select(i => ((ulong)i, i)).ToList();
        Assert.Equal(1000, guard.Filter(3, volumeDocs: 2000, small, nowMs: 0).Count); // not more than 1,000
        var tenth = Enumerable.Range(0, 1200).Select(i => ((ulong)i, i)).ToList();
        Assert.Equal(1200, guard.Filter(3, volumeDocs: 10_000, tenth, nowMs: 0).Count); // not more than 20%
        var large = Enumerable.Range(0, 1500).Select(i => ((ulong)i, i)).ToList();
        Assert.Empty(guard.Filter(4, volumeDocs: 2000, large, nowMs: 0));
        Assert.Equal(MassDeleteGuard.DelayMs, guard.RecheckAt);
        // Too soon: still waiting.
        Assert.Empty(guard.Filter(4, 2000, large, nowMs: 60_000));
        // After the wait: what is still gone is confirmed; the ones that came back are not deleted.
        var stillGone = large.Take(1200).ToList();
        var confirmed = guard.Filter(4, 2000, stillGone, nowMs: MassDeleteGuard.DelayMs + 1);
        Assert.Equal(1200, confirmed.Count);
        Assert.Equal(0, guard.Postponed);
    }

    [Fact]
    public void Deletes_that_shrink_below_the_threshold_go_ahead_and_the_wait_ends()
    {
        var guard = new MassDeleteGuard();
        var large = Enumerable.Range(0, 1500).Select(i => ((ulong)i, i)).ToList();
        Assert.Empty(guard.Filter(1, 2000, large, nowMs: 0));
        var fewer = large.Take(10).ToList();
        Assert.Equal(10, guard.Filter(1, 2000, fewer, nowMs: 1000).Count);
        Assert.Equal(0, guard.Postponed);
    }
}

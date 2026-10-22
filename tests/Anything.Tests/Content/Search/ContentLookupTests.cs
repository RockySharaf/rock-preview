using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Search;
using Anything.Core.Content.Store;
using Anything.Core.Search;

namespace Anything.Tests.Content.Search;

/// <summary>Lookups shared across searches (A8): the plain-word budget, joining, the lease, the cache per segment.</summary>
[Collection(ContentSearchSerial.Name)]
public class ContentLookupTests
{
    static readonly TimeSpan Long = TimeSpan.FromSeconds(20);
    const string Named = @"C:\Docs\budget.txt", Inside = @"C:\Docs\minutes.txt", Second = @"C:\Docs\plan.txt", Third = @"C:\Docs\later.txt";

    /// <summary>A fixture for one test: fakes here; the integration tests run the same tests on a real ContentStore.</summary>
    private protected virtual ContentFixture NewFixture(DocSetCache? cache = null) => new(cache);

    ContentFixture Fixture(DocSetCache? cache = null)
    {
        var f = NewFixture(cache);
        f.Doc(Named, "nothing to see");
        f.Doc(Inside, "the budget for next year");
        f.NextSegment();
        f.Doc(Second, "budget plan draft");
        return f;
    }

    [Fact]
    public void Past_the_budget_results_are_names_only_and_pending_then_the_cache_answers()
    {
        var f = Fixture();
        using var gate = new ManualResetEventSlim(false);
        f.Publish();
        f.View!.FakeSegments[0].OnTerms = () => gate.Wait(Long);
        f.Engine.ContentBudget = TimeSpan.FromMilliseconds(50);

        var first = f.Run("budget", ContentFixture.Plain);
        Assert.True(first.ContentPending);
        Assert.Contains(f.Tree.Id(Named), first.Ids);
        Assert.DoesNotContain(f.Tree.Id(Inside), first.Ids); // segment 10 isn't resolved yet

        gate.Set();
        Assert.True(f.Source.WaitCompleted(Long), "the lookup never finished");
        int scans = f.View.FakeSegments.Sum(s => s.TermScans);

        var second = f.Run("budget", ContentFixture.Plain);
        Assert.False(second.ContentPending);
        Assert.Equal([Named, Inside, Second], second.Ids.Select(f.Index.GetFullPath));
        Assert.Equal(1, second.NameMatches);
        Assert.Equal(scans, f.View.FakeSegments.Sum(s => s.TermScans)); // answered from the cache
    }

    [Fact]
    public void A_pending_word_compiles_from_the_segments_resolved_so_far()
    {
        var f = Fixture();
        using var gate = new ManualResetEventSlim(false);
        f.Publish();
        f.View!.FakeSegments[1].OnTerms = () => gate.Wait(Long);
        f.Engine.ContentBudget = TimeSpan.FromMilliseconds(50);
        var r = f.Run("budget", ContentFixture.Plain);
        gate.Set();
        Assert.True(r.ContentPending);
        Assert.Equal([Named, Inside], r.Ids.Select(f.Index.GetFullPath)); // segment 10 answered, 20 still looking
        Assert.Equal(1, r.NameMatches);
        Assert.True(f.Source.WaitCompleted(Long));
    }

    [Fact]
    public void The_lookup_holds_its_own_lease_after_the_search_returns()
    {
        var f = Fixture();
        using var gate = new ManualResetEventSlim(false);
        var store = f.Publish();
        store.FakeSegments[0].OnTerms = () => gate.Wait(Long);
        f.Engine.ContentBudget = TimeSpan.FromMilliseconds(30);

        Assert.True(f.Run("budget", ContentFixture.Plain).ContentPending);
        f.Source.Publish(null); // the index publishes something else and lets its reference go
        Assert.False(store.Released, "the view was released under a running lookup");

        gate.Set();
        Assert.True(f.Source.WaitCompleted(Long));
        Assert.True(SpinWait.SpinUntil(() => store.Released, Long), "the lookup never released its lease");
    }

    [Fact]
    public void Explicit_terms_have_no_budget()
    {
        var f = Fixture();
        f.Publish();
        f.View!.FakeSegments[0].OnTerms = () => Thread.Sleep(150);
        f.Engine.ContentBudget = TimeSpan.FromMilliseconds(10);
        var r = f.Run("content:budget");
        Assert.False(r.ContentPending);
        Assert.Equal([Inside, Second], r.Ids.Select(f.Index.GetFullPath));
    }

    [Fact]
    public void Searches_that_refresh_faster_than_a_slow_lookup_join_it_instead_of_starting_again()
    {
        var f = Fixture();
        f.Publish();
        int slowScans = 0;
        f.View!.FakeSegments[0].OnTerms = () =>
        {
            Interlocked.Increment(ref slowScans);
            Thread.Sleep(500);
        };
        f.Engine.ContentBudget = TimeSpan.FromMilliseconds(100);

        // The window searches again every 200 ms (each search cancels the one before), as it does while the index changes.
        SearchResults? done = null;
        CancellationTokenSource? previous = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < Long)
        {
            previous?.Cancel();
            var cts = new CancellationTokenSource();
            previous = cts;
            var r = f.Run("budget", ContentFixture.Plain, ct: cts.Token);
            if (!r.ContentPending) { done = r; break; }
            Thread.Sleep(200);
        }
        Assert.NotNull(done);
        Assert.Equal([Named, Inside, Second], done!.Ids.Select(f.Index.GetFullPath));
        Assert.Equal(1, Volatile.Read(ref slowScans)); // one lookup, joined by every refresh
    }

    [Fact]
    public void A_commit_resolves_only_the_new_segment()
    {
        var f = Fixture();
        var first = f.Publish();
        Assert.Equal([Inside, Second], f.Find("content:budget"));
        int[] scans = [.. first.FakeSegments.Select(s => s.TermScans)];

        // A new commit: the same two segments plus a third.
        f.NextSegment();
        f.Doc(Third, "budget again");
        var second = f.Publish(make: (id, docs) => first.FakeSegments.FirstOrDefault(s => s.Id == id) ?? new FakeSegment(id, docs));
        Assert.Equal([Third, Inside, Second], f.Find("content:budget"));
        Assert.Equal(scans, second.FakeSegments.Take(2).Select(s => s.TermScans));
        Assert.Equal(1, second.FakeSegments[2].TermScans);
    }

    [Fact]
    public void A_shared_cache_serves_every_engine()
    {
        var cache = new DocSetCache();
        var f = Fixture(cache);
        f.Publish();
        Assert.Equal([Inside, Second], f.Find("content:budget"));
        int scans = f.View!.FakeSegments.Sum(s => s.TermScans);
        var other = new SearchEngine(); // another tab or window
        var r = other.Search(f.Index, "content:budget", new SearchOptions(), SortKey.Name, false, default);
        Assert.Equal(2, r.Ids.Length);
        Assert.Equal(scans, f.View.FakeSegments.Sum(s => s.TermScans));
    }

    [Fact]
    public void A_lookup_nobody_asks_for_is_cancelled_and_releases_its_lease()
    {
        var cache = new DocSetCache { UnaskedLimit = TimeSpan.FromMilliseconds(150), SweepEvery = TimeSpan.FromMilliseconds(50) };
        var f = NewFixture(cache);
        for (int i = 0; i < 400; i++) f.Doc($@"C:\Data\minutes{i:D3}.txt", "the budget for next year");
        var store = f.Publish();
        int reads = 0;
        // A long confirmation (a phrase over 400 candidates, 10 ms each) that nobody waits for any more.
        store.FakeSegments[0].OnRead = _ => { Interlocked.Increment(ref reads); Thread.Sleep(10); };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Assert.ThrowsAny<OperationCanceledException>(() => f.Run("content:\"the budget\"", ct: cts.Token));
        f.Source.Publish(null);
        Assert.True(SpinWait.SpinUntil(() => cache.LookupCount == 0, Long), "the lookup was never cancelled");
        Assert.True(SpinWait.SpinUntil(() => store.Released, Long), "the cancelled lookup kept its lease");
        Assert.True(Volatile.Read(ref reads) < 400, "the lookup ran to its end");
        Thread.Sleep(300);
        // Announced once when it went unasked (a window could still have wanted it), then cancelled: never again.
        Assert.Equal(1, f.Source.Completed);
    }

    const int SlowCount = 12;

    /// <summary>Twelve segments, each 300 ms to resolve; the plain word misses its budget and goes to the background lane.</summary>
    (ContentFixture Fixture, FakeStoreView Store) SlowSegments(DocSetCache cache)
    {
        var f = NewFixture(cache);
        for (int s = 0; s < SlowCount; s++)
        {
            if (s > 0) f.NextSegment();
            f.Doc($@"C:\Slow\doc{s}.txt", "the budget for next year");
        }
        var store = f.Publish();
        foreach (var seg in store.FakeSegments) seg.OnTerms = () => Thread.Sleep(300);
        f.Engine.ContentBudget = TimeSpan.FromMilliseconds(30);
        return (f, store);
    }

    [Fact]
    public void A_given_up_lookup_nobody_asks_for_again_is_announced_exactly_once()
    {
        var cache = new DocSetCache { UnaskedLimit = TimeSpan.FromMilliseconds(200), SweepEvery = TimeSpan.FromMilliseconds(40) };
        var (f, store) = SlowSegments(cache);
        Assert.True(f.Run("budget", ContentFixture.Plain).ContentPending);
        // The window showing that result is told once (it searches again if it still shows the word) ...
        Assert.True(f.Source.WaitCompleted(Long), "a lookup dropped as unasked was never announced");
        // ... and nobody asking again, the lookup is cancelled, with nothing more to say.
        Assert.True(SpinWait.SpinUntil(() => cache.LookupCount == 0, Long), "the lookup was never cancelled");
        Thread.Sleep(500);
        Assert.Equal(1, f.Source.Completed);
        Assert.True(store.FakeSegments.Sum(s => s.TermScans) < SlowCount, "the lookup ran to its end");
    }

    [Fact]
    public void A_window_that_searches_again_after_the_announcement_keeps_the_lookup_and_its_progress()
    {
        var cache = new DocSetCache { UnaskedLimit = TimeSpan.FromMilliseconds(200), SweepEvery = TimeSpan.FromMilliseconds(40) };
        var (f, store) = SlowSegments(cache);
        var r = f.Run("budget", ContentFixture.Plain);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // The window searches again whenever it is told (ContentChanged), until nothing is missing.
        while (r.ContentPending && clock.Elapsed < Long)
        {
            Assert.True(f.Source.WaitCompleted(Long), "the window was never told to search again");
            r = f.Run("budget", ContentFixture.Plain);
        }
        Assert.False(r.ContentPending);
        Assert.Equal(SlowCount, r.Ids.Length);
        // Every segment resolved once: nothing was dropped and redone between the announcements.
        Assert.All(store.FakeSegments, s => Assert.Equal(1, s.TermScans));
    }

    [Fact]
    public void A_search_after_the_announcement_keeps_the_lookup_even_when_the_next_sweep_comes_late()
    {
        // No timed sweeps (an hour apart): the test sweeps by hand, each time past the limit, as a busy thread pool would.
        var cache = new DocSetCache { UnaskedLimit = TimeSpan.FromMilliseconds(100), SweepEvery = TimeSpan.FromHours(1) };
        var (f, _) = SlowSegments(cache);
        Assert.True(f.Run("budget", ContentFixture.Plain).ContentPending);
        Thread.Sleep(150);
        cache.Sweep();
        Assert.Equal(1, f.Source.Completed); // nobody asked within the limit: announced
        Assert.True(f.Run("budget", ContentFixture.Plain).ContentPending); // the window searches again and joins it
        Thread.Sleep(150);
        cache.Sweep();
        Assert.Equal(1, cache.LookupCount); // asked since the announcement: not cancelled ...
        Assert.Equal(2, f.Source.Completed); // ... but announced again, nobody having asked since
        cache.Clear();
    }

    [Fact]
    public void A_search_doing_an_item_itself_for_longer_than_the_limit_still_asks_for_the_lookup()
    {
        // No timed sweeps: the item sweeps by hand, past the limit, while the waiting search works on it itself.
        var cache = new DocSetCache { UnaskedLimit = TimeSpan.FromMilliseconds(100), SweepEvery = TimeSpan.FromHours(1) };
        var f = NewFixture(cache);
        f.Doc(Inside, "the budget for next year");
        var store = f.Publish();
        store.FakeSegments[0].OnTerms = () =>
        {
            Thread.Sleep(150);
            cache.Sweep();
        };
        var plan = ContentPlan.Build(QueryParser.Parse("budget", ContentFixture.Plain), ContentFixture.Plain, f.Source.Mode, ContentFixture.Now);
        var term = plan.Terms.Values.Single(t => t.Plain);
        using var view = f.Source.AcquireView()!;
        uint segment = view.Store.Segments[0].Id;
        var lookup = cache.Join(term.Key, term, f.Source, view, [0]);
        // No helpers: the search takes the item itself.
        Assert.True(lookup.WaitFor([segment], long.MaxValue, CancellationToken.None));
        Assert.True(cache.TryGet(term.Key, segment, out _), "the lookup was cancelled while its search worked on it");
    }

    [Fact]
    public void A_plain_word_waiting_behind_a_long_explicit_term_is_still_asked_for()
    {
        // No timed sweeps: the explicit term's first item sweeps by hand, past the limit, while the plain word waits.
        var cache = new DocSetCache { UnaskedLimit = TimeSpan.FromMilliseconds(100), SweepEvery = TimeSpan.FromHours(1) };
        var f = Fixture(cache);
        var store = f.Publish();
        int items = 0;
        foreach (var seg in store.FakeSegments)
            seg.OnTerms = () =>
            {
                if (Interlocked.Increment(ref items) != 1) return;
                Thread.Sleep(150);
                cache.Sweep();
            };
        f.Engine.ContentBudget = TimeSpan.Zero; // the plain word's helpers take nothing: it only waits for its turn
        Assert.True(f.Run("plan content:budget", ContentFixture.Plain).ContentPending);
        // Handed to the background lane, it finishes and says so (cancelled unannounced, it never would).
        Assert.True(f.Source.WaitCompleted(Long), "the plain word's lookup was cancelled while its search worked on another term");
        var r = f.Run("plan content:budget", ContentFixture.Plain);
        Assert.False(r.ContentPending);
        Assert.Equal([Second], r.Ids.Select(f.Index.GetFullPath));
    }

    [Fact]
    public void A_search_doing_a_confirmation_itself_stops_within_one_candidate_and_hands_back_the_rest()
    {
        var cache = new DocSetCache();
        var f = NewFixture(cache);
        for (int i = 0; i < 300; i++) f.Doc($@"C:\Data\notes{i:D3}.txt", i % 3 == 0 ? "the budget is fine" : "budget the plan");
        var store = f.Publish();
        int reads = 0;
        store.FakeSegments[0].OnRead = _ => { Interlocked.Increment(ref reads); Thread.Sleep(10); };
        var options = new SearchOptions();
        var term = ContentPlan.Build(QueryParser.Parse("content:\"the budget\"", options), options, ContentMode.Documents, ContentFixture.Now)
            .Terms.Values.Single();
        using var view = f.Source.AcquireView()!;
        var lookup = cache.Join(term.Key, term, f.Source, view, [0]);
        uint segment = store.FakeSegments[0].Id;

        // No helper: the search thread itself works (as when the thread pool is busy), on 300 candidates of 10 ms.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => lookup.WaitFor([segment], long.MaxValue, cts.Token));
        Assert.True(clock.ElapsedMilliseconds < 600, $"stopped {clock.ElapsedMilliseconds} ms after starting (cancelled at 150 ms)");
        Assert.True(Volatile.Read(ref reads) < 60, $"{reads} candidates read");
        Assert.True(lookup.HasQueued, "the candidates it didn't reach were thrown away");

        // The background lane finishes the rest: every candidate is read exactly once, and the answer is complete.
        lookup.GiveUp();
        Assert.True(f.Source.WaitCompleted(Long));
        Assert.Equal(300, Volatile.Read(ref reads));
        Assert.True(cache.TryGet(term.Key, segment, out var set));
        Assert.Equal(100, set.Count);
    }

    [Fact]
    public void A_search_waiting_on_a_slow_item_keeps_its_lookup_alive()
    {
        // One item takes longer than the unasked limit: the waiting search counts as asking, so the sweep leaves it alone.
        var cache = new DocSetCache { UnaskedLimit = TimeSpan.FromMilliseconds(100), SweepEvery = TimeSpan.FromMilliseconds(20) };
        var f = Fixture(cache);
        f.Publish();
        f.View!.FakeSegments[0].OnTerms = () => Thread.Sleep(400);
        var r = f.Run("content:budget");
        Assert.False(r.ContentPending);
        Assert.Equal([Inside, Second], r.Ids.Select(f.Index.GetFullPath));
    }

    [Fact]
    public void Idle_sets_are_released()
    {
        var cache = new DocSetCache { IdleRelease = TimeSpan.FromMilliseconds(100), SweepEvery = TimeSpan.FromMilliseconds(50) };
        var f = Fixture(cache);
        f.Publish();
        f.Find("content:budget");
        Assert.True(cache.TermCount > 0);
        Assert.True(SpinWait.SpinUntil(() => cache.TermCount == 0, Long));
    }

    [Fact]
    public void The_cache_keeps_at_most_its_capacity_of_terms()
    {
        var cache = new DocSetCache { Capacity = 3 };
        for (int i = 0; i < 10; i++) cache.Put("t" + i, 1, DocSet.Empty);
        Assert.Equal(3, cache.TermCount);
        Assert.True(cache.TryGet("t9", 1, out _));
        Assert.False(cache.TryGet("t0", 1, out _));
        Assert.False(cache.TryGet("t9", 2, out _)); // per segment
    }

    [Fact]
    public void Concurrent_searches_agree_while_lookups_run_and_finish()
    {
        var f = NewFixture();
        var rnd = new Random(7);
        string[] words = ["alpha", "beta", "gamma", "delta", "omega"];
        var expected = new Dictionary<string, HashSet<string>>();
        foreach (var w in words) expected[w] = [];
        for (int i = 0; i < 300; i++)
        {
            if (i % 100 == 0 && i > 0) f.NextSegment();
            string path = $@"C:\Data\file{i:D3}.txt";
            var chosen = words.Where(_ => rnd.Next(3) == 0).ToArray();
            f.Doc(path, string.Join(" filler ", chosen.Append("text")));
            foreach (var w in chosen) expected[w].Add(path);
        }
        var store = f.Publish();
        foreach (var s in store.FakeSegments) s.OnTerms = () => Thread.Sleep(Random.Shared.Next(0, 3) == 0 ? 20 : 0);
        f.Engine.ContentBudget = TimeSpan.FromMilliseconds(5);

        Parallel.For(0, 40, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            string w = words[i % words.Length];
            var r = f.Run("content:" + w);
            Assert.Null(r.Error);
            Assert.False(r.ContentPending); // explicit: always complete
            Assert.True(expected[w].SetEquals(r.Ids.Select(f.Index.GetFullPath)), w);
            var p = f.Run(w, ContentFixture.Plain);
            Assert.True(p.Ids.Select(f.Index.GetFullPath).ToHashSet().IsSubsetOf(expected[w]), w); // pending results are a subset
        });
    }
}

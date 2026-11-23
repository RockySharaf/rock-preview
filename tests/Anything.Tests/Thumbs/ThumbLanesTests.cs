using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

public sealed class ThumbLanesTests
{
    [Fact]
    public void A_job_that_doesnt_wait_for_the_gate_or_finds_it_free_is_taken()
    {
        Assert.Equal(GateChoice.Take, ThumbLanes.ForGateWaiter(needsGate: false, gateHeld: true, idle: false, shellTried: false, heldSince: 1, now: 100_000));
        Assert.Equal(GateChoice.Take, ThumbLanes.ForGateWaiter(needsGate: true, gateHeld: false, idle: false, shellTried: false, heldSince: 1, now: 100_000));
        Assert.Equal(GateChoice.Take, ThumbLanes.ForGateWaiter(needsGate: true, gateHeld: false, idle: true, shellTried: true, heldSince: 0, now: 100_000));
    }

    [Fact]
    public void A_gate_release_wakes_a_waiter_before_its_retry_delay_ends()
    {
        long since = 1_000, now = since + ThumbLanes.GateStuckMs, retry = now + ThumbLanes.GateRetryMs;
        Assert.Equal(GateChoice.Wait, ThumbLanes.ForGateWaiter(true, true, false, false, since, now, retry));
        Assert.Equal(GateChoice.Take, ThumbLanes.ForGateWaiter(true, false, false, false, since, now, retry));
        Assert.Equal(GateChoice.ToShell, ThumbLanes.ForGateWaiter(true, true, false, false, since, retry, retry));
    }

    [Theory]
    [InlineData(ThumbRoute.Picture, false, true, false)]
    [InlineData(ThumbRoute.OwnExtractor, false, false, true)]
    [InlineData(ThumbRoute.OwnExtractor, true, false, false)]
    [InlineData(ThumbRoute.Shell, false, false, false)]
    public void A_shell_miss_retries_only_a_decoder_that_can_still_run(ThumbRoute route, bool decodeTried, bool gateToShell, bool retry)
        => Assert.Equal(retry, ThumbLanes.DecodeAfterShellMiss(route, decodeTried, gateToShell));

    [Theory]
    // Windows' extractor was asked and found nothing that may pass: remembered for this run.
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    // Only Windows' cache was asked (a folder, a shortcut, a PDF not on this PC) and missed: not remembered, a later look
    // asks the cache again.
    [InlineData(true, false, false)]
    // A PDF whose own extractor also found nothing: remembered.
    [InlineData(true, true, true)]
    public void A_miss_in_Windows_cache_alone_is_not_remembered_as_a_failure(bool cacheOnly, bool decodeTried, bool remembered)
        => Assert.Equal(remembered, ThumbLanes.RemembersFailure(cacheOnly, decodeTried));

    [Fact]
    public void A_waiting_job_stays_queued_while_the_gate_is_held_for_less_than_the_limit()
    {
        long since = 1_000;
        Assert.Equal(GateChoice.Wait, ThumbLanes.ForGateWaiter(true, true, false, false, since, since));
        Assert.Equal(GateChoice.Wait, ThumbLanes.ForGateWaiter(true, true, false, false, since, since + ThumbLanes.GateStuckMs - 1));
        // Not known to be held (the watch on the gate was not set up yet): never sent away.
        Assert.Equal(GateChoice.Wait, ThumbLanes.ForGateWaiter(true, true, false, false, 0, long.MaxValue / 2));
    }

    [Fact]
    public void A_gate_held_past_the_limit_sends_wanted_jobs_to_windows_once_and_idle_work_never()
    {
        long since = 5_000, late = since + ThumbLanes.GateStuckMs;
        Assert.Equal(GateChoice.ToShell, ThumbLanes.ForGateWaiter(true, true, idle: false, shellTried: false, since, late));
        Assert.Equal(GateChoice.Wait, ThumbLanes.ForGateWaiter(true, true, idle: true, shellTried: false, since, late));
        // A job that asked Windows before reaching the gate cannot use it as a fallback again.
        Assert.Equal(GateChoice.Wait, ThumbLanes.ForGateWaiter(true, true, idle: false, shellTried: true, since, late));
    }

    [Fact]
    public void The_watchdog_gives_up_only_the_run_it_noted()
    {
        object a = new(), b = new();
        Assert.True(ThumbLanes.StillSameRun(a, 10, a, 10));
        // The noted job ended and the worker took another one.
        Assert.False(ThumbLanes.StillSameRun(a, 10, b, 20));
        // The noted job ended and the worker is between jobs.
        Assert.False(ThumbLanes.StillSameRun(a, 10, null, 10));
        // The same job came back to the same worker (a later stage): a new run, started later.
        Assert.False(ThumbLanes.StillSameRun(a, 10, a, 30));
        Assert.False(ThumbLanes.StillSameRun(null, 10, null, 10));
    }

    [Fact]
    public void A_lane_dies_only_when_its_last_thread_goes_without_a_replacement()
    {
        Assert.False(ThumbLanes.LaneDies(replaced: true, threadsLeftInLane: 0));
        Assert.False(ThumbLanes.LaneDies(replaced: false, threadsLeftInLane: 1));
        Assert.True(ThumbLanes.LaneDies(replaced: false, threadsLeftInLane: 0));
    }

    [Fact]
    public void The_budget_hands_out_exactly_its_threads_also_under_parallel_takes()
    {
        var budget = new AbandonBudget(ThumbRules.MaxAbandonedThreads);
        int taken = 0;
        Parallel.For(0, 200, _ => { if (budget.TryTake()) Interlocked.Increment(ref taken); });
        Assert.Equal(ThumbRules.MaxAbandonedThreads, taken);
        Assert.False(budget.TryTake());
    }
}

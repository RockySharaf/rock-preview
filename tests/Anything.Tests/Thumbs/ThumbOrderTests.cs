using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

public sealed class ThumbOrderTests
{
    [Fact]
    public void A_check_never_made_is_due_and_a_recent_one_is_not()
    {
        // "Never" is long.MinValue: subtracting it from the clock would overflow and read as "just checked".
        Assert.True(ThumbRules.CheckDue(Environment.TickCount64, long.MinValue, ThumbRules.IdleCheckMs));
        Assert.True(ThumbRules.CheckDue(10_000, long.MinValue, 2000));
        Assert.False(ThumbRules.CheckDue(10_000, 9_000, 2000));
        Assert.True(ThumbRules.CheckDue(10_000, 8_000, 2000));
    }

    [Fact]
    public void An_idle_job_is_finished_as_idle_only_while_nobody_asks_for_it_on_screen()
    {
        var q = new ThumbQueue();
        var key = ThumbKey.Of(@"D:\a.jpg", new ThumbStamp(1, 1), 160);
        Assert.Equal(1, q.SubmitIdle(0, [new ThumbRequest(key.Path, key.Stamp, 160, ThumbPriority.Idle)]));
        Assert.True(q.TryTake(out var job));
        Assert.True(q.CompleteIfIdle(job));
        Assert.True(q.IsIdle);

        Assert.Equal(1, q.SubmitIdle(0, [new ThumbRequest(key.Path, key.Stamp, 160, ThumbPriority.Idle)]));
        Assert.True(q.TryTake(out job));
        // A viewport asks for it while a worker has it: it is wanted on screen now, and must be answered as such.
        q.SubmitViewport(0, [new ThumbRequest(key.Path, key.Stamp, 200, ThumbPriority.Visible)]);
        Assert.False(q.CompleteIfIdle(job));
        Assert.True(q.IsRunning(job));
        Assert.Equal([160, 200], q.Complete(job));
        Assert.True(q.IsIdle);
    }

    static int[] Range(int from, int to) => [.. Enumerable.Range(from, to - from + 1)];

    static int[] Indices(IEnumerable<PlannedItem> plan, ThumbPriority priority) => [.. plan.Where(p => p.Priority == priority).Select(p => p.Index)];

    [Fact]
    public void Visible_cells_come_first_in_reading_order_then_ahead_then_behind()
    {
        // 4 columns, rows 5 and 6 on screen, scrolling down.
        var plan = ThumbOrder.Viewport(100, 4, 5, 2, ScrollDirection.Down);
        Assert.Equal(Range(20, 27), Indices(plan, ThumbPriority.Visible));
        // Two screens (four rows) below, nearest row first.
        Assert.Equal(Range(28, 43), Indices(plan, ThumbPriority.Ahead));
        // One screen (two rows) above, nearest row first, each row left to right.
        Assert.Equal([.. Range(16, 19), .. Range(12, 15)], Indices(plan, ThumbPriority.Behind));
        // And in that order overall.
        Assert.Equal(plan.OrderBy(p => p.Priority).Select(p => p.Priority), plan.Select(p => p.Priority));
    }

    [Fact]
    public void Scrolling_up_looks_ahead_above()
    {
        var plan = ThumbOrder.Viewport(100, 4, 5, 2, ScrollDirection.Up);
        Assert.Equal(Range(20, 27), Indices(plan, ThumbPriority.Visible));
        Assert.Equal([.. Range(16, 19), .. Range(12, 15), .. Range(8, 11), .. Range(4, 7)], Indices(plan, ThumbPriority.Ahead));
        Assert.Equal([.. Range(28, 31), .. Range(32, 35)], Indices(plan, ThumbPriority.Behind));
    }

    [Fact]
    public void Not_scrolled_yet_looks_ahead_down()
    {
        Assert.Equal(ThumbOrder.Viewport(100, 4, 5, 2, ScrollDirection.Down), ThumbOrder.Viewport(100, 4, 5, 2, ScrollDirection.None));
    }

    [Fact]
    public void The_plan_stays_inside_the_list_and_names_nothing_twice()
    {
        // The last row is short; nothing exists past it, nor above the first.
        var end = ThumbOrder.Viewport(30, 4, 6, 3, ScrollDirection.Down);
        Assert.Equal(Range(24, 29), Indices(end, ThumbPriority.Visible));
        Assert.Empty(Indices(end, ThumbPriority.Ahead));
        Assert.Equal([.. Range(20, 23), .. Range(16, 19), .. Range(12, 15)], Indices(end, ThumbPriority.Behind));
        var start = ThumbOrder.Viewport(30, 4, 0, 2, ScrollDirection.Up);
        Assert.Empty(Indices(start, ThumbPriority.Ahead));
        foreach (var plan in new[] { end, start, ThumbOrder.Viewport(5, 10, 0, 4, ScrollDirection.Down) })
        {
            Assert.All(plan, p => Assert.InRange(p.Index, 0, 29));
            Assert.Equal(plan.Count, plan.Select(p => p.Index).Distinct().Count());
        }
        Assert.Empty(ThumbOrder.Viewport(0, 4, 0, 2, ScrollDirection.Down));
    }

    [Fact]
    public void Idle_work_starts_past_the_plan()
    {
        var plan = ThumbOrder.Viewport(100, 4, 5, 2, ScrollDirection.Down);
        Assert.Equal(44, ThumbOrder.IdleStart(plan, 100));
        Assert.Equal(0, ThumbOrder.IdleStart(ThumbOrder.Viewport(30, 4, 6, 3, ScrollDirection.Down), 30));
        Assert.Equal(0, ThumbOrder.IdleStart([], 30));
    }

    [Fact]
    public void Idle_chunks_wrap_never_repeat_and_skip_what_is_not_wanted()
    {
        var idle = new IdlePlanner(chunkSize: 4, capPerList: 100);
        Assert.Equal([8, 9, 0, 1], idle.Next(10, 8, _ => true));
        // Items 2 and 3 are already pictured.
        Assert.Equal([4, 5, 6, 7], idle.Next(10, 8, i => i is not (2 or 3)));
        Assert.Empty(idle.Next(10, 8, i => i is not (2 or 3)));
        Assert.Equal([2, 3], idle.Next(10, 0, _ => true));
        Assert.Equal(10, idle.Given);
        idle.Reset();
        Assert.Equal([0, 1, 2, 3], idle.Next(10, 0, _ => true));
    }

    [Fact]
    public void Idle_items_not_made_are_given_back_and_made_ones_are_not_handed_out_again()
    {
        var idle = new IdlePlanner(chunkSize: 4, capPerList: 6);
        Assert.Equal([0, 1, 2, 3], idle.Next(10, 0, _ => true));
        // A new viewport dropped 2 and 3 before they were started; 0 and 1 were made.
        idle.Return([2, 3]);
        Assert.Equal(2, idle.Given);
        // The list changed again: the walk starts over, and only what wasn't made comes back.
        Assert.Equal([2, 3, 4, 5], idle.Next(10, 0, _ => true));
        Assert.True(idle.Exhausted);
        Assert.Empty(idle.Next(10, 0, _ => true));
    }

    [Fact]
    public void Idle_work_stops_at_the_cap_for_the_list()
    {
        var idle = new IdlePlanner();
        int total = 0, chunks = 0;
        for (List<int> chunk; (chunk = idle.Next(1_000_000, 500, _ => true)).Count > 0; chunks++)
        {
            Assert.True(chunk.Count <= IdlePlanner.DefaultChunk);
            total += chunk.Count;
        }
        Assert.Equal(IdlePlanner.DefaultCap, total);
        Assert.Equal(15, chunks);
        Assert.True(idle.Exhausted);
    }

    [Fact]
    public void Idle_walks_stay_bounded_on_a_huge_list_that_is_all_pictured()
    {
        var idle = new IdlePlanner();
        int asked = 0;
        Assert.Empty(idle.Next(10_000_000, 0, _ => { asked++; return false; }));
        Assert.Equal(2 * IdlePlanner.DefaultCap, asked);
        // Nothing was handed out, so the grid stops asking (the walk was cut short; a viewport moves its start).
        Assert.True(idle.Done);
    }

    [Fact]
    public void A_short_list_with_nothing_wanted_is_done_until_reset()
    {
        var idle = new IdlePlanner();
        Assert.False(idle.Done);
        Assert.Empty(idle.Next(10, 0, _ => false));
        Assert.True(idle.Done);
        Assert.False(idle.Exhausted);
        idle.Reset();
        Assert.False(idle.Done);
    }

    [Fact]
    public void A_list_shorter_than_the_cap_is_done_once_everything_outside_the_plan_is_handed_out()
    {
        // 1,200 items, 200 a chunk; items 0 to 99 are the viewport's plan.
        var idle = new IdlePlanner(chunkSize: 200);
        int handed = 0;
        for (List<int> chunk; (chunk = idle.Next(1200, 100, i => i >= 100)).Count > 0;)
        {
            handed += chunk.Count;
            Assert.False(idle.Done);
        }
        Assert.Equal(1100, handed);
        // The empty answer comes while the cap (3,000) is far off: Exhausted can't stop the idle timer here, Done does.
        Assert.False(idle.Exhausted);
        Assert.True(idle.Done);
    }

    [Fact]
    public void A_walk_cut_short_on_a_huge_list_that_finds_nothing_is_done_too()
    {
        // Cap 10, so a walk looks at 20 items at most; the 20 from the start are already pictured. The grid stops its
        // idle timer on Done, so an empty answer must say Done even though items past the walk are still wanted:
        // otherwise the same 20 would be walked twice a second for nothing. A viewport moves the start and resumes it.
        var idle = new IdlePlanner(chunkSize: 5, capPerList: 10);
        Assert.Empty(idle.Next(1_000_000, 0, i => i >= 20));
        Assert.True(idle.Done);
        Assert.Equal([20, 21, 22, 23, 24], idle.Next(1_000_000, 20, i => i >= 20));
        Assert.False(idle.Done);
    }

    [Fact]
    public void Reaching_the_cap_is_done()
    {
        var idle = new IdlePlanner(chunkSize: 4, capPerList: 4);
        Assert.Equal([0, 1, 2, 3], idle.Next(100, 0, _ => true));
        Assert.False(idle.Done);
        Assert.Empty(idle.Next(100, 0, _ => true));
        Assert.True(idle.Exhausted);
        Assert.True(idle.Done);
    }

    [Fact]
    public void Items_given_back_after_done_are_handed_out_again()
    {
        var idle = new IdlePlanner(chunkSize: 4);
        Assert.Equal([0, 1, 2, 3], idle.Next(6, 0, _ => true));
        Assert.Equal([4, 5], idle.Next(6, 0, _ => true));
        Assert.Empty(idle.Next(6, 0, _ => true));
        Assert.True(idle.Done);
        idle.Return([1, 4]);
        Assert.False(idle.Done);
        Assert.Equal([1, 4], idle.Next(6, 0, _ => true));
        Assert.False(idle.Done);
        Assert.Empty(idle.Next(6, 0, _ => true));
        Assert.True(idle.Done);
    }
}

public sealed class ThumbQueueTests
{
    [Fact]
    public void The_queue_says_when_idle_work_waits()
    {
        // The service asks the power policy only while idle work waits.
        var q = new ThumbQueue();
        Assert.False(q.HasIdleWaiting);
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible), Req(2, ThumbPriority.Behind)]);
        Assert.False(q.HasIdleWaiting);
        Assert.Equal(1, q.SubmitIdle(1, [Req(3, ThumbPriority.Idle)]));
        Assert.True(q.HasIdleWaiting);
        while (q.TryTake(out var job)) q.Complete(job);
        Assert.False(q.HasIdleWaiting);
    }

    static ThumbRequest Req(int n, ThumbPriority priority, int size = 160, long stamp = 1)
        => new($@"C:\Files\item {n}.jpg", new ThumbStamp(stamp, stamp), size, priority, n);

    static List<int> Drain(ThumbQueue queue, ThumbPriority worst = ThumbPriority.Idle)
    {
        var taken = new List<int>();
        while (queue.TryTake(worst, null, out var job))
        {
            taken.Add(job.Index);
            queue.Complete(job);
        }
        return taken;
    }

    [Fact]
    public void Jobs_come_out_by_priority_then_in_submitted_order()
    {
        var q = new ThumbQueue();
        q.SubmitViewport(1, [Req(5, ThumbPriority.Visible), Req(6, ThumbPriority.Visible), Req(9, ThumbPriority.Ahead), Req(1, ThumbPriority.Behind)]);
        Assert.Equal([5, 6, 9, 1], Drain(q));
        Assert.True(q.IsIdle);
    }

    [Fact]
    public void Ahead_comes_before_behind_whatever_order_they_were_given_in()
    {
        var q = new ThumbQueue();
        q.SubmitViewport(1, [Req(1, ThumbPriority.Behind), Req(2, ThumbPriority.Ahead), Req(3, ThumbPriority.Visible), Req(4, ThumbPriority.Ahead)]);
        Assert.Equal([3, 2, 4, 1], Drain(q));
    }

    [Fact]
    public void The_order_follows_the_scroll_direction_of_the_plan()
    {
        var q = new ThumbQueue();
        var plan = ThumbOrder.Viewport(100, 4, 5, 2, ScrollDirection.Up);
        q.SubmitViewport(1, [.. plan.Select(p => Req(p.Index, p.Priority))]);
        var order = Drain(q);
        Assert.Equal(plan.Select(p => p.Index), order);
        // The first look-ahead cell is the row just above the screen.
        Assert.Equal(16, order[8]);
    }

    [Fact]
    public void A_new_viewport_replaces_the_plan()
    {
        var q = new ThumbQueue();
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible), Req(2, ThumbPriority.Visible), Req(3, ThumbPriority.Ahead)]);
        // Scrolled: 3 is now on screen, 4 ahead; 1 and 2 went off and were not started.
        q.SubmitViewport(1, [Req(3, ThumbPriority.Visible), Req(4, ThumbPriority.Ahead), Req(2, ThumbPriority.Behind)]);
        Assert.Equal([3, 4, 2], Drain(q));
    }

    [Fact]
    public void An_idle_job_asked_for_on_screen_while_it_runs_becomes_wanted_on_screen()
    {
        var q = new ThumbQueue();
        q.SubmitViewport(1, []);
        q.SubmitIdle(1, [Req(1, ThumbPriority.Idle)]);
        Assert.True(q.TryTake(out var job));
        Assert.Equal(ThumbPriority.Idle, job.Priority);
        Assert.True(q.IsRunning(job));
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible)]);
        Assert.Equal(ThumbPriority.Visible, job.Priority);
        // Never made worse again.
        q.SubmitViewport(1, [Req(1, ThumbPriority.Behind)]);
        Assert.Equal(ThumbPriority.Visible, job.Priority);
        q.Complete(job);
        Assert.False(q.IsRunning(job));
    }

    [Fact]
    public void Started_work_is_never_dropped()
    {
        var q = new ThumbQueue();
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible), Req(2, ThumbPriority.Visible)]);
        Assert.True(q.TryTake(out var running));
        q.SubmitViewport(1, [Req(7, ThumbPriority.Visible)]);
        Assert.Equal(1, q.RunningCount);
        Assert.Equal(1, q.PendingCount);
        Assert.False(q.IsIdle);
        Assert.Equal([160], q.Complete(running));
        Assert.Equal([7], Drain(q));
    }

    [Fact]
    public void A_new_list_drops_everything_not_started()
    {
        var q = new ThumbQueue();
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible), Req(2, ThumbPriority.Ahead), Req(3, ThumbPriority.Behind)]);
        Assert.True(q.TryTake(out var running));
        Assert.Equal(1, q.SubmitIdle(1, [Req(50, ThumbPriority.Idle)]));
        q.SubmitViewport(2, [Req(10, ThumbPriority.Visible)]);
        Assert.Equal(2, q.ListNumber);
        Assert.Equal([10], q.Snapshot().Select(j => j.Index));
        Assert.True(q.Contains(running.Key));
        q.Complete(running);
        Assert.Equal([10], Drain(q));
    }

    [Fact]
    public void One_job_per_key()
    {
        var q = new ThumbQueue();
        // The same file asked for at two display sizes of one class, and once more at the same size.
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible, 160), Req(2, ThumbPriority.Visible), Req(1, ThumbPriority.Ahead, 200), Req(1, ThumbPriority.Behind, 160)]);
        Assert.Equal(2, q.PendingCount);
        Assert.True(q.TryTake(out var job));
        Assert.Equal(1, job.Index);
        // Asked for again while a worker has it: it joins the running job.
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible, 240), Req(2, ThumbPriority.Visible)]);
        q.SubmitIdle(1, [Req(1, ThumbPriority.Idle, 128)]);
        Assert.Equal(1, q.PendingCount);
        Assert.Equal([160, 200, 240, 128], q.Complete(job));
        // Another size class is another key, so another job.
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible, 160), Req(1, ThumbPriority.Visible, 400)]);
        Assert.Equal(2, q.PendingCount);
    }

    [Fact]
    public void Another_stamp_is_another_job()
    {
        var q = new ThumbQueue();
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible, stamp: 1), Req(1, ThumbPriority.Visible, stamp: 2)]);
        Assert.Equal(2, q.PendingCount);
    }

    [Fact]
    public void Idle_work_comes_last_and_only_for_the_current_list()
    {
        var q = new ThumbQueue();
        q.SubmitViewport(3, [Req(1, ThumbPriority.Visible), Req(2, ThumbPriority.Behind)]);
        Assert.Equal(0, q.SubmitIdle(2, [Req(90, ThumbPriority.Idle)]));
        Assert.Equal(2, q.SubmitIdle(3, [Req(91, ThumbPriority.Idle), Req(1, ThumbPriority.Idle), Req(92, ThumbPriority.Idle)]));
        // A lane that takes no idle work stops before it.
        Assert.Equal([1, 2], Drain(q, ThumbPriority.Behind));
        Assert.Equal([91, 92], Drain(q));
    }

    [Fact]
    public void A_new_viewport_drops_idle_work_not_started()
    {
        var q = new ThumbQueue();
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible)]);
        q.SubmitIdle(1, [Req(80, ThumbPriority.Idle), Req(81, ThumbPriority.Idle)]);
        q.SubmitViewport(1, [Req(2, ThumbPriority.Visible)]);
        Assert.Equal([2], Drain(q));
    }

    [Fact]
    public void Without_the_store_there_is_no_idle_step()
    {
        var q = new ThumbQueue(allowIdle: false);
        Assert.False(q.AllowIdle);
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible), Req(2, ThumbPriority.Ahead), Req(3, ThumbPriority.Behind)]);
        Assert.Equal(0, q.SubmitIdle(1, [Req(80, ThumbPriority.Idle), Req(81, ThumbPriority.Idle)]));
        Assert.Equal([1, 2, 3], Drain(q));
        Assert.True(q.IsIdle);
    }

    [Fact]
    public void Turning_idle_work_off_drops_what_has_not_started()
    {
        var q = new ThumbQueue();
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible)]);
        Assert.Equal(3, q.SubmitIdle(1, [Req(80, ThumbPriority.Idle), Req(81, ThumbPriority.Idle), Req(82, ThumbPriority.Idle)]));
        Assert.True(q.TryTake(ThumbPriority.Idle, j => j.Index == 80, out var started));
        q.AllowIdle = false;
        // The viewport's work stays; idle work not started goes; the started job finishes (its picture is kept).
        Assert.Equal([1], q.Snapshot().Select(j => j.Index));
        Assert.Equal(1, q.RunningCount);
        q.Complete(started);
        Assert.Equal(0, q.SubmitIdle(1, [Req(83, ThumbPriority.Idle)]));
        q.AllowIdle = true;
        Assert.Equal(1, q.SubmitIdle(1, [Req(83, ThumbPriority.Idle)]));
        Assert.Equal([1, 83], Drain(q));
    }

    [Fact]
    public void A_lane_filter_picks_the_best_job_it_accepts()
    {
        var q = new ThumbQueue();
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible), Req(2, ThumbPriority.Visible), Req(3, ThumbPriority.Ahead)]);
        Assert.True(q.TryTake(ThumbPriority.Idle, j => j.Index != 1, out var job));
        Assert.Equal(2, job.Index);
        Assert.True(job.Started);
        Assert.False(q.TryTake(ThumbPriority.Visible, j => j.Index == 3, out _));
    }

    [Fact]
    public void Changed_is_raised_when_work_arrives_and_finishes()
    {
        var q = new ThumbQueue();
        int changed = 0;
        q.Changed += () => changed++;
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible)]);
        Assert.True(q.TryTake(out var job));
        q.Complete(job);
        Assert.Equal(2, changed);
        Assert.True(q.IsIdle);
    }

    [Fact]
    public void Clear_drops_waiting_work()
    {
        var q = new ThumbQueue();
        q.SubmitViewport(1, [Req(1, ThumbPriority.Visible), Req(2, ThumbPriority.Visible)]);
        q.Clear();
        Assert.True(q.IsIdle);
        Assert.False(q.TryTake(out _));
    }

    [Fact]
    public void Workers_on_several_threads_take_each_job_once()
    {
        var q = new ThumbQueue();
        q.SubmitViewport(1, [.. Enumerable.Range(0, 2000).Select(i => Req(i, ThumbPriority.Ahead))]);
        var taken = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 6, _ =>
        {
            while (q.TryTake(out var job))
            {
                taken.Add(job.Index);
                q.Complete(job);
            }
        });
        Assert.Equal(2000, taken.Count);
        Assert.Equal(2000, taken.Distinct().Count());
        Assert.True(q.IsIdle);
    }
}

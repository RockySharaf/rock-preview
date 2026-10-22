using Anything.Core.Content;
using Anything.Core.Content.Indexing;

namespace Anything.Tests.Content.Indexing;

public class PriorityTests
{
    [Fact]
    public void Most_opened_first_then_newest_first()
    {
        using var h = new Harness();
        h.Folder(@"C:\Users\Me");
        long t = h.NextTime();
        h.File(@"C:\Users\Me\old.txt", "old", modified: t);
        h.File(@"C:\Users\Me\newer.txt", "newer", modified: t + 10_000_000);
        h.File(@"C:\Users\Me\newest.txt", "newest", modified: t + 20_000_000);
        h.File(@"C:\Users\Me\opened.txt", "opened", modified: t - 10_000_000);
        h.File(@"C:\Users\Me\recent.txt", "recent", modified: t - 20_000_000);
        h.Index.RunHistory.Increment(@"C:\Users\Me\opened.txt", t);
        h.Recent = [@"C:\Users\Me\recent.txt"];
        h.Start();
        h.Settle();
        var order = h.Files.ReadPaths.Select(Path.GetFileName).ToList();
        Assert.True(order.Take(2).Order().SequenceEqual(["opened.txt", "recent.txt"]), string.Join(", ", order));
        Assert.Equal(["newest.txt", "newer.txt", "old.txt"], order.Skip(2));
    }

    [Fact]
    public void The_queue_orders_levels_then_time_and_keeps_shares_apart()
    {
        var q = new WorkQueue();
        q.Enqueue(1, WorkQueue.Priority(WorkQueue.Newest, 100), null);
        q.Enqueue(2, WorkQueue.Priority(WorkQueue.Newest, 300), null);
        q.Enqueue(3, WorkQueue.Priority(WorkQueue.Upgrade, 900), null);
        q.Enqueue(4, WorkQueue.Priority(WorkQueue.Opened, 1), null);
        q.Enqueue(2, WorkQueue.Priority(WorkQueue.Newest, 999), null); // already queued: keeps its place
        q.Enqueue(5, WorkQueue.Priority(WorkQueue.Newest, 1), "unc:a");
        q.Enqueue(6, WorkQueue.Priority(WorkQueue.Newest, 2), "unc:b");
        q.Enqueue(7, WorkQueue.Priority(WorkQueue.Newest, 3), "unc:a");
        Assert.Equal(4, q.LocalCount);
        Assert.Equal(3, q.NetworkCount);
        var local = new List<int>();
        while (q.TryDequeueLocal(out int id, out _)) local.Add(id);
        Assert.Equal([4, 2, 1, 3], local);
        var net = new List<int>();
        while (q.TryDequeueNetwork(_ => true, out int id, out _, out _)) net.Add(id);
        Assert.Equal([7, 6, 5], net); // round-robin over shares, newest first in each
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void The_queue_follows_a_compact_and_skips_removed_ids()
    {
        var q = new WorkQueue();
        for (int i = 0; i < 10; i++) q.Enqueue(i, WorkQueue.Priority(WorkQueue.Newest, i), i % 2 == 0 ? null : "unc:s");
        q.Remove(4);
        var remap = Enumerable.Range(0, 10).Select(i => i == 3 ? -1 : i < 3 ? i : i - 1).ToArray();
        q.Remap(remap);
        Assert.Equal(8, q.Count);
        Assert.False(q.Contains(9));
        Assert.True(q.Contains(8)); // 9 → 8
        var all = new List<int>();
        while (q.TryDequeueLocal(out int id, out _)) all.Add(id);
        while (q.TryDequeueNetwork(_ => true, out int id, out _, out _)) all.Add(id);
        Assert.Equal([0, 1, 2, 4, 5, 6, 7, 8], all.Order());
    }

    [Fact]
    public void A_share_that_isnt_ready_waits_its_turn()
    {
        var q = new WorkQueue();
        q.Enqueue(1, WorkQueue.Priority(WorkQueue.Newest, 1), "unc:slow");
        q.Enqueue(2, WorkQueue.Priority(WorkQueue.Newest, 1), "unc:fast");
        Assert.True(q.TryDequeueNetwork(s => s == "unc:fast", out int id, out _, out string share));
        Assert.Equal((2, "unc:fast"), (id, share));
        Assert.False(q.TryDequeueNetwork(s => s == "unc:fast", out _, out _, out _));
        Assert.True(q.HasNetwork("unc:slow"));
    }
}

public class PauseTests
{
    static Harness Started(Action<Harness>? before = null)
    {
        var h = new Harness();
        h.Folder(@"C:\Users\Me");
        before?.Invoke(h);
        h.File(@"C:\Users\Me\a.txt", "alpha");
        h.Start();
        h.Settle();
        return h;
    }

    [Theory]
    [InlineData("battery")]
    [InlineData("saver")]
    [InlineData("fullscreen")]
    [InlineData("disk")]
    [InlineData("disk5")]
    public void Reading_pauses_on_battery_saver_full_screen_and_low_disk(string what)
    {
        using var h = Started(h => h.Power.State = what switch
        {
            "battery" => PowerState.Unlimited with { OnBattery = true },
            "saver" => PowerState.Unlimited with { BatterySaver = true },
            "fullscreen" => PowerState.Unlimited with { FullScreen = true },
            "disk" => PowerState.Unlimited with { FreeBytes = 1L << 30, TotalBytes = 1L << 40 },
            _ => PowerState.Unlimited with { FreeBytes = 3L << 30, TotalBytes = 100L << 30 },
        });
        Assert.Equal(0, h.Files.Reads);
        Assert.Equal(ContentPhase.Paused, h.Content.Status.Phase);
        Assert.NotNull(h.Content.Status.Reason);
        h.Power.State = PowerState.Unlimited;
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
        Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);
    }

    [Fact]
    public void Reading_on_battery_when_allowed_but_never_on_battery_saver()
    {
        var settings = Harness.DefaultSettings();
        settings.ReadOnBattery = true;
        using var h = new Harness(settings);
        h.Folder(@"C:\Users\Me");
        h.File(@"C:\Users\Me\a.txt", "alpha");
        h.Power.State = PowerState.Unlimited with { OnBattery = true };
        h.Start();
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
        h.Power.State = PowerState.Unlimited with { OnBattery = true, BatterySaver = true };
        h.File(@"C:\Users\Me\b.txt", "beta");
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
    }

    [Fact]
    public void Running_as_administrator_pauses_reading_but_renames_still_follow()
    {
        using var h = Started();
        h.Names.IsElevated = true;
        h.File(@"C:\Users\Me\b.txt", "beta");
        h.Rename(@"C:\Users\Me\a.txt", @"C:\Users\Me\renamed.txt");
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
        Assert.Equal("running as administrator", h.Content.Status.Reason);
        Assert.Equal("alpha", h.Doc(@"C:\Users\Me\renamed.txt")!.Body);
        h.Names.IsElevated = false;
        h.Settle();
        Assert.Equal(2, h.Files.Reads);
    }

    [Fact]
    public void Paused_by_the_user_until_resumed_or_for_an_hour()
    {
        using var h = Started();
        h.Content.Pause(null);
        h.File(@"C:\Users\Me\b.txt", "beta");
        h.Pass(3_600_000, step: 300_000);
        Assert.Equal(1, h.Files.Reads);
        Assert.Equal("paused by you", h.Content.Status.Reason);
        h.Content.Resume();
        h.Settle();
        Assert.Equal(2, h.Files.Reads);
        h.Content.Pause(TimeSpan.FromHours(1));
        h.Run();
        h.File(@"C:\Users\Me\c.txt", "gamma");
        h.Pass(1_800_000, step: 300_000);
        Assert.Equal(2, h.Files.Reads);
        h.Pass(1_900_000, step: 300_000);
        Assert.Equal(3, h.Files.Reads);
    }

    [Fact]
    public void The_power_rules_say_when_the_user_is_active_and_when_large_merges_may_run()
    {
        Assert.True(PowerRules.UserActive(PowerState.Unlimited with { Idle = TimeSpan.FromSeconds(30) }));
        Assert.False(PowerRules.UserActive(PowerState.Unlimited with { Idle = TimeSpan.FromMinutes(3) }));
        Assert.False(PowerRules.AllowLargeMerge(PowerState.Unlimited with { Idle = TimeSpan.FromMinutes(1) }));
        Assert.False(PowerRules.AllowLargeMerge(PowerState.Unlimited with { OnBattery = true }));
        Assert.True(PowerRules.AllowLargeMerge(PowerState.Unlimited));
        Assert.Null(PowerRules.ReadPause(PowerState.Unlimited, false, false));
        Assert.Equal("running as administrator", PowerRules.ReadPause(PowerState.Unlimited, false, true));
        Assert.Null(PowerRules.MergePause(PowerState.Unlimited with { OnBattery = true }, readOnBattery: true));
    }

    [Fact]
    public void The_real_power_policy_answers_without_throwing()
    {
        var s = SystemPowerPolicy.Instance.Now(Path.GetTempPath());
        Assert.True(s.Idle >= TimeSpan.Zero);
        Assert.True(s.FreeBytes > 0);
    }
}

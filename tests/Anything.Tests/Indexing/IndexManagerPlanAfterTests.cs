using System.Diagnostics;
using Anything.Core;
using Anything.Core.Indexing;
using static Anything.Tests.Indexing.DriveIndexingTestSupport;

namespace Anything.Tests.Indexing;

/// <summary>
/// <see cref="IndexManager.PlanAfter"/>: with the Store copy's helper starting (Windows asking), a start loads the
/// database, then plans its drives only once the answer is in, so they're read through the helper from the start rather
/// than scanned, then read again. <see cref="IndexManager.HasPlanned"/> tells the app whether a helper that answers needs
/// a new start. Drives are temp folders.
/// </summary>
public sealed class IndexManagerPlanAfterTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("anything-planafter-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    DriveEntry Usb()
    {
        string folder = Path.Combine(_dir, "usb");
        Write(Path.Combine(folder, "photo.jpg"));
        return FakeDrive(folder, DriveKind.Removable);
    }

    IndexSettings Settings() => new() { DatabasePath = Path.Combine(_dir, "test.db") };

    [Fact]
    public void A_start_plans_its_drives_only_once_the_helper_s_answer_is_in()
    {
        var usb = Usb();
        using var mgr = Manager(() => [usb]);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mgr.PlanAfter(answer.Task);
        mgr.Start(Settings());
        Assert.True(WaitFor(() => mgr.StatusText == "Starting instant indexing..."), $"not waiting: {mgr.State} {mgr.StatusText}");
        Thread.Sleep(300);
        Assert.False(mgr.HasPlanned);
        Assert.Empty(mgr.PlannedDrives);
        Assert.Equal(IndexState.Loading, mgr.State);

        answer.SetResult();
        WaitUntilSettled(mgr);
        Assert.True(mgr.HasPlanned);
        Assert.Equal([usb], mgr.PlannedDrives);
    }

    [Fact]
    public void An_answer_already_in_holds_nothing_up()
    {
        var usb = Usb();
        using var mgr = Manager(() => [usb]);
        mgr.PlanAfter(Task.CompletedTask);
        Assert.False(mgr.HasPlanned); // not started
        mgr.Start(Settings());
        WaitUntilSettled(mgr);
        Assert.True(mgr.HasPlanned);
        Assert.Equal([usb], mgr.PlannedDrives);
        // Started again (the helper answering later): not planned until the run gets there.
        mgr.Start(Settings());
        WaitUntilSettled(mgr);
        Assert.True(mgr.HasPlanned);
    }

    [Fact]
    public void Closing_while_it_waits_ends_at_once()
    {
        var usb = Usb();
        var mgr = Manager(() => [usb]);
        mgr.PlanAfter(new TaskCompletionSource().Task); // never answered
        mgr.Start(Settings());
        Assert.True(WaitFor(() => mgr.StatusText == "Starting instant indexing..."));
        var closing = Stopwatch.StartNew();
        mgr.Dispose();
        Assert.True(closing.Elapsed < TimeSpan.FromSeconds(10), $"closing took {closing.Elapsed}");
        Assert.False(mgr.HasPlanned);
    }
}

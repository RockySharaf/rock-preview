using System.Reflection;
using Anything.Core;
using Anything.Core.Indexing;
using static Anything.Tests.Indexing.DriveIndexingTestSupport;

namespace Anything.Tests.Indexing;

/// <summary>
/// <see cref="IndexManager.PlannedDrives"/> and <see cref="IndexManager.DrivesChanged"/>: the drives the index planned
/// with, told to the app when they change, so what it works out from drives matches the index. Drives are temp folders.
/// </summary>
public sealed class IndexManagerPlannedDrivesTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("anything-planned-").FullName;
    readonly object _gate = new();
    readonly List<DriveEntry> _drives = [];
    uint _mask;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    string Dir(string name)
    {
        string path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    IReadOnlyList<DriveEntry> Drives() { lock (_gate) return [.. _drives]; }

    void SetDrives(params DriveEntry[] drives)
    {
        lock (_gate)
        {
            _drives.Clear();
            _drives.AddRange(drives);
        }
    }

    /// <summary>A manager that sees <see cref="_drives"/> and <see cref="_mask"/>, and counts DrivesChanged.</summary>
    IndexManager NewManager(Action onChanged)
    {
        var mgr = new IndexManager { DriveSource = Drives, LetterSource = () => Volatile.Read(ref _mask), WatchDevices = false };
        mgr.DrivesChanged += onChanged;
        return mgr;
    }

    IndexSettings Settings() => new() { DatabasePath = Path.Combine(_dir, "test.db") };

    /// <summary>The drive tick on its own, as the timer runs it (no replan asked for).</summary>
    static void Tick(IndexManager mgr) =>
        typeof(IndexManager).GetMethod("OnDriveTick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(mgr, null);

    [Fact]
    public void Drives_the_index_planned_with_are_told_once_it_has_planned()
    {
        var usb = FakeDrive(Dir("usb"), DriveKind.Removable);
        SetDrives(usb);
        int changed = 0;
        using var mgr = NewManager(() => Interlocked.Increment(ref changed));
        Assert.Empty(mgr.PlannedDrives);

        mgr.Start(Settings());
        WaitUntilSettled(mgr);
        Assert.Equal([usb], mgr.PlannedDrives);
        Assert.Equal(1, Volatile.Read(ref changed));
        Assert.Equal([usb], ((IIndexManager)mgr).PlannedDrives);

        // The same drives looked at again: nothing to tell.
        for (int i = 0; i < 3; i++) mgr.CheckDrivesNow();
        Assert.Equal(1, Volatile.Read(ref changed));
    }

    [Fact]
    public void A_drive_plugged_in_or_removed_is_told_after_its_root_is_added_or_dropped()
    {
        using var mgr = NewManager(() => { });
        mgr.Start(Settings());
        WaitUntilSettled(mgr);
        Assert.Empty(mgr.PlannedDrives);

        string folder = Dir("usb");
        Write(Path.Combine(folder, "photo.jpg"));
        var usb = FakeDrive(folder, DriveKind.Removable);
        int changed = 0;
        int rootWhenTold = -1; // 1 when its root was there as it was told
        IReadOnlyList<DriveEntry>? drivesWhenTold = null;
        mgr.DrivesChanged += () =>
        {
            Volatile.Write(ref rootWhenTold, mgr.StateOfRoot(folder) != null ? 1 : 0);
            Volatile.Write(ref drivesWhenTold, mgr.PlannedDrives);
            Interlocked.Increment(ref changed);
        };

        // Plugged in.
        SetDrives(usb);
        Assert.True(WaitFor(() => { mgr.CheckDrivesNow(); return Volatile.Read(ref changed) == 1; }), "not told");
        Assert.Equal(1, Volatile.Read(ref rootWhenTold)); // its root is there by then
        Assert.Equal([usb], Volatile.Read(ref drivesWhenTold));

        // Removed.
        SetDrives();
        Assert.True(WaitFor(() => { mgr.CheckDrivesNow(); return Volatile.Read(ref changed) == 2; }), "not told");
        Assert.Equal(0, Volatile.Read(ref rootWhenTold)); // and dropped
        Assert.Empty(mgr.PlannedDrives);
    }

    [Fact]
    public void A_card_put_in_a_reader_is_told_by_the_periodic_look_and_a_new_letter_by_the_letter_poll()
    {
        string card = Dir("card"), letter = Dir("letter");
        var empty = FakeDrive(card, DriveKind.Removable, ready: false);
        SetDrives(empty);
        int changed = 0;
        using var mgr = NewManager(() => Interlocked.Increment(ref changed));
        mgr.Start(Settings());
        WaitUntilSettled(mgr);
        Assert.Equal(1, Volatile.Read(ref changed));

        // No event for a card going in: the drive list is looked at again every 30 seconds.
        var full = FakeDrive(card, DriveKind.Removable);
        SetDrives(full);
        var lastFullPlan = typeof(IndexManager).GetField("_lastFullPlanTicks", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.True(WaitFor(() =>
        {
            lastFullPlan.SetValue(mgr, Environment.TickCount64 - 60_000);
            Tick(mgr);
            return Volatile.Read(ref changed) == 2;
        }), "a card put in was not told");
        Assert.Equal([full], mgr.PlannedDrives);

        // A new drive letter is seen on the next tick.
        var mapped = FakeDrive(letter, DriveKind.Network);
        SetDrives(full, mapped);
        Volatile.Write(ref _mask, 1u << 25);
        Assert.True(WaitFor(() => { Tick(mgr); return Volatile.Read(ref changed) == 3; }), "a new letter was not told");
        Assert.Equal([full, mapped], mgr.PlannedDrives);
    }

    [Fact]
    public void An_index_manager_with_no_drives_has_none_and_never_tells()
    {
        IIndexManager im = new BareManager();
        Action never = () => throw new InvalidOperationException("never raised");
        im.DrivesChanged += never;
        Assert.Empty(im.PlannedDrives);
        im.DrivesChanged -= never;
    }

    /// <summary>An index manager with nothing but what <see cref="IIndexManager"/> asks for, as the demo's.</summary>
    sealed class BareManager : IIndexManager
    {
        public FileIndex Index { get; } = new();
        public IndexState State => IndexState.Ready;
        public string StatusText => "";
        public event Action? StateChanged { add { } remove { } }
        public bool IsElevated => false;
        public bool UsesService => false;
        public void Start(IndexSettings settings) { }
        public void Rebuild() { }
        public void Save() { }
        public void Dispose() { }
    }
}

using System.Diagnostics;
using Anything.Core.Indexing;

namespace Anything;

/// <summary>Where the Store copy's index helper stands in this run: Settings › Indexing and the yellow bar follow it.</summary>
internal enum IndexHelperState
{
    /// <summary>Not started in this run: turned off, no window opened yet, or a service Setup installed reads the drives.</summary>
    NotStarted,
    /// <summary>Starting: Windows asks for permission, then the helper starts.</summary>
    Starting,
    /// <summary>The helper answers: NTFS drives are read through it.</summary>
    Serving,
    /// <summary>Windows' permission was declined: folders are scanned, and nothing asks again in this run unless the user does.</summary>
    Declined,
    /// <summary>The helper didn't start, didn't answer or ended: folders are scanned, and nothing asks again in this run unless the user does.</summary>
    Stopped,
    /// <summary>Another account's helper has the pipe, which refuses this one: a helper couldn't serve beside it, so none starts.</summary>
    InUse,
}

/// <summary>
/// The Microsoft Store copy's instant indexing (<see cref="IndexHelper"/>): with Settings › Indexing's switch on, the
/// first window of each run has Windows start the helper elevated (Windows asks), never a start in the tray. The index
/// loads its database as usual, then waits for Windows' answer before it plans its drives (<see cref="IIndexManager.PlanAfter"/>),
/// so the NTFS drives are read through the helper from the start, catching up from their journals. A helper that comes
/// later (turned on, Ask again, or the first window after a start in the tray) has indexing start again from the
/// database, off the UI thread; so does one that ends or is turned off, scanning those drives again: what they held stays
/// searchable meanwhile.
/// </summary>
internal sealed partial class AnythingApp
{
    IndexHelperState _helper;
    bool _helperTried, _helperLooking;
    /// <summary>Counts starts and stops, so a start still waiting on Windows or the pipe knows it was overtaken.</summary>
    int _helperRun;
    /// <summary>The stop event of the start under way: one per start, set and closed when that start is stopped, never reused.</summary>
    EventWaitHandle? _helperStop;
    System.Windows.Forms.Timer? _helperWatch;
    /// <summary>
    /// What the index's first run waits for before it plans its drives, when the helper is asked for as the first window
    /// opens: done once the helper answers, or won't in this start.
    /// </summary>
    TaskCompletionSource? _planWait;

    /// <summary>Where the helper stands in this run.</summary>
    public IndexHelperState HelperState => _helper;

    /// <summary>A window was shown in this run: with the switch on, the helper was asked for then.</summary>
    public bool HelperAsked => _helperTried;

    /// <summary>
    /// This copy offers the helper: the Store's, not the demo, not run as administrator (it reads the drives itself then),
    /// and for an administrator's account (<see cref="IndexHelper.UserCanElevate"/>). The GitHub copy has the service instead.
    /// </summary>
    public bool OffersIndexHelper => StoreCopyScans && IndexHelper.UserCanElevate;

    /// <summary>The Store copy, run by an account that isn't an administrator: no helper, and Settings › Indexing says why.</summary>
    public bool IndexHelperNeedsAdministrator => StoreCopyScans && !IndexHelper.UserCanElevate;

    bool StoreCopyScans => StorePackage.IsInstalled && !IsDemo && !IndexManager.IsElevated;

    /// <summary>
    /// Before the index's first start: with the switch on and a window about to open, its first run plans the drives only
    /// once Windows' answer is in, rather than scan them and read them all over again a moment later.
    /// </summary>
    void HoldPlanningForHelper(CommandLine cmd)
    {
        if (!OffersIndexHelper || !Settings.StoreInstantIndexing || cmd.Exit || StartsInTray(cmd)) return;
        _planWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IndexManager.PlanAfter(_planWait.Task);
    }

    /// <summary>The index plans its drives now: the helper answers, or it won't in this start.</summary>
    void ReleasePlanning() => _planWait?.TrySetResult();

    /// <summary>
    /// A window was shown: the first time in this run, with the switch on, the helper starts and Windows asks. Never at a
    /// start in the tray (with Windows, say): the question waits for a window the user opens.
    /// </summary>
    void StartIndexHelperOnce()
    {
        if (_helperTried || _exiting) return;
        _helperTried = true; // turned on later, it starts then (TurnOnIndexHelper)
        if (OffersIndexHelper && Settings.StoreInstantIndexing) StartIndexHelper();
        else ReleasePlanning();
    }

    /// <summary>Settings › Indexing's Turn on and Ask again, and the yellow bar's link: saves the switch on and starts the helper now.</summary>
    public void TurnOnIndexHelper()
    {
        if (_exiting || !OffersIndexHelper) return;
        _helperTried = true;
        if (!Settings.StoreInstantIndexing)
        {
            Settings.StoreInstantIndexing = true;
            Settings.Save();
        }
        foreach (var w in _windows) w.OnIndexStateChanged(); // the bar goes
        StartIndexHelper();
    }

    /// <summary>
    /// Settings › Indexing's Turn off: saves the switch off (and the yellow bar's offer as answered, so it doesn't come
    /// back) and ends the helper. Once its pipe is gone, indexing starts again from the database and scans the drives it
    /// read, so they stay searchable meanwhile.
    /// </summary>
    public async void TurnOffIndexHelper()
    {
        if (Settings.StoreInstantIndexing || !Settings.StoreIndexingBarDismissed)
        {
            Settings.StoreInstantIndexing = false;
            Settings.StoreIndexingBarDismissed = true;
            Settings.Save();
        }
        bool started = _helper is IndexHelperState.Starting or IndexHelperState.Serving;
        StopIndexHelper();
        int run = _helperRun;
        SetHelperState(IndexHelperState.NotStarted);
        // It ends as soon as it sees its event: nothing plans again until its pipe is gone, so the drives are scanned.
        if (started)
            for (int i = 0; i < 20 && !_exiting && run == _helperRun && await Task.Run(() => VolumeService.IsRunning()); i++) await Task.Delay(250);
        if (_exiting || run != _helperRun) return; // turned on again meanwhile: that start goes on
        bool planned = IndexManager.HasPlanned;
        ReleasePlanning();
        if (started && planned && IndexManager.UsesService) await RestartIndexing();
    }

    async void StartIndexHelper()
    {
        if (_helper is IndexHelperState.Starting or IndexHelperState.Serving) return;
        int run = ++_helperRun;
        // A service Setup installed reads the drives for this copy too, or will once it answers: the pipe's name is its own.
        if (IndexService.IsInstalled())
        {
            SetHelperState(IndexHelperState.NotStarted);
            ReleasePlanning();
            return;
        }
        SetHelperState(IndexHelperState.Starting);
        // The name taken already: by another account's helper, which this one couldn't serve beside, or by this copy's
        // last run's helper (a restart), which goes with that run in a moment.
        var since = Stopwatch.StartNew();
        VolumePipe pipe;
        while ((pipe = await Task.Run(() => VolumeService.Probe())) != VolumePipe.Missing)
        {
            if (run != _helperRun || _exiting) return;
            if (since.Elapsed > TimeSpan.FromSeconds(3))
            {
                // One that answers this account is read through as it is; one that refuses it is said so.
                SetHelperState(pipe == VolumePipe.Answers ? IndexHelperState.NotStarted : IndexHelperState.InUse);
                ReleasePlanning();
                return;
            }
            await Task.Delay(250);
        }
        if (run != _helperRun || _exiting) return;
        // This start's own stop event, made new: without it the helper couldn't be stopped, so it isn't started then.
        try
        {
            var stop = new EventWaitHandle(false, EventResetMode.ManualReset, IndexHelper.StopEventName(Environment.ProcessId, run), out bool made);
            if (!made)
            {
                stop.Dispose();
                throw new IOException("The helper's stop event is there already.");
            }
            _helperStop = stop;
        }
        catch (Exception)
        {
            SetHelperState(IndexHelperState.Stopped);
            ReleasePlanning();
            return;
        }
        var result = await Task.Run(() => IndexHelper.Start(Environment.ProcessId, run));
        if (run != _helperRun || _exiting) return;
        if (result != IndexHelper.StartResult.Started)
        {
            // Declined: nothing asks again in this run until the user does (Ask again).
            StopIndexHelper();
            SetHelperState(result == IndexHelper.StartResult.Declined ? IndexHelperState.Declined : IndexHelperState.Stopped);
            ReleasePlanning();
            return;
        }
        // Windows may still be asking (its question can outlast the start): up to two minutes for the pipe to answer.
        var waited = Stopwatch.StartNew();
        bool answers = false;
        while (!_exiting && run == _helperRun && !(answers = await Task.Run(() => VolumeService.IsRunning())) && waited.Elapsed < TimeSpan.FromMinutes(2))
            await Task.Delay(500);
        if (run != _helperRun || _exiting) return;
        if (!answers)
        {
            StopIndexHelper(); // one that starts after all finds its event set, or gone, and ends at once
            SetHelperState(IndexHelperState.Stopped);
            ReleasePlanning();
            return;
        }
        // Asked before the index may plan: a run that waited for this plans now, with the pipe up, and needs no new start.
        bool planned = IndexManager.HasPlanned;
        SetHelperState(IndexHelperState.Serving);
        ReleasePlanning();
        if (planned && (!IndexManager.UsesService || IndexManager.ScansInsteadOfReading)) await RestartIndexing();
        if (run == _helperRun && !_exiting) WatchIndexHelper();
    }

    /// <summary>
    /// Tells the helper of the start under way to end (it ends with this process anyway) and stops watching it. Its event
    /// is set, then closed: a helper still to come finds it gone, and doesn't serve.
    /// </summary>
    void StopIndexHelper()
    {
        _helperRun++;
        _helperWatch?.Stop();
        if (Interlocked.Exchange(ref _helperStop, null) is not { } stop) return;
        try { stop.Set(); }
        catch (Exception) { }
        stop.Dispose();
    }

    void SetHelperState(IndexHelperState state)
    {
        if (_helper == state) return;
        _helper = state;
        if (_exiting) return;
        foreach (var w in _windows) w.OnIndexStateChanged();
    }

    /// <summary>Looks every 10 seconds whether the helper still answers (ended from Task Manager, say).</summary>
    void WatchIndexHelper()
    {
        if (_helperWatch is null)
        {
            _helperWatch = new System.Windows.Forms.Timer { Interval = 10_000 };
            _helperWatch.Tick += async (_, _) => await LookAtIndexHelper();
        }
        _helperWatch.Start();
    }

    async Task LookAtIndexHelper()
    {
        if (_helperLooking || _exiting || _helper != IndexHelperState.Serving) return;
        _helperLooking = true;
        int run = _helperRun;
        try
        {
            if (await Task.Run(() => HelperAnswers())) return;
            if (_exiting || run != _helperRun || _helper != IndexHelperState.Serving) return;
            // Gone: indexing starts again from the database, scanning the drives it read, as after Turn off.
            StopIndexHelper();
            SetHelperState(IndexHelperState.Stopped);
            if (IndexManager.HasPlanned && IndexManager.UsesService) await RestartIndexing();
        }
        finally { _helperLooking = false; }
    }

    /// <summary>Whether the helper's pipe answers, asked up to three times half a second apart: one busy moment isn't an end.</summary>
    static bool HelperAnswers()
    {
        for (int i = 0; i < 3; i++)
        {
            if (VolumeService.IsRunning()) return true;
            Thread.Sleep(500);
        }
        return false;
    }

    /// <summary>
    /// Indexing starts again from the database (as after installing the service), off the UI thread: stopping waits for
    /// the run under way and saves first, which can take seconds with a big index. The NTFS drives are read through the
    /// helper now, or scanned again without it, and what they held stays searchable meanwhile.
    /// </summary>
    async Task RestartIndexing()
    {
        try { await Task.Run(() => IndexManager.Start(Settings.Index)); }
        catch (Exception ex)
        {
            if (_exiting) return; // the index was closed meanwhile
            MessageBox.Show($"Unable to restart indexing.\n\n{ex.Message}", "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        if (_exiting) return;
        SubscribeIndex();
        foreach (var w in _windows) w.OnIndexStateChanged();
    }
}

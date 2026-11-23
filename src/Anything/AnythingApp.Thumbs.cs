using Anything.Core;
using Anything.Core.Thumbs;

namespace Anything;

/// <summary>
/// "Instant thumbnails": the store's place in the program (chosen at start-up, changed from Settings, closed at exit),
/// the estimate of what it would take for this PC's files (worked out in the background once indexing is complete), and
/// the waits that lead to offering it once. Demo mode has none of it.
/// </summary>
internal sealed partial class AnythingApp
{
    readonly ThumbWaitOccasions _thumbOccasions = new();
    ThumbCensus? _thumbCensus;
    string? _thumbCensusFilter; // the Everyday files filter the estimate was counted with (null: every file)
    bool _thumbCounting;
    readonly CancellationTokenSource _thumbCountCts = new();

    /// <summary>What thumbnails for every indexed picture, video and document would take; null until worked out.</summary>
    public ThumbCensus? ThumbEstimate
    {
        get
        {
            // "Everyday files only" changed since it was counted: counted again (in the background), the old one meanwhile.
            if (_thumbCensus is not null && (Settings.EverydayFilesOnly ? Settings.Everyday.Filter : null) != _thumbCensusFilter) EstimateThumbnails();
            return _thumbCensus;
        }
    }

    /// <summary>Indexing is complete, so the estimate means something (not in the demo).</summary>
    public bool IndexingComplete => !IsDemo && IndexManager.State == IndexState.Ready;

    /// <summary>
    /// The size class the estimate is worked out for: that of the thumbnail size in use (160, the Thumbnails button's
    /// size, while the details list shows). Pictures for a size over 256 take about three times as much.
    /// </summary>
    public int ThumbEstimateClass => ThumbSizeClass.For(Settings.ThumbnailSize > 0 ? Settings.ThumbnailSize : 160);

    /// <summary>The estimate was worked out (again): Settings shows the new numbers.</summary>
    public event Action? ThumbEstimateChanged;

    /// <summary>At start-up: the store chosen in the settings is handed to the thumbnail service (which opens it on its first request).</summary>
    void StartThumbnails()
    {
        if (IsDemo) return;
        _ = ThumbHost.Choose(StoreOpener());
        EstimateThumbnails();
        RemoveThumbLeftovers();
        ScheduleThumbWarm();
    }

    // Warming at start-up: with "Instant thumbnails" on, the thumbnail service is made a few seconds after the first window
    // shows, once the program is idle, so the first Thumbnails view doesn't pay for its threads, their first calls and
    // the store's opening. Without the option nothing happens here (a user who never opens Thumbnails pays nothing);
    // hovering over the Thumbnails button warms it too. The timer looks every 2 s until a window has been seen on two
    // looks in a row, then stops: a few ticks of a UI timer, once per run.
    System.Windows.Forms.Timer? _thumbWarmTimer;
    bool _thumbWindowSeen;
    const int ThumbWarmLookMs = 2_000;

    void ScheduleThumbWarm()
    {
        if (IsDemo || !Settings.ThumbnailStore) return;
        _thumbWarmTimer = new System.Windows.Forms.Timer { Interval = ThumbWarmLookMs };
        _thumbWarmTimer.Tick += (_, _) => LookToWarmThumbnails();
        _thumbWarmTimer.Start();
    }

    void LookToWarmThumbnails()
    {
        // Made already (a view opened, the button hovered), the option turned off, or the program closing: nothing to do.
        if (_exiting || ThumbHost.Started || !Settings.ThumbnailStore)
        {
            StopThumbWarm();
            return;
        }
        // A window shown on this look and the one before: between 2 and 4 s after it showed (started in the tray, the
        // warming waits for the first window).
        bool shown = _windows.Any(w => w.Visible);
        bool seenBefore = _thumbWindowSeen;
        _thumbWindowSeen = shown;
        if (!shown || !seenBefore) return;
        StopThumbWarm();
        Application.Idle += WarmThumbnailsWhenIdle;
    }

    void WarmThumbnailsWhenIdle(object? sender, EventArgs e)
    {
        Application.Idle -= WarmThumbnailsWhenIdle;
        if (_exiting || IsDemo || !Settings.ThumbnailStore) return;
        ThumbHost.Warm();
    }

    void StopThumbWarm()
    {
        _thumbWarmTimer?.Stop();
        _thumbWarmTimer?.Dispose();
        _thumbWarmTimer = null;
    }

    /// <summary>How the store is opened under the settings now (a worker calls it); null with the option off or in the demo.</summary>
    Func<ThumbStore>? StoreOpener()
    {
        if (IsDemo || !Settings.ThumbnailStore) return null;
        string folder = Settings.ThumbnailStoreFolder;
        long cap = ThumbStore.CapBytesFor(Settings.ThumbnailStoreMegabytes);
        // The default place is Rock Preview's own data folder, which is only made when the index is first saved: on a first
        // run the option may be turned on before that. A place the user chose is never built (the store opens it or says
        // it can't be used).
        string? own = Settings.ThumbnailStorePath.Length == 0 ? Path.GetDirectoryName(folder) : null;
        return () =>
        {
            if (own is { Length: > 0 })
            {
                try { Directory.CreateDirectory(own); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
            return ThumbStore.Open(folder, new ThumbStoreOptions { CapBytes = cap });
        };
    }

    /// <summary>
    /// Turns "Instant thumbnails" on: the store opens on the next thumbnail asked for. Saved at once; the bar offering it
    /// goes from every window.
    /// </summary>
    public void TurnOnThumbStore()
    {
        if (IsDemo) return;
        Settings.ThumbnailStore = true;
        // A folder of an earlier store, still to be removed, that is the place used again now: it is the store again.
        Settings.ThumbnailStoreLeftovers.RemoveAll(f => AppSettings.SamePath(f, Settings.ThumbnailStoreFolder));
        Settings.Save();
        _ = ThumbHost.Choose(StoreOpener());
        foreach (var w in _windows)
        {
            w.HideThumbStoreBar();
            w.ThumbStoreChosen(); // the idle step may start now, without a scroll
        }
    }

    /// <summary>
    /// A store folder that was to be removed and wasn't found: when its drive isn't there (a stick taken out, a share not
    /// connected), that is a failure worth saying, not "nothing to remove".
    /// </summary>
    static StoreRemoval Reached(StoreRemoval removal, string folder)
    {
        if (removal != StoreRemoval.NotThere) return removal;
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            return root is { Length: > 0 } && !Directory.Exists(root) ? StoreRemoval.Failed : removal;
        }
        catch (Exception) { return removal; }
    }

    /// <summary>
    /// Turns it off: thumbnails go on the normal way at once, the store closes, and its folder is removed (only when it
    /// holds nothing but the store's own files). The task ends when that is done, with what happened to the folder; a
    /// store that was still opening there and didn't finish within a few seconds (a drive asleep) leaves the folder,
    /// reported as not removed.
    /// </summary>
    public async Task<StoreRemoval> TurnOffThumbStore()
    {
        string folder = Settings.ThumbnailStoreFolder;
        Settings.ThumbnailStore = false;
        Settings.Save();
        bool closed;
        try { closed = await ThumbHost.Choose(null); } catch (Exception) { closed = false; }
        var removal = closed ? await Task.Run(() => Reached(ThumbStore.RemoveFolder(folder), folder)) : StoreRemoval.Failed;
        if (removal == StoreRemoval.Failed) NoteThumbLeftover(folder);
        // An opener still out may create the folder later. Keep its reminder for the next sweep.
        RemoveThumbLeftovers(closed ? null : folder);
        return removal;
    }

    /// <summary>
    /// The store goes to <paramref name="chosenFolder"/> (a folder "Rock Preview Thumbnails" is made in it): an empty one
    /// starts there, and the old one is removed once closed. Moved is false when that is where it already is, or when the
    /// new place lies inside the old store's folder (which could then never be removed); Removal says what happened to
    /// the old folder.
    /// </summary>
    public async Task<(bool Moved, StoreRemoval Removal)> MoveThumbStore(string chosenFolder)
    {
        string old = Settings.ThumbnailStoreFolder;
        string now = ThumbOption.StoreFolder(Settings.Index.DatabasePath, chosenFolder);
        if (AppSettings.SamePath(old, now) || ThumbOption.IsWithin(now, old)) return (false, StoreRemoval.NotThere);
        // The same folder reached another way (a junction, a subst drive, a mapped drive or its share): where Windows
        // really puts them, compared off the UI thread (a network folder may be slow to answer).
        bool same = await Task.Run(() =>
        {
            string oldReal = ThumbStore.FinalPath(old), nowReal = ThumbStore.FinalPath(now);
            return AppSettings.SamePath(oldReal, nowReal) || ThumbOption.IsWithin(nowReal, oldReal);
        });
        if (same) return (false, StoreRemoval.NotThere);
        Settings.ThumbnailStorePath = chosenFolder;
        Settings.Save();
        if (Settings.ThumbnailStore)
        {
            bool closed;
            try { closed = await ThumbHost.Choose(StoreOpener()); } catch (Exception) { closed = false; }
            if (!closed)
            {
                NoteThumbLeftover(old);
                return (true, StoreRemoval.Failed);
            }
        }
        var removal = await Task.Run(() => Reached(ThumbStore.RemoveFolder(old), old));
        if (removal == StoreRemoval.Failed) NoteThumbLeftover(old);
        return (true, removal);
    }

    /// <summary>
    /// A store folder that couldn't be removed when the option was turned off or the store moved (a drive asleep or gone,
    /// a file in use): remembered in the settings, and removed when it can be, at the next start or the next turn-off;
    /// uninstall removes it too.
    /// </summary>
    void NoteThumbLeftover(string folder)
    {
        if (folder.Length == 0 || Settings.ThumbnailStoreLeftovers.Exists(f => AppSettings.SamePath(f, folder))) return;
        Settings.ThumbnailStoreLeftovers.Add(folder);
        Settings.Save();
    }

    /// <summary>
    /// Tries again to remove the store folders left behind (off the UI thread); each one removed, or no longer there, is
    /// forgotten. Never the folder in use now.
    /// </summary>
    void RemoveThumbLeftovers(string? stillClosing = null)
    {
        if (IsDemo || Settings.ThumbnailStoreLeftovers.Count == 0) return;
        string? inUse = Settings.ThumbnailStore ? Settings.ThumbnailStoreFolder : null;
        var folders = Settings.ThumbnailStoreLeftovers.Where(f => (inUse is null || !AppSettings.SamePath(f, inUse))
            && (stillClosing is null || !AppSettings.SamePath(f, stillClosing))).ToList();
        Task.Run(() => folders.Where(f => Reached(ThumbStore.RemoveFolder(f), f) != StoreRemoval.Failed).ToList()).ContinueWith(t => Post(() =>
        {
            if (_exiting || !t.IsCompletedSuccessfully) return;
            int before = Settings.ThumbnailStoreLeftovers.Count;
            Settings.ThumbnailStoreLeftovers.RemoveAll(f => t.Result.Exists(done => AppSettings.SamePath(done, f))
                                                           || (Settings.ThumbnailStore && AppSettings.SamePath(f, Settings.ThumbnailStoreFolder)));
            if (Settings.ThumbnailStoreLeftovers.Count != before) Settings.Save();
        }), TaskScheduler.Default);
    }

    /// <summary>The limit changed: saved, and the open store keeps to it at once.</summary>
    public void SetThumbStoreLimit(int megabytes)
    {
        Settings.ThumbnailStoreMegabytes = megabytes;
        Settings.Save();
        ThumbHost.SetCap(megabytes);
        // A store not open yet takes the limit from how it is opened.
        if (!ThumbHost.Started) _ = ThumbHost.Choose(StoreOpener());
    }

    /// <summary>
    /// Settings' Clear: every saved thumbnail goes, and so do the pictures the windows hold in memory (they are made
    /// again as they are shown).
    /// </summary>
    public async Task ClearThumbStore()
    {
        if (ThumbHost.Started) ThumbHost.ClearStore();
        else
        {
            // Nothing has the store open in this run: its folder can go as a whole (it is made again when needed).
            string folder = Settings.ThumbnailStoreFolder;
            await Task.Run(() => ThumbStore.RemoveFolder(folder));
        }
        ThumbHost.ClearMemory();
        foreach (var w in _windows) w.ClearThumbnails();
    }

    /// <summary>
    /// Works out (once indexing is complete, in the background) what thumbnails for every picture, video and document
    /// indexed would take, and again when the index has changed much since. Never in the demo.
    /// </summary>
    void EstimateThumbnails()
    {
        if (IsDemo || _exiting || _thumbCounting || IndexManager.State != IndexState.Ready) return;
        var index = IndexManager.Index;
        if (index is null) return;
        // With "Everyday files only" (the default) the thumbnail views show only those files, so only they are counted;
        // the estimate is counted again when that setting changes.
        var only = Settings.EverydayFilesOnly ? Settings.Everyday : null;
        string? filter = only?.Filter;
        // The index's live count then against now, like with like (folders count in both).
        if (_thumbCensus is { Complete: true } c && filter == _thumbCensusFilter && !ThumbEstimator.ChangedMuch(c.LiveAtCount, index.LiveCount)) return;
        _thumbCounting = true;
        var ct = _thumbCountCts.Token;
        Task.Run(() =>
        {
            try { return (ThumbCensus?)ThumbEstimator.CountIndex(index, ct, only); }
            catch (Exception) { return null; }
        }).ContinueWith(t => Post(() =>
        {
            _thumbCounting = false;
            if (_exiting || t.Result is not { } census) return;
            _thumbCensus = census;
            _thumbCensusFilter = filter;
            ThumbEstimateChanged?.Invoke();
            // "Everyday files only" may have changed while this one counted: then it is counted again (only then: a
            // count that just finished is not started again for nothing).
            if ((Settings.EverydayFilesOnly ? Settings.Everyday.Filter : null) != filter) EstimateThumbnails();
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// A window's thumbnails kept the user waiting more than a second. Counted once per occasion (in the settings); true
    /// when the bar offering to keep thumbnails on disk may show now.
    /// </summary>
    public bool NoteThumbnailWait()
    {
        if (IsDemo || Settings.ThumbnailStore || Settings.ThumbnailStoreOffered) return false;
        if (_thumbOccasions.TryCount(Environment.TickCount64))
        {
            Settings.ThumbnailWaits++;
            Settings.Save();
        }
        return ThumbRecommendationDue;
    }

    bool ThumbRecommendationDue => ThumbOption.ShouldRecommend(Settings.ThumbnailStore, Settings.ThumbnailStoreOffered, IsDemo,
        IndexingComplete, _thumbCensus is not null, Settings.ThumbnailWaits);

    /// <summary>
    /// The estimate for the bar when it may show now, and remembered as shown (it shows once ever); null when it may not.
    /// </summary>
    public ThumbCensus? TakeThumbStoreRecommendation()
    {
        if (!ThumbRecommendationDue || _thumbCensus is not { } census) return null;
        Settings.ThumbnailStoreOffered = true;
        Settings.Save();
        return census;
    }

    /// <summary>At exit, before the index is saved and closed: the estimate (which reads the index) stops.</summary>
    void StopThumbnailEstimate() => _thumbCountCts.Cancel();

    /// <summary>
    /// At exit, after the index is saved: the store writes what it has queued and closes. Last, because a graphics
    /// driver's fault seen a few seconds after the service stopped must not cut the index's save short.
    /// </summary>
    void StopThumbnails() => ThumbHost.Shutdown();
}

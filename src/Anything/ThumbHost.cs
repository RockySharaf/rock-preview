using Anything.Core.Thumbs;
using Anything.Preview.Thumbnails;

namespace Anything;

/// <summary>
/// The program's one way to the thumbnail service: grids get their clients here, and the "Instant thumbnails" choice is
/// handed to the service here. The service (its threads) only starts when a grid first asks for a thumbnail, or when it
/// is warmed (<see cref="Warm"/>: with "Instant thumbnails" on, or the pointer resting on the Thumbnails button), so a
/// user who never opens Thumbnails pays nothing; the choice made before then is handed over at that moment. UI thread
/// only, except where a member says otherwise.
/// </summary>
internal static class ThumbHost
{
    static Func<ThumbStore>? _open;
    static bool _started;
    static ThumbKeep _keep = ThumbKeep.All;

    /// <summary>The pictures held in memory at least (96 MB), shared by every window's grid.</summary>
    const long MinMemoryBudget = 96L * 1024 * 1024;

    // Step 1 of the ladder, for the whole program: pictures at display size, least recently used first out. One for
    // every window, so a second window on the same folder draws what the first has (with nothing kept on disk, nothing
    // else would remember them). All of it on the UI thread, which every window shares.
    static readonly ThumbMemory<(ThumbKey Key, int Size), ThumbShown> _memory = new(MinMemoryBudget, s => s.Bitmap?.Dispose());
    static readonly Dictionary<object, long> _planBytes = [];
    // No thumbnail view shown anywhere (the details list, a window closed into the background): after this long the
    // pictures are let go, so a program left running in the tray doesn't hold them. A view shown again makes them again.
    const int ReleaseAfterMs = 30_000;
    static System.Windows.Forms.Timer? _release;

    /// <summary>The pictures every grid draws from (UI thread only).</summary>
    public static ThumbMemory<(ThumbKey Key, int Size), ThumbShown> Memory => _memory;

    /// <summary>
    /// What <paramref name="grid"/>'s plan (the cells on screen and around them) takes at display size, 0 when it shows
    /// none: memory always holds every window's plan with room to spare (a plan bigger than 96 MB, a large screen at a
    /// large size, would otherwise push out the pictures on screen as the look-ahead arrives).
    /// </summary>
    public static void NotePlan(object grid, long bytes)
    {
        if (bytes <= 0) _planBytes.Remove(grid);
        else _planBytes[grid] = bytes;
        long plans = 0;
        foreach (long b in _planBytes.Values) plans += b;
        _memory.Budget = Math.Max(MinMemoryBudget, plans + plans / 2);
        if (_planBytes.Count > 0)
        {
            _release?.Stop();
            return;
        }
        if (_release == null)
        {
            _release = new System.Windows.Forms.Timer { Interval = ReleaseAfterMs };
            _release.Tick += (_, _) =>
            {
                _release.Stop();
                if (_planBytes.Count == 0) _memory.Clear();
            };
        }
        _release.Stop();
        _release.Start();
    }

    /// <summary>Whether the service has started (a grid asked for a thumbnail in this run).</summary>
    public static bool Started => _started;

    /// <summary>The service, started with the store chosen so far when this is the first use.</summary>
    public static ThumbnailService Service
    {
        get
        {
            var service = ThumbnailService.Shared;
            if (!_started)
            {
                _started = true;
                service.StoreKeep = _keep;
                service.StoreBecameFull += () => StoreBecameFull?.Invoke();
                if (_open != null) service.UseStore(_open);
            }
            return service;
        }
    }

    /// <summary>
    /// Makes the thumbnail service now, if it doesn't exist yet, so the first Thumbnails view doesn't pay for it: its
    /// threads start (about 10 ms here, on the calling thread) and warm themselves as they do when idle (their first shell
    /// call and first decode, 150 to 200 ms each, on their own threads), and a store chosen starts opening on the thread
    /// pool. Nothing when the service exists already. UI thread; cheap; never waits for a worker or a file. Called a few
    /// seconds after start-up when "Instant thumbnails" is on, and when the pointer first rests on the Thumbnails button.
    /// </summary>
    public static void Warm()
    {
        if (_started) return;
        try { Service.OpenStoreSoon(); } catch (Exception) { /* only a warm-up: the first view makes it as before */ }
    }

    /// <summary>
    /// Whether the thumbnails kept on disk can be read by this user only (with SYSTEM and administrators), for Settings'
    /// note: true when no store is open (none chosen, the service not started, or one still opening) or the store works
    /// on nothing (a location that can't be used); the open store's own check otherwise (false on a drive that keeps no
    /// permissions, such as a USB stick, or a folder others can read). Any thread; cheap (a brief lock, no disk).
    /// </summary>
    public static bool StoreIsPrivate
    {
        get
        {
            if (!Volatile.Read(ref _started)) return true;
            var store = ThumbnailService.Shared.CurrentStore;
            return store is not { IsEnabled: true } || store.IsPrivate;
        }
    }

    /// <summary>
    /// Whether pictures are kept on disk now ("Instant thumbnails" on, with a store chosen whose location can be used, or
    /// isn't open yet): the idle step only runs then.
    /// </summary>
    public static bool KeepsStore => _open != null && (!_started || ThumbnailService.Shared.KeepsStore);

    /// <summary>Whether "Instant thumbnails" is on (a store chosen), whether or not its location can be used now.</summary>
    public static bool StoreChosen => _open != null;

    /// <summary>A new client for a grid (see "How to use" in ThumbnailService.cs).</summary>
    public static ThumbClient CreateClient(Action<ThumbArrival> onArrival) => Service.CreateClient(onArrival);

    /// <summary>
    /// The store from now on (<paramref name="open"/>), or none (null: the normal way). The task ends when the store used
    /// until now has closed: true, and its folder may be removed; false when a store still being opened there didn't
    /// finish in time (the folder must be left). At once (true) when the service hasn't started: nothing is open.
    /// </summary>
    public static Task<bool> Choose(Func<ThumbStore>? open)
    {
        _open = open;
        return _started ? ThumbnailService.Shared.UseStore(open) : Task.FromResult(true);
    }

    /// <summary>The store open now (null while none is, or the service hasn't started): Settings' line about it.</summary>
    public static ThumbStore? CurrentStore => _started ? ThumbnailService.Shared.CurrentStore : null;

    /// <summary>What the open store takes now; -1 when none is open.</summary>
    public static long StoreSizeBytes => _started ? ThumbnailService.Shared.StoreSizeBytes : -1;

    /// <summary>The limit changed: applies to the open store at once (a store opened later takes it from its options).</summary>
    public static void SetCap(int megabytes)
    {
        if (_started) ThumbnailService.Shared.StoreCapBytes = ThumbStore.CapBytesFor(megabytes);
    }

    /// <summary>
    /// Which thumbnails are kept on disk, as the user chose (the Instant thumbnails card in Settings): for pictures made
    /// from now on, and for the grids' idle planning (<see cref="Keep"/>). A service started later takes it then.
    /// </summary>
    public static void SetKeep(ThumbKeep keep)
    {
        _keep = keep;
        if (_started) ThumbnailService.Shared.StoreKeep = keep;
    }

    /// <summary>The user's choice of what is kept (<see cref="ThumbKeep.All"/> unless changed): the grid's idle planner asks it.</summary>
    public static ThumbKeep Keep => _keep;

    /// <summary>
    /// The store in use started dropping its oldest thumbnails. Raised on a worker thread, not the UI's: the program
    /// posts it to the UI thread before touching a window. Only once the service has started (no store is open before).
    /// </summary>
    public static event Action? StoreBecameFull;

    /// <summary>Whether the store in use is full now (false while none is open). Any thread; cheap.</summary>
    public static bool StoreIsFull => Volatile.Read(ref _started) && ThumbnailService.Shared.StoreIsFull;

    /// <summary>Whether the store in use refuses new pictures because its drive is nearly full (false while none is open). Any thread; cheap.</summary>
    public static bool StoreDiskLow => Volatile.Read(ref _started) && ThumbnailService.Shared.StoreDiskLow;

    /// <summary>Settings' Clear: the store is emptied, and what the service remembers in memory is forgotten.</summary>
    public static void ClearStore()
    {
        if (_started) ThumbnailService.Shared.ClearStore();
    }

    /// <summary>
    /// Opens the chosen store now, on a worker, and waits for it there (Settings wants to say whether its location can
    /// be used). Never on the UI thread.
    /// </summary>
    public static ThumbStore? OpenNow(ThumbnailService service)
    {
        try
        {
            service.WarmUp(TimeSpan.Zero, openStore: true);
            return service.CurrentStore;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Settings' Clear: the pictures every window holds in memory go too.</summary>
    public static void ClearMemory() => _memory.Clear();

    /// <summary>At exit: the service stops and the store writes what it has queued (a few seconds at most).</summary>
    public static void Shutdown()
    {
        if (!_started) return;
        try { ThumbnailService.Shared.Dispose(); } catch (Exception) { }
    }
}

/// <summary>What a cell can show: the picture (or icon, or nothing for None) made for one key at one display size.</summary>
internal sealed class ThumbShown(Bitmap? bitmap, ThumbQuality quality, int size)
{
    public readonly Bitmap? Bitmap = bitmap;
    public readonly ThumbQuality Quality = quality;
    public readonly int Size = size;
    public long Bytes => Bitmap is { } b ? (long)b.Width * b.Height * 4 : 64;
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using Anything.Core.Content.Indexing;
using Anything.Core.Thumbs;
using Anything.Preview.Interop;
using Anything.Preview.Util;

namespace Anything.Preview.Thumbnails;

// How to use the thumbnail service from the grid
// ================================================
//
//   One service for the whole program: ThumbnailService.Shared. It starts with no store: the normal way, "Instant
//   thumbnails" off (the default, and always in demo mode). Then nothing is written to disk (no folder, no in-flight
//   marker) and the idle step does not run; the rest of the ladder (Windows' cache, the previews inside files, the real
//   thumbnail, the icon) and the look-ahead work as usual.
//   With the option on, the program says where the store is, at start-up or whenever the setting changes:
//       Shared.UseStore(() => ThumbStore.Open(folder, new ThumbStoreOptions { CapBytes = ThumbStore.CapBytesFor(mb) }))
//   where folder is ThumbStore.FolderFor(databasePath), or ThumbStore.FolderIn(chosenFolder) for a chosen location.
//   The store is opened on the thread pool on the next request, never on the calling thread. UseStore never waits.
//   Turning the option off:     await Shared.UseStore(null); then ThumbStore.RemoveFolder(folder) (only after the task,
//                               and only when it says true: the old store must be closed first; false means a store
//                               being opened there didn't finish in time, so the folder is left and reported).
//   Changing the location:      await Shared.UseStore(() => ThumbStore.Open(newFolder, ...)); then remove the old folder.
//   Changing the limit:         Shared.StoreCapBytes = ThumbStore.CapBytesFor(mb) (applies at once, and to a store opened later).
//   What is kept:               Shared.StoreKeep = the user's ThumbKeep (asked with ThumbKinds.KeepOnDisk, for pictures made
//                               from then on). A group switched off has its saved thumbnails deleted in the background;
//                               types left out stay until dropped for room. Each picture is saved with its group (ThumbKeep.GroupOf).
//   Full:                       Shared.StoreBecameFull is raised on a worker when the store starts dropping
//                               thumbnails (marshal to the UI); Shared.StoreIsFull and StoreDiskLow say how it is now.
//   Clear:                      Shared.ClearStore() empties the store (the grid clears its own memory cache itself).
//   Settings' line:             Shared.CurrentStore (null until the first request opens it) has Problem, Folder, IsEnabled;
//                               Shared.StoreSizeBytes and StoreCapBytes give "123 MB of 500 MB". A folder that can't be
//                               used leaves the option on and the store idle: no error is ever raised while browsing.
//   Requests in flight while the store changes are not lost or mixed up: a picture finished after the change goes to
//   the store chosen now (keys don't depend on the store), or nowhere when there is none.
//   At exit: Shared.Dispose() (writes what the store has queued, 2 s at most).
//
//   Each grid makes its own client:  var client = ThumbnailService.Shared.CreateClient(OnArrival);
//
//   client.SubmitViewport(listNumber, requests)   UI thread (any thread is safe). Cheap: no file is touched. Call it
//       whenever the scroll position, the size, the list or the thumbnail size changes, never from painting. requests
//       is the whole plan in order (ThumbOrder.Viewport gives it: visible cells in reading order, then two screens
//       ahead, then one behind), each a ThumbRequest(path, stamp from the index or ThumbStamp.Unknown, display size,
//       priority, list index). Work not started for items no longer in the plan is dropped; a new listNumber (a new
//       search, another folder, Refresh) drops everything not started. Work already started is finished and stored.
//   client.SubmitIdle(listNumber, requests)       UI thread (any thread). Idle warming (IdlePlanner gives the chunks):
//       runs after everything else, on at most 2 decode threads and 1 shell thread, and waits while Battery Saver is on,
//       a full-screen program is in front, or the disk is low. Ignored (returns 0) for a list that is no longer current
//       and while the service has no store (Shared.KeepsStore is false): check KeepsStore before planning idle work.
//   client.IsIdle / client.Changed                 everything submitted is done (for the grid's 1.5 s idle rule).
//       Changed is raised on whatever thread made the change (often a worker): marshal to the UI before touching it.
//   client.Dispose()                               the grid is closing: drops its work; arrivals after this are disposed.
//
//   OnArrival(ThumbArrival a)                      raised on a worker thread, any number at once from several workers.
//       a.Key is the key of the request (ThumbRequest.Key: as asked, also when the stamp was Unknown), a.DisplaySize
//       the size asked for, a.Quality Low (a stand-in: draw it scaled until the Final comes), Final, or None (no picture
//       can be made: a.Bitmap is the file's icon, or null when not even that). a.Bitmap is 32bpp premultiplied and fits
//       in DisplaySize x DisplaySize. THE HANDLER OWNS a.Bitmap and must dispose it (also when it drops the arrival as
//       out of date). It must not block: queue the arrival and take the queue on the UI thread a little at a time.
//       If the handler throws, the service disposes the bitmap.
//
//   Threads, in short: every public member of ThumbnailService and ThumbClient may be called from the UI thread and
//   returns at once (none touches a file, waits for a worker, or holds a lock a worker keeps for long), except WarmUp
//   and Dispose, which block (WarmUp: never from the UI thread; Dispose: at exit). Bitmaps: the service owns every
//   bitmap it makes until it hands one over in a ThumbArrival; from then the handler owns it. Nothing the service
//   hands over is ever touched by the service again.
//
// What runs where: the fast lane (2 STA threads) asks Windows' thumbnail cache, reads the previews inside files and
// fetches icons; the decode lane (3/8 of the processors, 2 to 6 STA threads) reads the store and runs Rock Preview's own
// decoders and extractors; the shell lane (3 STA threads) runs Windows' extractor (for folders, shortcuts and the PDF
// kinds its thumbnail cache only), whose calls are given up after 12 s (the stuck thread is replaced). Nothing here ever uses StaPool.Shared, which the preview pane keeps for itself.

/// <summary>One picture handed to a client: see "How to use" at the top of this file. The receiver owns <see cref="Bitmap"/>.</summary>
internal sealed class ThumbArrival
{
    internal ThumbArrival(ThumbKey key, string path, int displaySize, Bitmap? bitmap, ThumbQuality quality, int listNumber, int index)
    {
        Key = key;
        Path = path;
        DisplaySize = displaySize;
        Bitmap = bitmap;
        Quality = quality;
        ListNumber = listNumber;
        Index = index;
    }

    /// <summary>The key of the request this answers (as the request made it).</summary>
    public ThumbKey Key { get; }
    /// <summary>The file's path as the request gave it.</summary>
    public string Path { get; }
    /// <summary>The display size asked for; the bitmap fits in a square of this size.</summary>
    public int DisplaySize { get; }
    /// <summary>The picture (or the icon for <see cref="ThumbQuality.None"/>); null when there is nothing to show. Owned by the receiver.</summary>
    public Bitmap? Bitmap { get; }
    /// <summary>Low (a stand-in, replaced later), Final, or None (the icon).</summary>
    public ThumbQuality Quality { get; }
    /// <summary>The list number the job last belonged to.</summary>
    public int ListNumber { get; }
    /// <summary>The list index the request gave (-1 when not known).</summary>
    public int Index { get; }
}

/// <summary>How a service is set up; the defaults are the product's.</summary>
internal sealed class ThumbServiceOptions
{
    /// <summary>Opens the store on the first request (on a worker); null: no store.</summary>
    public Func<ThumbStore>? OpenStore { get; init; }
    /// <summary>Battery Saver, full screen and disk space, for the idle rule.</summary>
    public IPowerPolicy Power { get; init; } = DefaultPowerPolicy.Current;
    /// <summary>Decode threads (default: 3/8 of the logical processors, 2 to 6).</summary>
    public int DecodeThreads { get; init; } = ThumbRules.DecodeThreadsFor(Environment.ProcessorCount);
    /// <summary>A Windows call on a fast or shell thread that takes longer than this is given up (default 12 s).</summary>
    public TimeSpan ShellTimeout { get; init; } = ThumbRules.ShellTimeout;
    /// <summary>The prefix of the threads' names.</summary>
    public string Name { get; init; } = "Thumbnails";
    /// <summary>
    /// Without a store, have Windows make (and keep in its own cache) the pictures Rock Preview decoded itself, at idle
    /// times on one shell thread, so a folder seen again after a restart comes from Windows' cache as it did in 1.7.27.
    /// </summary>
    public bool WarmWindowsCache { get; init; } = true;
}

/// <summary>Counts of what a service did (for the benchmark and diagnostics); a snapshot.</summary>
internal sealed record ThumbServiceStats(
    long StoreHits, long StoreNone, long StoreMisses, long StoreBytes, long CacheProbes, long CacheFinal, long CacheLow,
    long EmbeddedLows, long OwnDecodes, long OwnExtracts, long ShellExtracts, long ShellTimeouts, long TextSniffs,
    long IconsFetched, long IconsRemembered, long FinalsStored, long NonesStored, long Failures, long Refused,
    long ArrivedLow, long ArrivedFinal, long ArrivedNone, long Dropped, long WindowsWarms);

/// <summary>
/// A grid's connection to the service: its own request order (one <see cref="ThumbQueue"/>) and its arrivals. See
/// "How to use" at the top of this file for which call is safe where.
/// </summary>
internal sealed class ThumbClient : IDisposable
{
    private readonly ThumbnailService _service;
    private readonly Action<ThumbArrival> _onArrival;
    private volatile bool _disposed;
    // Idle jobs the service dropped after their front stage (a new viewport, Clear): not made, so the grid may plan them again.
    private readonly System.Collections.Concurrent.ConcurrentQueue<ThumbKey> _droppedIdle = new();
    internal int LastList = int.MinValue;

    internal ThumbClient(ThumbnailService service, Action<ThumbArrival> onArrival, bool allowIdle)
    {
        _service = service;
        _onArrival = onArrival;
        Queue = new ThumbQueue(allowIdle);
        Queue.Changed += () =>
        {
            Changed?.Invoke();
            service.Wake();
        };
    }

    // Idle work is allowed only while the service has a store: without one its results would have nowhere to go.
    internal ThumbQueue Queue { get; }

    /// <summary>Raised after work was added, dropped or finished, on the thread that made the change.</summary>
    public event Action? Changed;

    /// <summary>Everything submitted is done (nothing waits, nothing runs).</summary>
    public bool IsIdle => Queue.IsIdle;

    /// <summary>Jobs waiting or running (diagnostics).</summary>
    public int PendingCount => Queue.PendingCount + Queue.RunningCount;

    internal bool IsDisposed => _disposed;

    /// <summary>Replaces the plan (see "How to use"). Any thread; cheap.</summary>
    public void SubmitViewport(int listNumber, IReadOnlyList<ThumbRequest> ordered)
    {
        if (_disposed) return;
        _service.SubmitViewport(this, listNumber, ordered);
    }

    /// <summary>Adds idle work after everything else (see "How to use"). Returns how many jobs were added.</summary>
    public int SubmitIdle(int listNumber, IReadOnlyList<ThumbRequest> requests)
    {
        if (_disposed) return 0;
        int added = Queue.SubmitIdle(listNumber, requests);
        if (added > 0) _service.Wake();
        return added;
    }

    /// <summary>Drops every job not started.</summary>
    public void Clear() => _service.DropClient(this);

    /// <summary>
    /// The keys of idle work the service dropped since the last call without making it (it had passed its first stage
    /// and waited for the next when a viewport or <see cref="Clear"/> dropped it): the grid hands them back to its idle
    /// planner, as it does with idle work dropped before any worker took it. Any thread.
    /// </summary>
    public List<ThumbKey> TakeDroppedIdle()
    {
        var keys = new List<ThumbKey>();
        while (_droppedIdle.TryDequeue(out var k)) keys.Add(k);
        return keys;
    }

    internal void NoteDroppedIdle(in ThumbKey key)
    {
        // Bounded by the idle step itself (3,000 a list); a grid that never asks again is closed soon after.
        if (_droppedIdle.Count < 10_000) _droppedIdle.Enqueue(key);
    }

    internal void Deliver(ThumbArrival a)
    {
        if (_disposed)
        {
            a.Bitmap?.Dispose();
            return;
        }
        try
        {
            _onArrival(a);
        }
        catch
        {
            a.Bitmap?.Dispose();
        }
    }

    /// <summary>Drops this client's work; arrivals after this are disposed.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.DropClient(this);
        _service.RemoveClient(this);
    }
}

/// <summary>
/// Grid thumbnails: the ladder (the store on disk, Windows' cache, the previews inside files, the real thumbnail, the
/// icon), the lanes that run it and the order it runs in. See "How to use" at the top of this file. Thread-safe; owns
/// no UI. Its threads are background threads.
/// </summary>
internal sealed class ThumbnailService : IDisposable
{
    private static readonly Lazy<ThumbnailService> _shared = new(() => new ThumbnailService(SharedOptionsForProbes?.Invoke() ?? new ThumbServiceOptions()));

    /// <summary>
    /// For probes that host the program's grid only (never set by the program, its settings or its command line): the
    /// options <see cref="Shared"/> is made with, when set before its first use. On a desktop of their own Windows reports
    /// a full-screen program, which rightly pauses idle work; a probe gives the service a power policy of its own here to
    /// measure the idle step.
    /// </summary>
    internal static Func<ThumbServiceOptions>? SharedOptionsForProbes;

    /// <summary>
    /// For probes only (never set by the program): called on the worker at the start of each stage of a job, with the
    /// stage's name and the file's path, inside the watchdog's view. A probe makes a stage hang here (a Windows call that
    /// never returns can't be made on demand). One static read per stage while unset.
    /// </summary>
    internal static Action<string, string>? StageHookForProbes;

    /// <summary>
    /// For probes only (never set by the program): called by the watchdog when it has found overdue workers, before it
    /// gives any up, so a probe can let a job end and the next one begin in between.
    /// </summary>
    internal static Action? WatchdogHookForProbes;

    /// <summary>The program's service (created on first use, with no store until <see cref="UseStore"/>).</summary>
    public static ThumbnailService Shared => _shared.Value;

    [ThreadStatic] private static bool _onServiceThread;

    /// <summary>Whether the calling thread is one of a service's workers (STA; may run WPF work inline).</summary>
    public static bool IsServiceThread => _onServiceThread;

    // Shared by every service in the process: a stuck thread costs a thread for good, so the total is what counts.
    private static readonly AbandonBudget Budget = new(ThumbRules.MaxAbandonedThreads);

    // One suspect at a time in marked work, for the whole process (the crash marker is the process's): see SuspectTurn.
    private static readonly SuspectTurn Suspects = new();

    private enum Stage { Front, Probe, Decode, Shell, Icon }

    private enum LaneKind { Fast, Decode, Shell }

    /// <summary>One job of one client on its way down the ladder. Its stage queue (or the worker running it) owns it.</summary>
    private sealed class Work
    {
        private static long _ids;
        public readonly long Id = Interlocked.Increment(ref _ids);
        public readonly ThumbClient Client;
        public readonly ThumbJob Job;
        public ThumbPriority Priority;
        public long Rank;
        public long Seq;
        public Stage Stage;
        public ThumbKey RealKey;
        public bool Resolved, IsFolder, HadLow, Unclean;
        // NotHere: a cloud placeholder or offline file (only Windows may picture it). ShellTried, DecodeTried: which of
        // step 4's two sources answered already. WarmOnly: not a request but Windows asked to make and cache a picture.
        public bool NotHere, ShellTried, DecodeTried, WarmOnly;
        // CacheOnly: a folder, a shortcut or a PDF kind, whose picture Windows' extractor is never asked for in this
        // process, only Windows' thumbnail cache (see ThumbKinds.WindowsCacheOnly). Decided once, in Resolve.
        public bool CacheOnly;
        // NeedsGate: a picture too large to decode beside another was told the large-decode gate is held. It is not taken
        // again while the gate stays held (see DecodeMayTake): no thread waits for the gate, and the file isn't opened
        // again only to be told so.
        public bool NeedsGate, GateToShell;
        // Suspect: the store has no answer and says the program ended once while this file's picture was being made. Its
        // marked work runs only while it holds the one suspect turn (see SuspectMayGo), and it is cleared in the store
        // when the job ends normally. Not copied by CopyAt: a copy is made only of a job the watchdog gave up, which must
        // stay a suspect.
        public bool Suspect;
        public long DeferUntil;
        public long Length;
        public ThumbRoute Route;
        // The stand-in shown, kept until the job ends: when no source makes the real thumbnail it is the best there is,
        // and it is handed over once more as the answer (a caller waiting for one picture would wait for ever otherwise).
        public Bitmap? LowMaster;

        public void ReleaseLow()
        {
            Interlocked.Exchange(ref LowMaster, null)?.Dispose();
        }
        private int _state; // 0 waiting, 1 running, 2 given up by the watchdog

        public Work(ThumbClient client, ThumbJob job)
        {
            Client = client;
            Job = job;
            Priority = job.Priority;
            Rank = job.Rank;
            RealKey = job.Key;
        }

        /// <summary>A fresh copy at another stage (the original was given up while a thread was stuck in it).</summary>
        public Work CopyAt(Stage stage) => new(Client, Job)
        {
            Priority = Priority, Rank = Rank, Stage = stage, RealKey = RealKey, Resolved = Resolved, IsFolder = IsFolder,
            HadLow = HadLow, Unclean = true, Length = Length, Route = Route, LowMaster = Interlocked.Exchange(ref LowMaster, null),
            NotHere = NotHere, ShellTried = ShellTried, DecodeTried = DecodeTried, WarmOnly = WarmOnly, CacheOnly = CacheOnly,
        };

        public string Path => Job.Path;
        public int SizeClass => Job.Key.SizeClass;
        public bool BeginRun() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        public bool EndRun() => Interlocked.CompareExchange(ref _state, 0, 1) == 1;
        public bool GiveUp() => Interlocked.CompareExchange(ref _state, 2, 1) == 1;
    }

    /// <summary>What one stage made of a work item; the worker applies it once it knows the item is still its own.</summary>
    private sealed class Outcome
    {
        public Bitmap? Low;
        public Bitmap? Final;
        public bool StoreFinal;
        public ReadOnlyMemory<byte> Stored;
        public bool HasStored;
        public Stage? Next;
        public bool None;
        public bool NoneReady;
        // Skip: idle work for a kind never kept on disk (nothing to make). WarmWindows: made by Rock Preview's own
        // decoder with no store: Windows is asked to make it too, later, so its cache has it after a restart. TextPage:
        // the Final is a page drawn from the file's own text: shown, never kept on disk.
        public bool Skip, WarmWindows, TextPage;
        public Dictionary<int, Bitmap?>? Icons;

        public void DisposeAll()
        {
            Low?.Dispose();
            Final?.Dispose();
            if (Icons != null) foreach (var b in Icons.Values) b?.Dispose();
        }
    }

    /// <summary>Work items waiting for one stage, best first (priority, then place in the plan).</summary>
    private sealed class StageQueue
    {
        private readonly SortedSet<Work> _set = new(Comparer<Work>.Create(Compare));

        public int Count { get { lock (_set) return _set.Count; } }

        /// <summary>Whether idle work waits here (the set is ordered by priority, idle last).</summary>
        public bool HasIdle { get { lock (_set) return _set.Count > 0 && _set.Max!.Priority == ThumbPriority.Idle; } }

        public void Add(Work w) { lock (_set) _set.Add(w); }

        public bool TryTake(ThumbPriority worst, out Work w) => TryTake(worst, null, out w);

        /// <summary>The best item of priority <paramref name="worst"/> or better that <paramref name="accept"/> takes (called under the lock: quick).</summary>
        public bool TryTake(ThumbPriority worst, Func<Work, bool>? accept, out Work w)
        {
            lock (_set)
            {
                foreach (var item in _set)
                {
                    if (item.Priority > worst) break;
                    if (accept != null && !accept(item)) continue;
                    _set.Remove(item);
                    w = item;
                    return true;
                }
            }
            w = null!;
            return false;
        }

        /// <summary>A new plan for <paramref name="client"/>: items it holds move to their new place, the rest are dropped.</summary>
        public void Replan(ThumbClient client, Dictionary<ThumbKey, (ThumbPriority Priority, int Rank)>? plan, long seq, List<Work> dropped)
        {
            lock (_set)
            {
                var mine = _set.Where(w => w.Client == client).ToList();
                foreach (var w in mine)
                {
                    _set.Remove(w);
                    if (plan != null && plan.TryGetValue(w.Job.Key, out var place))
                    {
                        w.Priority = place.Priority;
                        w.Rank = place.Rank;
                        w.Seq = seq;
                        _set.Add(w);
                    }
                    else dropped.Add(w);
                }
            }
        }

        public List<Work> TakeAll()
        {
            lock (_set)
            {
                var all = _set.ToList();
                _set.Clear();
                return all;
            }
        }

        private static int Compare(Work? a, Work? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a is null) return -1;
            if (b is null) return 1;
            int c = a.Priority.CompareTo(b.Priority);
            if (c != 0) return c;
            c = a.Rank.CompareTo(b.Rank);
            if (c != 0) return c;
            c = a.Seq.CompareTo(b.Seq);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        }
    }

    /// <summary>One worker thread and what it is doing (the watchdog reads this).</summary>
    private sealed class Worker
    {
        public readonly LaneKind Lane;
        public int Index;
        public Thread? Thread;
        public volatile Work? Current;
        public long StartedAt;
        public volatile bool Abandoned;
        public int Slot = -1;
        public ThumbStore? SlotStore;

        public Worker(LaneKind lane, int index)
        {
            Lane = lane;
            Index = index;
        }
    }

    // The store used while none is chosen, or while the chosen one is still opening: stores nothing, touches no disk.
    private static readonly ThumbStore Off = ThumbStore.Disabled();
    // A decode thread waits this long for a store being opened (a filled store must answer the first screen); after that
    // it goes on as if there were none. Fast and shell threads never wait for it (their time-out would count it).
    private static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(2);
    // A chosen location that couldn't be used (a drive not there, another copy using it) is tried again this often.
    private const long RetryOpenMs = 30_000;

    private readonly ThumbServiceOptions _options;
    private readonly object _gate = new();          // clients, workers, which store to use; only ever held briefly
    private readonly object _wake = new();
    private readonly int _decodeThreads;
    private long _version;
    private readonly List<ThumbClient> _clients = new();
    private readonly List<Worker> _workers = new();
    private readonly StageQueue _probe = new(), _decode = new(), _shell = new(), _icon = new();
    private readonly SemaphoreSlim _largeGate = new(1, 1);
    // While jobs wait for the large-decode gate, a one-time wait on its handle wakes the workers when it is let go
    // (_gateWatched 1), and _gateHeldSince says since when it is known to be held (0: not known), so a holder that never
    // lets go doesn't keep wanted pictures waiting for ever (ThumbLanes.GateStuckMs).
    private int _gateWatched;
    private long _gateHeldSince;
    private readonly IconMemory _icons = new();
    private readonly HashSet<ThumbKey> _failed = new(); // timed out or failed for a reason that may pass: this run only
    // Without a store, every file with no picture lands in _failed, except a folder, shortcut or PDF not on this PC that
    // only missed Windows' cache (asked again later): past this many it starts again (a few MB at most).
    private const int MaxFailed = 20_000;
    // Files whose Windows call hung (a thread was given up for each): never forgotten in this run, also when _failed starts
    // again, since asking again would cost another thread. Few: each one spent a thread of a small budget.
    private readonly HashSet<ThumbKey> _hung = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _watchdog;
    private Func<ThumbStore>? _openStore;
    private volatile ThumbStore? _store;
    private int _storeGeneration;                   // counts UseStore calls, so a store opened for an old choice is closed again
    private System.Threading.Tasks.Task<bool>? _opening; // false if a store opened too late and its writer is still closing
    private System.Threading.Tasks.Task _openReturned = System.Threading.Tasks.Task.CompletedTask; // the open itself, before any close
    private long _openStartedAt = long.MinValue / 2, _openedAt;
    private System.Threading.Tasks.Task<bool> _closing = System.Threading.Tasks.Task.FromResult(true);
    // How long the closing of an old choice waits for a store still being opened for it (a drive asleep or gone).
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(5);
    // Pictures Rock Preview decoded with no store, for Windows to make and cache when nothing else waits (one shell thread).
    private readonly LinkedList<Work> _warm = new();
    private const int MaxWarm = 2000;
    private int _warmRunning;
    private volatile bool _warmStopped;              // a warming call hung once: no more warming in this run
    private int _pdfRendering;                      // a decode thread renders a PDF page (one at a time: the page renderer is one for the process)
    // Jobs drawing text now (text pages, a Word page, an Excel sheet page, a font sample, the text sniff). The font engine
    // is shared by every thread that draws text, and the grid's captions wait for it (0.1 ms each alone, 0.15 to 0.3 with
    // one worker drawing pages, 0.5 to 0.9 with three): while the view moves (scrolled in the last 300 ms) one such job
    // runs at a time, else three.
    private int _textJobs;
    private long _viewMovedAt = long.MinValue / 2;
    private long _capOverride = -1;
    private volatile ThumbKeep _keep = ThumbKeep.All;
    private long _seq;
    private int _roundRobin;
    private long _powerCheckedAt = long.MinValue;   // never: the first check asks the power policy
    // Whether idle work waits, as the power policy last answered: idle work waits until it first has (a moment).
    private volatile bool _idlePaused = true;
    private int _powerChecking;                     // 1 while a check runs on the thread pool
    private long _powerCheckStartedAt;
    // Wakes the watchdog when a fast or shell job starts: it only watches while one runs.
    private readonly AutoResetEvent _watch = new(false);
    // A lane whose last thread was given up with the budget spent: work sent to it wakes the watchdog, which applies the
    // lane's fallback (DrainDeadLanes). Set once, never cleared: a dead lane never comes back.
    private volatile bool _fastDead, _shellDead;
    // Set once a suspect was found in this run: only then do the fast and shell threads look at each job they take for
    // the suspect turn (one bool read per pick until then; the decode threads look in DecodeMayTake).
    private volatile bool _suspectsSeen;
    private readonly Func<Work, bool> _suspectMayGo;
    private volatile bool _disposed;
    private readonly ManualResetEventSlim _warmed = new(false);
    private int _warmLeft;

    // Counters (Interlocked).
    private long _storeHits, _storeNone, _storeMisses, _storeBytes, _cacheProbes, _cacheFinal, _cacheLow, _embeddedLows,
        _ownDecodes, _ownExtracts, _shellExtracts, _shellTimeouts, _textSniffs, _finalsStored, _nonesStored, _failures,
        _refused, _arrivedLow, _arrivedFinal, _arrivedNone, _dropped, _windowsWarms;

    /// <summary>A service with its own threads and (through <see cref="ThumbServiceOptions.OpenStore"/>) its own store.</summary>
    public ThumbnailService(ThumbServiceOptions? options = null)
    {
        _options = options ?? new ThumbServiceOptions();
        _openStore = _options.OpenStore;
        _decodeThreads = Math.Max(1, _options.DecodeThreads);
        _warmLeft = ThumbRules.FastLaneThreads + ThumbRules.ShellLaneThreads + 1;
        _suspectMayGo = SuspectMayGo;
        lock (_gate)
        {
            for (int i = 0; i < ThumbRules.FastLaneThreads; i++) StartWorker(LaneKind.Fast, i);
            for (int i = 0; i < _decodeThreads; i++) StartWorker(LaneKind.Decode, i);
            for (int i = 0; i < ThumbRules.ShellLaneThreads; i++) StartWorker(LaneKind.Shell, i);
        }
        _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = _options.Name + " watchdog" };
        _watchdog.Start();
    }

    // ── The public surface ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A new client for a grid; <paramref name="onArrival"/> is called on worker threads and owns each bitmap.</summary>
    public ThumbClient CreateClient(Action<ThumbArrival> onArrival)
    {
        lock (_gate)
        {
            var c = new ThumbClient(this, onArrival, allowIdle: KeepsStoreLocked);
            _clients.Add(c);
            return c;
        }
    }

    /// <summary>
    /// Whether pictures are kept on disk ("Instant thumbnails" on): a store was chosen with <see cref="UseStore"/>, and
    /// its location could be used (or it isn't open yet). A location that couldn't be used keeps the store idle: no idle
    /// step, whose pictures would have nowhere to go, until it is tried again and works.
    /// </summary>
    public bool KeepsStore { get { lock (_gate) return KeepsStoreLocked; } }

    private bool KeepsStoreLocked => _openStore != null && _store is not { IsEnabled: false };

    /// <summary>
    /// The store in use now, or null while none is open yet (it opens on the next request) or none is chosen. Settings
    /// reads <see cref="ThumbStore.Problem"/>, <see cref="ThumbStore.Folder"/> and <see cref="ThumbStore.SizeBytes"/> from
    /// it; a store that could not use its folder is here too, with IsEnabled false. Any thread; never waits.
    /// </summary>
    public ThumbStore? CurrentStore
    {
        get
        {
            // Without a chosen store a worker may still hold the stand-in (ThumbStore.Disabled): that is not a store.
            lock (_gate) return _openStore == null ? null : _store;
        }
    }

    /// <summary>
    /// Where pictures are kept on disk from now on: <paramref name="open"/> is called on the thread pool on the next
    /// request (null: no store, the normal way: nothing is written to disk and the idle step stops). Any thread; never
    /// waits. Requests in flight are not lost: a picture being made when the store changes goes to the store chosen now
    /// (its key is the same), or nowhere when there is none. The returned task ends when the store used until now is
    /// closed and a store still being opened for the old choice has opened and closed again. Only then may its folder be
    /// removed (<see cref="ThumbStore.RemoveFolder"/>). If an open times out or the writer hasn't let go of the folder
    /// within 10 s after disposal, the task returns false and the folder is left.
    /// </summary>
    public System.Threading.Tasks.Task<bool> UseStore(Func<ThumbStore>? open)
    {
        ThumbStore? old;
        List<ThumbClient> clients;
        System.Threading.Tasks.Task<bool> closed;
        lock (_gate)
        {
            old = _store;
            var opening = _opening;
            var openReturned = _openReturned;
            _store = null;
            _opening = null;
            _openStore = open;
            _storeGeneration++;
            _capOverride = -1; // the new store's own options carry its cap
            clients = _clients.ToList();
            if (old != null || opening != null || !_closing.IsCompleted)
            {
                // Closing writes what is queued: not on the caller's thread. A store still opening for the old choice
                // closes itself once open (it sees the choice changed); this waits for that too. A store opened next in
                // the same folder waits for all of it, since a folder serves one store at a time.
                var previous = _closing;
                closed = System.Threading.Tasks.Task.Run(() =>
                {
                    // A drive that never opens mustn't keep the option from being turned off. Once the open returns,
                    // its writer gets the full close wait, even if the drive used most of the time allowed to open.
                    try { previous.Wait(); } catch { /* only this store's close decides whether its folder may go */ }
                    bool done;
                    try { done = opening == null || (openReturned.Wait(CloseWait) && opening.Result); } catch { done = false; }
                    if (old != null) done &= CloseStore(old);
                    return done;
                });
                _closing = closed;
            }
            else closed = System.Threading.Tasks.Task.FromResult(true);
        }
        // Idle work only has a purpose with a store; turning it off drops the idle work not started (started jobs finish).
        foreach (var c in clients) c.Queue.AllowIdle = open != null;
        // With a store chosen, Windows' cache is not warmed any more: what waits for it goes.
        if (open != null) lock (_warm) _warm.Clear();
        Wake();
        return closed;
    }

    /// <summary>Empties the store and forgets the icons and failures remembered (off the calling thread).</summary>
    public void ClearStore()
    {
        lock (_failed) _failed.Clear();
        _icons.Clear();
        int generation;
        lock (_gate) generation = _storeGeneration;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var s = Store(TimeSpan.FromSeconds(10));
                // Another store chosen meanwhile: it is not the one the user asked to clear.
                lock (_gate) if (generation != _storeGeneration) return;
                s.Clear();
            }
            catch { /* the store never throws; belt and braces for a closed one */ }
        });
    }

    /// <summary>Forgets the icons and failures remembered in memory (the store is kept).</summary>
    public void ClearMemory()
    {
        lock (_failed) _failed.Clear();
        _icons.Clear();
    }

    /// <summary>What the store's packs take now, in bytes; -1 when the store isn't open yet (or there is none).</summary>
    public long StoreSizeBytes
    {
        get
        {
            var s = _store;
            return s != null && s.IsEnabled ? s.SizeBytes : -1;
        }
    }

    /// <summary>The store's cap in bytes (applies to the open store and to one opened later).</summary>
    public long StoreCapBytes
    {
        get
        {
            ThumbStore? s;
            lock (_gate) s = _store;
            return s?.CapBytes ?? _capOverride;
        }
        set
        {
            ThumbStore? s;
            lock (_gate)
            {
                _capOverride = value;
                s = _store;
            }
            if (s != null) s.CapBytes = value;
        }
    }

    /// <summary>
    /// Which thumbnails the store keeps, as the user chose (asked in addition to <see cref="ThumbKinds.KeepOnDisk"/>):
    /// pictures made from now on that it doesn't keep are shown but not saved, and the idle step skips them. A group
    /// switched off has its saved thumbnails deleted, in the background (a store opened later does it when it opens).
    /// Types left out are not deleted: the store doesn't keep types apart. Any thread; never waits.
    /// </summary>
    public ThumbKeep StoreKeep
    {
        get => _keep;
        set
        {
            var keep = value ?? ThumbKeep.All;
            var before = _keep;
            _keep = keep;
            if (_store is { IsEnabled: true } s) DropUnkept(s, keep, before);
        }
    }

    /// <summary>
    /// Deletes, on the thread pool, the saved thumbnails of each group <paramref name="keep"/> doesn't tick (only those
    /// <paramref name="before"/> ticked, when given). Skipped for a group ticked again meanwhile, or a store replaced.
    /// </summary>
    private void DropUnkept(ThumbStore store, ThumbKeep keep, ThumbKeep? before)
    {
        var groups = new List<ThumbKeepGroup>(3);
        foreach (var g in new[] { ThumbKeepGroup.Pictures, ThumbKeepGroup.Videos, ThumbKeepGroup.Documents })
            if (!keep.Has(g) && (before == null || before.Has(g))) groups.Add(g);
        if (groups.Count == 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (var g in groups)
            {
                if (_keep.Has(g) || !ReferenceEquals(_store, store)) continue;
                try { store.DropGroup(g); }
                catch { /* only on a fault such as memory running out: the next open of the store drops the group again */ }
            }
        });
    }

    /// <summary>
    /// The store in use started dropping thumbnails to stay within its limit. Raised on a worker thread (the
    /// store's writer or the thread pool), never under a lock of the service or the store: marshal to the UI thread. May
    /// be raised again for the same filling (once by the store, once when a store that opened full is published).
    /// </summary>
    public event Action? StoreBecameFull;

    /// <summary>Whether the store in use is full now (it drops thumbnails as new ones are saved). Any thread; cheap.</summary>
    public bool StoreIsFull => _store is { IsEnabled: true } s && s.IsFull;

    /// <summary>Whether the store in use refuses new pictures now because its drive is nearly full. Any thread; cheap.</summary>
    public bool StoreDiskLow => _store is { IsEnabled: true } s && s.DiskLow;

    private void RaiseStoreBecameFull()
    {
        try { StoreBecameFull?.Invoke(); }
        catch { /* a handler's fault must not stop the store's writer */ }
    }

    /// <summary>What the service did so far.</summary>
    public ThumbServiceStats Stats => new(
        Interlocked.Read(ref _storeHits), Interlocked.Read(ref _storeNone), Interlocked.Read(ref _storeMisses), Interlocked.Read(ref _storeBytes),
        Interlocked.Read(ref _cacheProbes), Interlocked.Read(ref _cacheFinal), Interlocked.Read(ref _cacheLow), Interlocked.Read(ref _embeddedLows),
        Interlocked.Read(ref _ownDecodes), Interlocked.Read(ref _ownExtracts), Interlocked.Read(ref _shellExtracts), Interlocked.Read(ref _shellTimeouts),
        Interlocked.Read(ref _textSniffs), Interlocked.Read(ref _icons.Fetched), Interlocked.Read(ref _icons.Remembered),
        Interlocked.Read(ref _finalsStored), Interlocked.Read(ref _nonesStored), Interlocked.Read(ref _failures), Interlocked.Read(ref _refused),
        Interlocked.Read(ref _arrivedLow), Interlocked.Read(ref _arrivedFinal), Interlocked.Read(ref _arrivedNone), Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _windowsWarms));

    /// <summary>
    /// Pays the first shell call of each fast and shell thread now (150 to 200 ms each, in parallel) and, when asked,
    /// opens the store, then waits up to <paramref name="max"/>. The threads warm by themselves when idle after
    /// start-up; this only makes it happen at once (the benchmark, or a program that knows it is idle). Blocks: any
    /// thread but the UI's.
    /// </summary>
    public bool WarmUp(TimeSpan max, bool openStore)
    {
        Wake();
        bool warmed = _warmed.Wait(max);
        if (openStore) Store(TimeSpan.FromSeconds(10));
        return warmed;
    }

    /// <summary>
    /// Starts opening the chosen store on the thread pool now, if one is chosen and none is open or opening yet, instead
    /// of on the first request (warming before the first Thumbnails view). Any thread; never waits.
    /// </summary>
    public void OpenStoreSoon()
    {
        if (!_disposed) Store(TimeSpan.Zero);
    }

    /// <summary>Pictures waiting for Windows to make and cache them (no store), or being made now (the benchmark waits for these).</summary>
    public int WarmPending
    {
        get { lock (_warm) return _warm.Count + Volatile.Read(ref _warmRunning); }
    }

    /// <summary>Stops the workers (a decode in progress finishes in the background) and closes the store (2 s at most).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        List<ThumbClient> clients;
        lock (_gate) clients = _clients.ToList();
        foreach (var c in clients) c.Queue.Clear();
        lock (_warm) _warm.Clear();
        Wake();
        // A decode still running when the process ends can take the process down inside the codec as the runtime shuts
        // down (seen once in the benchmark): give the work in progress a moment to finish. Stuck shell calls aren't waited for.
        var waited = Stopwatch.StartNew();
        while (waited.ElapsedMilliseconds < 1500)
        {
            bool busy;
            // Warming Windows' cache is never waited for: nobody asked for it.
            lock (_gate) busy = _workers.Any(x => !x.Abandoned && x.Current is { WarmOnly: false } && Environment.TickCount64 - x.StartedAt < 2000);
            if (!busy) break;
            Thread.Sleep(10);
        }
        ThumbStore? s;
        System.Threading.Tasks.Task closing;
        System.Threading.Tasks.Task? opening;
        lock (_gate)
        {
            s = _store;
            opening = _opening;
            _store = null;
            _opening = null;
            _openStore = null;
            _storeGeneration++;
            closing = _closing;
        }
        s?.Dispose();
        // A store being closed after a change of setting finishes its writing too, before the program goes, and one
        // still opening closes itself once open.
        try { closing.Wait(TimeSpan.FromSeconds(3)); } catch { /* it closed badly: nothing more to do */ }
        try { opening?.Wait(TimeSpan.FromSeconds(3)); } catch { /* it disposed what it opened */ }
    }

    // ── Requests ────────────────────────────────────────────────────────────────────────────────────────────────────

    internal void SubmitViewport(ThumbClient client, int listNumber, IReadOnlyList<ThumbRequest> ordered)
    {
        // Another viewport of the list already shown is a scroll (or a resize): the view moves. The first one of a list is
        // a screen being filled, not a scroll, and its text pages may be drawn three at a time.
        if (client.LastList == listNumber) Volatile.Write(ref _viewMovedAt, Environment.TickCount64);
        client.LastList = listNumber;
        client.Queue.SubmitViewport(listNumber, ordered);
        // Items already past the front stage but not started at their next one follow the new plan too; the rest are
        // dropped. A new list is no different: a job of the new plan may already have passed its front stage (a worker
        // took it just now), and an old-list job the new plan asks for again was joined by it in the queue.
        var plan = new Dictionary<ThumbKey, (ThumbPriority, int)>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++) plan.TryAdd(ordered[i].Key, (ordered[i].Priority, i));
        var dropped = new List<Work>();
        long seq = Interlocked.Increment(ref _seq);
        foreach (var q in new[] { _probe, _decode, _shell, _icon }) q.Replan(client, plan, seq, dropped);
        foreach (var w in dropped) Drop(w);
        Wake();
    }

    internal void DropClient(ThumbClient client)
    {
        client.Queue.Clear();
        var dropped = new List<Work>();
        foreach (var q in new[] { _probe, _decode, _shell, _icon }) q.Replan(client, null, 0, dropped);
        foreach (var w in dropped) Drop(w);
    }

    internal void RemoveClient(ThumbClient client)
    {
        lock (_gate) _clients.Remove(client);
    }

    private void Drop(Work w)
    {
        w.ReleaseLow();
        Interlocked.Increment(ref _dropped);
        if (w.Job.Priority == ThumbPriority.Idle) w.Client.NoteDroppedIdle(w.Job.Key);
        w.Client.Queue.Complete(w.Job);
    }

    internal void Wake()
    {
        lock (_wake)
        {
            _version++;
            Monitor.PulseAll(_wake);
        }
    }

    // ── Workers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private void StartWorker(LaneKind lane, int index)
    {
        var w = new Worker(lane, index);
        var t = new Thread(() => WorkerLoop(w))
        {
            IsBackground = true,
            Name = $"{_options.Name} {lane.ToString().ToLowerInvariant()} {index + 1}",
        };
        t.SetApartmentState(ApartmentState.STA);
        w.Thread = t;
        _workers.Add(w);
        t.Start();
    }

    private void WorkerLoop(Worker me)
    {
        _onServiceThread = true;
        if (me.Lane != LaneKind.Decode || me.Index == 0) WarmWhenIdle(me);
        while (!_disposed && !me.Abandoned)
        {
            long version;
            lock (_wake) version = _version;
            var work = Pick(me);
            if (work == null)
            {
                // With nothing queued anywhere a worker sleeps until woken (every submit, change and completion wakes it);
                // with work it may not take yet (a deferred job, one waiting for the text or PDF turn, a store opening)
                // it looks again within a second. Asked outside _wake: the queues' own locks are never taken inside it.
                bool waiting = HasAnyWork() || WarmWaiting;
                lock (_wake)
                {
                    if (_version == version && !_disposed) Monitor.Wait(_wake, waiting ? 1000 : Timeout.Infinite);
                }
                continue;
            }
            Run(me, work);
        }
        ReleaseSlot(me);
    }

    /// <summary>
    /// The first shell call in a thread costs 150 to 200 ms, and the first picture decoded in a process about 100 ms
    /// (WPF's imaging and GDI+ load, the code is compiled): pay both while nothing is asked, so a request doesn't. The
    /// fast and shell threads each make one cache-only shell call; the first decode thread decodes, scales and encodes
    /// a tiny picture and makes a key and a plan.
    /// </summary>
    private void WarmWhenIdle(Worker me)
    {
        bool busy = HasAnyWork();
        lock (_wake)
        {
            if (!busy) Monitor.Wait(_wake, 1000);
        }
        try
        {
            if (!_disposed && me.Lane == LaneKind.Decode)
            {
                using var tiny = new Bitmap(8, 8, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                var bytes = ThumbSources.Encode(tiny, ".jpg", ThumbSizeClass.Small, out _);
                if (bytes != null) ThumbSources.DecodeStored(bytes, 4)?.Dispose();
                // WPF's imaging too, which some sources still decode through (a PSD's thumbnail resource, an Office
                // file's own thumbnail): loading it costs 60 to 80 ms the first time, which a request shouldn't pay.
                if (bytes != null) BitmapUtil.Decode(bytes, new Size(4, 4))?.Dispose();
                ThumbSources.ToDisplay(tiny, 4).Dispose();
                var key = ThumbKey.Of(AppContext.BaseDirectory, new ThumbStamp(1, 1), ThumbSizeClass.Small);
                var scratch = new ThumbQueue();
                scratch.SubmitViewport(1, [new ThumbRequest(key.Path, key.Stamp, 16, ThumbPriority.Visible)]);
                _ = ThumbOrder.Viewport(1, 1, 0, 1, ScrollDirection.Down);
            }
            else if (!_disposed)
            {
                var bmp = Shell.GetImageSta(Environment.GetFolderPath(Environment.SpecialFolder.Windows), 16,
                    Shell.SIIGBF.ThumbnailOnly | Shell.SIIGBF.InCacheOnly, out _);
                bmp?.Dispose();
            }
        }
        catch
        {
            // only a warm-up
        }
        if (Interlocked.Decrement(ref _warmLeft) == 0) _warmed.Set();
    }

    private bool WarmWaiting
    {
        get { lock (_warm) return _warm.Count > 0; }
    }

    private bool HasAnyWork()
    {
        if (_probe.Count + _decode.Count + _shell.Count + _icon.Count > 0) return true;
        lock (_gate) return _clients.Any(c => c.Queue.PendingCount > 0);
    }

    private Work? Pick(Worker me)
    {
        // The cached answer only: the power policy is never asked on a worker (see IdlePaused).
        bool paused = IdlePaused();
        var worst = me.Lane switch
        {
            LaneKind.Decode => ThumbRules.WorstFor(me.Index, ThumbRules.IdleDecodeThreadsFor(_decodeThreads), paused),
            LaneKind.Shell => ThumbRules.WorstFor(me.Index, ThumbRules.IdleShellThreads, paused),
            _ => paused ? ThumbPriority.Behind : ThumbPriority.Idle,
        };
        // A fast thread doesn't take front work while the chosen store is still opening: it would have to wait for it
        // (and its time-out would count the wait) or miss a store that has the picture. Decode threads wait for it.
        bool fastFront = me.Lane != LaneKind.Fast || FastLaneMayTakeFront();
        long now = Environment.TickCount64;
        // A probe or shell job of a suspect does marked work (the preview inside the file, Windows' extractor): taken only
        // with the suspect turn. Nothing is looked at for it until a suspect has been seen in this run. The icon stage
        // marks nothing.
        var marked = _suspectsSeen ? _suspectMayGo : null;
        for (var p = ThumbPriority.Visible; p <= worst; p++)
        {
            Work? w;
            switch (me.Lane)
            {
                case LaneKind.Fast:
                    if (_icon.TryTake(p, out w) || _probe.TryTake(p, marked, out w) || (fastFront && TryTakeFront(p, out w))) return w;
                    break;
                case LaneKind.Decode:
                    // The front stage (the store) is cheap and turns into pictures at once: it goes first.
                    if (TryTakeFront(p, out w) || _decode.TryTake(p, x => DecodeMayTake(x, now), out w)) return w;
                    break;
                case LaneKind.Shell:
                    if (_shell.TryTake(p, marked, out w)) return w;
                    break;
            }
        }
        // Nothing else to do anywhere: the first shell thread has Windows make the pictures decoded with no store.
        if (me.Lane == LaneKind.Shell && me.Index < ThumbRules.IdleShellThreads && !paused && TryTakeWarm(out var warm)) return warm;
        return null;
    }

    /// <summary>
    /// Whether a decode thread may take <paramref name="w"/> now: not a job waiting for the large-decode gate while it is
    /// held (unless it has been held so long that the job goes to Windows' extractor: see <see cref="ThumbLanes.ForGateWaiter"/>),
    /// and not a second PDF page at once: the page renderer serves one page at a time for
    /// the whole process, the preview pane included, so a second decode thread would only wait in it. Under the stage
    /// queue's lock; taking a PDF job claims the renderer, which <see cref="RunDecode"/> gives back.
    /// </summary>
    private bool DecodeMayTake(Work w, long now)
    {
        // One field read for every other job; the gate's count is read only for the few that wait for it.
        if (w.NeedsGate)
        {
            bool held = _largeGate.CurrentCount == 0;
            if (held) WatchGate(now);
            var choice = ThumbLanes.ForGateWaiter(true, held, IdleOnly(w), w.ShellTried, Volatile.Read(ref _gateHeldSince), now, w.DeferUntil);
            if (choice == GateChoice.Wait) return false;
            if (choice == GateChoice.ToShell)
            {
                w.GateToShell = true; // no suspect turn was taken, so this worker must not decode even if the gate opens
                return true;
            }
        }
        if (w.Route != ThumbRoute.OwnExtractor) return SuspectMayGo(w);
        if (ThumbKinds.RendersPdfPage(w.Path))
        {
            if (Interlocked.CompareExchange(ref _pdfRendering, 1, 0) != 0) return false;
            if (SuspectMayGo(w)) return true;
            Volatile.Write(ref _pdfRendering, 0);
            return false;
        }
        // Text jobs: one at a time while the view moves (the grid paints its captions then), three when it is still.
        if (!ThumbKinds.DrawsTextWhenMade(w.Path)) return SuspectMayGo(w);
        if (!TryTakeTextTurn(now)) return false;
        if (SuspectMayGo(w)) return true;
        Interlocked.Decrement(ref _textJobs); // given back without the wake: nothing was held long enough to wait for
        return false;
    }

    /// <summary>
    /// Whether <paramref name="w"/> may do its marked work now: any job that isn't a suspect (one field read), and a suspect
    /// that takes the suspect turn (<see cref="SuspectTurn"/>); one that can't stays queued and is looked at again when the
    /// turn is given back (a wake) or within a second. Under a stage queue's lock (the turn's own lock is held for a
    /// moment). The turn is given back when the stage ends (<see cref="Run"/>) or the watchdog gives the job up.
    /// </summary>
    private bool SuspectMayGo(Work w) => !w.Suspect || Suspects.TryTake(w.Id, Environment.TickCount64);

    /// <summary>Gives back the suspect turn if <paramref name="w"/> holds it, and wakes the workers when it did (a suspect may wait).</summary>
    private void EndSuspectTurn(Work w)
    {
        if (Suspects.Release(w.Id)) Wake();
    }

    /// <summary>A turn to draw text (see <see cref="_textJobs"/>); give it back with <see cref="EndTextTurn"/>.</summary>
    private bool TryTakeTextTurn(long now)
    {
        int limit = now - Volatile.Read(ref _viewMovedAt) < ThumbRules.TextViewMovedMs ? 1 : ThumbRules.TextJobsWhenStill;
        while (true)
        {
            int n = Volatile.Read(ref _textJobs);
            if (n >= limit) return false;
            if (Interlocked.CompareExchange(ref _textJobs, n + 1, n) == n) return true;
        }
    }

    private void EndTextTurn()
    {
        Interlocked.Decrement(ref _textJobs);
        Wake(); // a text job waiting for its turn may go
    }

    /// <summary>
    /// A job was told the large-decode gate is held: notes since when (if nobody has yet) and has the thread pool wake the
    /// workers once it is let go, so the jobs waiting for it are taken again at once. Costs a pool wait registration once
    /// per holder, and only while large pictures wait; a gate already free fires it at once.
    /// </summary>
    private void WatchGate(long now)
    {
        if (Volatile.Read(ref _gateWatched) != 0 || Interlocked.CompareExchange(ref _gateWatched, 1, 0) != 0) return;
        Volatile.Write(ref _gateHeldSince, now);
        try
        {
            ThreadPool.UnsafeRegisterWaitForSingleObject(_largeGate.AvailableWaitHandle, (_, _) => GateFreed(), null, Timeout.Infinite, executeOnlyOnce: true);
        }
        catch
        {
            // No watch: the workers still look again within a second (WorkerLoop), and the next job told so tries again.
            GateFreed();
        }
    }

    private void GateFreed()
    {
        // In this order: a job told the gate is held after this sets a new start, which this must not wipe out.
        Volatile.Write(ref _gateHeldSince, 0);
        Volatile.Write(ref _gateWatched, 0);
        Wake();
    }

    /// <summary>Whether a fast thread may take front work: the store chosen is open (or none is), or has taken too long to open.</summary>
    private bool FastLaneMayTakeFront()
    {
        if (_store != null) return true;
        lock (_gate)
        {
            if (_openStore == null || _store != null) return true;
        }
        Store(TimeSpan.Zero); // starts opening it, if nobody has yet
        return Environment.TickCount64 - Volatile.Read(ref _openStartedAt) > (long)OpenWait.TotalMilliseconds;
    }

    private bool TryTakeWarm(out Work w)
    {
        lock (_warm)
        {
            if (_warm.First is { } first && !_warmStopped && NoStoreChosen && !HasAnyWork())
            {
                _warm.RemoveFirst();
                Interlocked.Increment(ref _warmRunning);
                w = first.Value;
                return true;
            }
        }
        w = null!;
        return false;
    }

    /// <summary>
    /// With no store, a picture Rock Preview decoded itself is asked of Windows' extractor later, when nothing else waits,
    /// so Windows' own cache has it the next time the program starts (Rock Preview keeps nothing on disk itself then).
    /// The newest are kept when too many wait.
    /// </summary>
    private void EnqueueWarm(Work w)
    {
        // No shell thread left: nothing would ever take it (and the benchmark would wait for it).
        if (!_options.WarmWindowsCache || _disposed || _warmStopped || _shellDead || !NoStoreChosen) return;
        var warm = new Work(w.Client, w.Job)
        {
            Stage = Stage.Shell, WarmOnly = true, Priority = ThumbPriority.Idle, Resolved = true, RealKey = w.RealKey, Route = w.Route,
        };
        lock (_warm)
        {
            _warm.AddLast(warm);
            while (_warm.Count > MaxWarm) _warm.RemoveFirst();
        }
        Wake();
    }

    /// <summary>
    /// Whether no store is chosen ("Instant thumbnails" off): only then is Windows' cache warmed. Not whether a store is
    /// open: one still opening, or at a location that can't be used, leaves the option on, and nothing may then be
    /// written to Windows' cache on Rock Preview's behalf.
    /// </summary>
    private bool NoStoreChosen => Volatile.Read(ref _openStore) == null;

    private bool TryTakeFront(ThumbPriority p, out Work w)
    {
        ThumbClient[] clients;
        lock (_gate) clients = _clients.ToArray();
        int n = clients.Length;
        int start = n == 0 ? 0 : (int)((uint)Interlocked.Increment(ref _roundRobin) % (uint)n);
        for (int k = 0; k < n; k++)
        {
            var c = clients[(start + k) % n];
            if (c.IsDisposed) continue;
            if (c.Queue.TryTake(p, null, out var job))
            {
                w = new Work(c, job) { Stage = Stage.Front, Seq = Interlocked.Increment(ref _seq) };
                return true;
            }
        }
        w = null!;
        return false;
    }

    /// <summary>
    /// Whether idle work waits now (Battery Saver, full screen, a low disk), from the last answer of the power policy. The
    /// policy itself is asked on the thread pool, by one caller at a time, every 2 s at most and only while idle work or
    /// warming waits: it reads the store's disk, which may be asleep or on the network, and no worker (the pictures on
    /// screen above all) may wait for that. A check that hasn't answered within 4 s pauses idle work, nothing else.
    /// </summary>
    private bool IdlePaused()
    {
        long now = Environment.TickCount64;
        if (Volatile.Read(ref _powerChecking) == 1)
            return now - Volatile.Read(ref _powerCheckStartedAt) > ThumbRules.PowerCheckGiveUpMs || _idlePaused;
        if (ThumbRules.CheckDue(now, Volatile.Read(ref _powerCheckedAt), ThumbRules.IdleCheckMs) && IdleWorkWaits()
            && Interlocked.CompareExchange(ref _powerChecking, 1, 0) == 0)
        {
            Volatile.Write(ref _powerCheckStartedAt, now);
            ThreadPool.UnsafeQueueUserWorkItem(_ => CheckPower(), null);
        }
        return _idlePaused;
    }

    private void CheckPower()
    {
        bool paused;
        try
        {
            ThumbStore? s;
            lock (_gate) s = _store;
            // Idle work fills the store; without one, the only idle work is warming Windows' thumbnail cache, which Windows
            // keeps under the user's local application data: the disk that counts is that one.
            string folder = s != null && s.IsEnabled ? s.Folder : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            paused = ThumbRules.IdleWorkPaused(_options.Power.Now(folder));
        }
        catch
        {
            paused = false;
        }
        bool was = _idlePaused;
        _idlePaused = paused;
        Volatile.Write(ref _powerCheckedAt, Environment.TickCount64);
        Volatile.Write(ref _powerChecking, 0);
        if (was && !paused) Wake(); // idle work may go now
    }

    /// <summary>Whether idle work or warming waits anywhere (only then is the power policy asked).</summary>
    private bool IdleWorkWaits()
    {
        lock (_warm) if (_warm.Count > 0) return true;
        if (_decode.HasIdle || _shell.HasIdle || _probe.HasIdle) return true;
        ThumbClient[] clients;
        lock (_gate) clients = _clients.ToArray();
        foreach (var c in clients) if (c.Queue.HasIdleWaiting) return true;
        return false;
    }

    private void Run(Worker me, Work w)
    {
        if (!w.BeginRun())
        {
            if (w.Suspect) EndSuspectTurn(w); // taken with the job (SuspectMayGo), not used
            return;
        }
        try { Thread.CurrentThread.Priority = w.Priority == ThumbPriority.Visible ? ThreadPriority.Normal : ThreadPriority.BelowNormal; } catch { }
        Volatile.Write(ref me.StartedAt, Environment.TickCount64);
        me.Current = w;
        if (me.Lane != LaneKind.Decode) _watch.Set();
        if (w.WarmOnly)
        {
            RunWarm(w);
            me.Current = null;
            // Given up by the watchdog meanwhile: it counted the item off already.
            if (w.EndRun()) Interlocked.Decrement(ref _warmRunning);
            return;
        }
        Outcome o;
        try
        {
            StageHookForProbes?.Invoke(w.Stage.ToString(), w.Path);
            o = w.Stage switch
            {
                Stage.Front => RunFront(me, w),
                Stage.Probe => RunProbe(me, w),
                Stage.Decode => RunDecode(me, w),
                Stage.Shell => RunShell(me, w),
                _ => RunIcon(w),
            };
        }
        catch (Exception e)
        {
            // A stage that threw: the picture can't be made now, and maybe later (a transient failure) or never. The icon
            // stage is the last: it ends without an icon rather than be run again (and fail again) for ever.
            if (ThumbSources.IsTransient(e)) w.Unclean = true;
            o = w.Stage == Stage.Icon ? new Outcome { NoneReady = true } : new Outcome { None = true };
        }
        me.Current = null;
        // The stage's marked work is over: the suspect turn goes back (a no-op when this job doesn't hold it).
        if (w.Suspect) EndSuspectTurn(w);
        bool mine = w.EndRun();
        bool answered = false;
        try
        {
            answered = Apply(me, w, o, mine);
        }
        catch (Exception)
        {
            // Nothing a worker does with a finished picture may end the program (it runs on a thread nothing catches
            // for): this job ends without a picture, this time.
            o.DisposeAll();
            w.ReleaseLow();
            Interlocked.Increment(ref _failures);
            RememberFailed(w.RealKey);
            if (mine && w.Client.Queue.IsRunning(w.Job))
            {
                foreach (int size in w.Client.Queue.Complete(w.Job))
                    w.Client.Deliver(new ThumbArrival(w.Job.Key, w.Path, size, null, ThumbQuality.None, w.Job.ListNumber, w.Job.Index));
                answered = !w.Client.IsDisposed;
            }
        }
        // A suspect whose job has ended here (not sent on to another stage) without the program ending, and that the
        // watchdog didn't give up: its picture can be made, so the suspicion goes, whatever the result (also a kind never
        // kept on disk, which no Put would ever replace). Only at the job's end: a suspect cleared after an early stage
        // would make its later stages beside other files again. One store lookup, for suspects only.
        if (answered && w.Suspect)
        {
            w.Suspect = false;
            Store().ClearSuspect(w.RealKey);
        }
    }

    /// <summary>Windows' extractor makes (and caches) a picture Rock Preview decoded with no store; the picture itself isn't wanted.</summary>
    private void RunWarm(Work w)
    {
        try
        {
            Interlocked.Increment(ref _windowsWarms);
            ThumbSources.ShellExtract(w.Path, w.SizeClass, out _)?.Dispose();
        }
        catch
        {
            // only warming
        }
    }

    // ── The stages ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Front: the file's real stamp when needed, then the store (step 2), then on to the probes or the real thumbnail.</summary>
    private Outcome RunFront(Worker me, Work w)
    {
        if (!Resolve(w)) return new Outcome { None = true };
        // Idle work for a kind never kept on disk (text pages) has nothing to make: it only fills the store.
        // So is idle work for a kind the user chose not to keep, and for a kind the full store would not save now.
        if (IdleOnly(w) && (!ThumbKinds.KeepOnDisk(w.Path, w.IsFolder) || !_keep.Keeps(w.Path, w.IsFolder)
            || _store is { } refusing && refusing.Refuses(ThumbKeep.GroupOf(w.Path, w.IsFolder)))) return new Outcome { Skip = true };
        lock (_failed)
        {
            if (_failed.Contains(w.RealKey) || _hung.Contains(w.RealKey)) return new Outcome { Next = Stage.Icon };
        }
        // A decode thread waits a moment for a store still opening; a fast thread never gets here then (see Pick).
        var store = Store(me.Lane == LaneKind.Decode ? OpenWait : TimeSpan.Zero);
        if (store.TryGet(w.RealKey, out var thumb))
        {
            if (thumb.HasPicture)
            {
                Interlocked.Increment(ref _storeHits);
                Interlocked.Add(ref _storeBytes, thumb.Payload.Length);
                return new Outcome { Stored = thumb.Payload, HasStored = true };
            }
            // None (nothing can make a picture) or Crashed (making it once killed the program): the icon.
            Interlocked.Increment(ref _storeNone);
            return new Outcome { Next = Stage.Icon };
        }
        Interlocked.Increment(ref _storeMisses);
        string ext = Ext(w);
        // A miss may be a suspect (one lookup, on misses only). Its marked work waits for the suspect turn: taken here for
        // the preview inside the file; when another suspect holds it, the job goes on without that preview to a queued
        // stage, which takes it with the turn (the probe stage reads the preview, so a kind that has one goes there).
        if (store.IsSuspect(w.RealKey))
        {
            w.Suspect = true;
            _suspectsSeen = true;
            if (!SuspectMayGo(w))
            {
                bool hasPreview = !IdleOnly(w) && !w.IsFolder && !w.NotHere && ThumbKinds.EmbeddedPreviewOf(ext) != EmbeddedPreview.None;
                return new Outcome
                {
                    // (A suspect means an open store, so the no-store rule for Windows' cache below doesn't apply.)
                    Next = hasPreview || ThumbKinds.AskWindowsCache(ext, w.Length, w.IsFolder) ? Stage.Probe : RouteStage(w),
                };
            }
        }
        // Without a store Windows' cache is the only thing that outlives the process, so pictures of every size ask it
        // too: measured on a second visit in a new process, a screen of 144 mostly small pictures took about 690 ms when
        // they were decoded again and about 430 ms with this rule (today's way, Windows' cache only, about 390 ms), and a
        // miss costs a few milliseconds on a lane the decoders don't wait for. With a
        // store, a picture seen once comes from the store, and small pictures go straight to the decoder as designed.
        if (ThumbKinds.AskWindowsCache(ext, w.Length, w.IsFolder) || (!store.IsEnabled && w.Route == ThumbRoute.Picture))
        {
            // Windows' cache is asked on the fast lane only (it is a shell call); a fast thread does it right away.
            if (me.Lane == LaneKind.Fast) return RunProbe(me, w);
            return new Outcome { Next = Stage.Probe };
        }
        var o = new Outcome { Next = RouteStage(w) };
        if (!IdleOnly(w)) o.Low = EmbeddedLow(me, w); // nobody sees idle work's stand-in
        return o;
    }

    /// <summary>
    /// Whether a job is idle warming only: nobody is looking at it, so what it makes goes to the store and nowhere else.
    /// Read live: a viewport that asks for it while it runs makes it wanted on screen (<see cref="ThumbQueue"/>).
    /// </summary>
    private static bool IdleOnly(Work w) => w.Job.Priority == ThumbPriority.Idle;

    /// <summary>Probe: Windows' thumbnail cache only, then the preview inside the file; a Low is shown at once.</summary>
    private Outcome RunProbe(Worker me, Work w)
    {
        var o = new Outcome { Next = RouteStage(w) };
        Interlocked.Increment(ref _cacheProbes);
        var bmp = ThumbSources.CacheProbe(w.Path, w.SizeClass, out int hr);
        if (bmp != null && !DiscardBlankOfficePicture(w, bmp))
        {
            int longSide = Math.Max(bmp.Width, bmp.Height);
            if (ThumbKinds.QualityOf(longSide, w.SizeClass) != ThumbQuality.Final)
            {
                Interlocked.Increment(ref _cacheLow);
                if (IdleOnly(w)) bmp.Dispose();
                else o.Low = bmp;
                return o;
            }
            Interlocked.Increment(ref _cacheFinal);
            return new Outcome { Final = ThumbSources.FitClass(bmp, w.SizeClass), StoreFinal = true };
        }
        if (!IdleOnly(w)) o.Low = EmbeddedLow(me, w);
        return o;
    }

    private Bitmap? EmbeddedLow(Worker me, Work w)
    {
        // A file whose contents aren't on this PC would be downloaded to read its preview.
        if (w.IsFolder || w.NotHere) return null;
        var kind = ThumbKinds.EmbeddedPreviewOf(Ext(w));
        if (kind == EmbeddedPreview.None) return null;
        try
        {
            var bmp = Marked(me, w, () => ThumbSources.Embedded(w.Path, kind, w.SizeClass));
            if (bmp != null) Interlocked.Increment(ref _embeddedLows);
            return bmp;
        }
        catch (Exception e)
        {
            // A stand-in that can't be read is no loss: the real thumbnail is made next.
            if (ThumbSources.IsTransient(e)) w.Unclean = true;
            return null;
        }
    }

    /// <summary>Decode: Rock Preview's own decoder or extractor at the size class (step 4); nothing goes to Windows' extractor.</summary>
    private Outcome RunDecode(Worker me, Work w)
    {
        string ext = Ext(w);
        int cls = w.SizeClass;
        Bitmap? bmp = null;
        bool pdf = w.Route == ThumbRoute.OwnExtractor && ThumbKinds.RendersPdfPage(ext); // claimed the page renderer (DecodeMayTake)
        bool text = w.Route == ThumbRoute.OwnExtractor && !pdf && ThumbKinds.DrawsTextWhenMade(ext); // took a text turn
        w.DecodeTried = true;
        try
        {
            switch (w.Route)
            {
                case ThumbRoute.Picture:
                {
                    // The gate was stuck at dequeue. The decode attempt ends here, so a Windows miss finishes the job
                    // with its stand-in or icon. The decoder never got the gate, so the miss isn't kept on disk.
                    if (w.GateToShell)
                    {
                        w.DecodeTried = false;
                        w.Unclean = true;
                        return new Outcome { Next = Stage.Shell };
                    }
                    Interlocked.Increment(ref _ownDecodes);
                    WicImage.ThumbStatus status = WicImage.ThumbStatus.NoCodec;
                    // No job waits for the large-decode gate on a thread, a visible one neither: told it is held, the job
                    // goes back to the queue and isn't taken again until the gate is let go (DecodeMayTake), so small
                    // pictures and documents keep flowing on every decode thread while large ones wait their turn.
                    bmp = Marked(me, w, () => WicImage.DecodeThumbnail(w.Path, ext, cls, _largeGate, out status, out _, false));
                    if (status == WicImage.ThumbStatus.Busy)
                    {
                        long now = Environment.TickCount64;
                        Interlocked.Decrement(ref _ownDecodes); // not decoded: asked again later
                        w.DecodeTried = false;
                        w.NeedsGate = true;
                        w.DeferUntil = now + ThumbLanes.GateRetryMs;
                        WatchGate(now);
                        return new Outcome { Next = Stage.Decode };
                    }
                    w.NeedsGate = false;
                    if (status == WicImage.ThumbStatus.Refused) Interlocked.Increment(ref _refused);
                    break;
                }
                case ThumbRoute.Psd:
                {
                    Interlocked.Increment(ref _ownDecodes);
                    bool fromResource = false;
                    bmp = Marked(me, w, () => PsdDecoder.Decode(w.Path, new Size(cls, cls), _stop.Token, out _, out fromResource));
                    // The resource was the stand-in: it is not the Final.
                    if (fromResource) { bmp?.Dispose(); bmp = null; }
                    break;
                }
                default:
                    Interlocked.Increment(ref _ownExtracts);
                    bmp = Marked(me, w, () => ThumbnailProvider.ExtractAsync(w.Path, ext, cls, _stop.Token).GetAwaiter().GetResult());
                    break;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Cancelled because the service is closing: no fact about the file, so "no picture" isn't stored for it.
            w.Unclean = true;
            return new Outcome { None = true };
        }
        catch (Exception e)
        {
            if (ThumbSources.IsTransient(e))
            {
                w.Unclean = true;
                return new Outcome { None = true };
            }
            bmp = null; // the file isn't readable as its kind: Windows may still manage
        }
        finally
        {
            if (pdf) Volatile.Write(ref _pdfRendering, 0);
            if (text) EndTextTurn();
        }
        if (bmp != null)
        {
            // With no store chosen, a picture asked for the screen or the look-ahead is asked of Windows too, later, so
            // its cache has it after a restart (as 1.7.27's thumbnails were).
            bool warm = w.Route == ThumbRoute.Picture && NoStoreChosen && !IdleOnly(w);
            // A page of the file's own text or cells (a text kind, or a Word or Excel file without a thumbnail of its own,
            // old .doc and .xls included) is never kept.
            bool textPage = ThumbKinds.DrawsText(ext) || ThumbnailProvider.IsTextPage(bmp);
            return new Outcome { Final = ThumbSources.FitClass(bmp, cls), StoreFinal = true, WarmWindows = warm, TextPage = textPage };
        }
        // No codec, refused as too big, or our extractor had nothing: Windows' extractor is next, unless it answered already.
        return w.ShellTried ? new Outcome { None = true } : new Outcome { Next = Stage.Shell };
    }

    /// <summary>
    /// Shell: Windows' extractor at the size class (for folders, shortcuts and the PDF kinds its thumbnail cache only:
    /// <see cref="RunCacheOnly"/>), then (for kinds Rock Preview can't decode) the text sniff.
    /// </summary>
    private Outcome RunShell(Worker me, Work w)
    {
        w.ShellTried = true;
        if (w.CacheOnly) return RunCacheOnly(w);
        Interlocked.Increment(ref _shellExtracts);
        int hr = 0;
        var bmp = Marked(me, w, () => ThumbSources.ShellExtract(w.Path, w.SizeClass, out hr));
        if (bmp != null)
        {
            if (!DiscardBlankOfficePicture(w, bmp))
                return new Outcome { Final = ThumbSources.FitClass(bmp, w.SizeClass), StoreFinal = true };
        }
        else if (!ThumbRules.IsCleanShellNone(hr)) w.Unclean = true;
        // A kind asked of Windows first (fonts, old Word and Excel files): Rock Preview's own extractor is next.
        if (ThumbLanes.DecodeAfterShellMiss(w.Route, w.DecodeTried, w.GateToShell)) return new Outcome { Next = Stage.Decode };
        // The text sniff reads the file: not one whose contents would be downloaded.
        if (w.Route == ThumbRoute.Shell && !w.IsFolder && !w.NotHere)
        {
            // A text page is never kept on disk, so idle work has nothing to make here (and "no picture" isn't stored
            // for a file that may well be text when someone looks at it).
            if (IdleOnly(w)) return new Outcome { Skip = true };
            Interlocked.Increment(ref _textSniffs);
            // A sniff counts as a text job too. It doesn't wait for a turn (it runs on a shell thread, after Windows found
            // nothing, which is rare); counted, it makes the decode threads' text jobs wait for it.
            Interlocked.Increment(ref _textJobs);
            try
            {
                var page = ThumbSources.TextSniff(w.Path, w.SizeClass);
                if (page != null) return new Outcome { Final = page, StoreFinal = true, TextPage = true };
            }
            catch (Exception e)
            {
                if (ThumbSources.IsTransient(e)) w.Unclean = true;
            }
            finally
            {
                EndTextTurn();
            }
        }
        return new Outcome { None = true };
    }

    /// <summary>
    /// The shell stage for a folder, a shortcut or a PDF kind: Windows' thumbnail cache only, never its extractor, which
    /// would load another program's PDF handler into this process, and that can end it however it is asked (see
    /// <see cref="ThumbKinds.WindowsCacheOnly"/>). Not marked: a cache read leaves no in-flight record that a crash
    /// elsewhere would turn into a suspect. A picture of the class's size is the Final, judged as the probe stage judges
    /// Windows' cache (<see cref="ThumbKinds.QualityOf"/>); a smaller one is only a stand-in, shown and never stored. Then
    /// a PDF kind goes on to Rock Preview's own extractor; a file only Windows can picture (a folder, a shortcut, a PDF not
    /// on this PC) finishes with that stand-in or its icon. A miss in the cache marks every such job unclean, so "no
    /// picture" is never stored for it for good. No text sniff for these files.
    /// </summary>
    private Outcome RunCacheOnly(Work w)
    {
        var bmp = ThumbSources.CacheOnlyExtract(w.Path, w.SizeClass, out _);
        Bitmap? low = null;
        if (bmp != null)
        {
            if (ThumbKinds.QualityOf(Math.Max(bmp.Width, bmp.Height), w.SizeClass) == ThumbQuality.Final)
                return new Outcome { Final = ThumbSources.FitClass(bmp, w.SizeClass), StoreFinal = true };
            // Nobody sees idle work's stand-in; a job that shows one already keeps it (its size isn't read: another
            // thread may be disposing it).
            if (IdleOnly(w) || Volatile.Read(ref w.LowMaster) != null) bmp.Dispose();
            else low = bmp;
        }
        // A miss in Windows' cache is no fact about the file, also for a PDF kind that goes on to the own extractor:
        // otherwise a PDF or Illustrator file it can't draw would be stored as "no picture" for good after only the cache
        // was asked, and a picture File Explorer later puts into that cache would never be shown. One more render try in
        // a later run is the smaller cost.
        w.Unclean = true;
        if (ThumbLanes.DecodeAfterShellMiss(w.Route, w.DecodeTried, w.GateToShell)) return new Outcome { Next = Stage.Decode, Low = low };
        return new Outcome { None = true, Low = low };
    }

    /// <summary>Icon: the file's icon for each size wanted so far (step 5).</summary>
    private Outcome RunIcon(Work w)
    {
        var icons = new Dictionary<int, Bitmap?>();
        foreach (int size in w.Client.Queue.SizesOf(w.Job))
            icons[size] = FetchIcon(w, size);
        return new Outcome { NoneReady = true, Icons = icons };
    }

    /// <summary>
    /// The icon for a "no picture" answer (fast threads only: a shell call). When none comes back it is asked once more,
    /// past the memory, so the answer doesn't leave as an empty frame when Windows can give one. That costs one shell call,
    /// and only for an answer that would otherwise have no icon.
    /// </summary>
    private Bitmap? FetchIcon(Work w, int size)
        => _icons.Get(w.Path, w.IsFolder, size) ?? _icons.Refetch(w.Path, size);

    /// <summary>Runs a decode or extraction with the key in this thread's in-flight slot, so a crash inside it is remembered.</summary>
    private T Marked<T>(Worker me, Work w, Func<T> f)
    {
        var store = Store(); // never waits: a store still opening marks nothing this time
        if (me.SlotStore != store)
        {
            ReleaseSlot(me);
            me.Slot = store.Marker.Acquire();
            me.SlotStore = store;
        }
        if (me.Slot < 0) return f();
        uint epoch = store.Marker.Enter(me.Slot, w.RealKey);
        try { return f(); }
        finally { store.Marker.Leave(me.Slot, epoch); }
    }

    private static void ReleaseSlot(Worker me)
    {
        if (me.SlotStore != null && me.Slot >= 0)
        {
            try { me.SlotStore.Marker.Release(me.Slot); } catch { /* a closed marker */ }
        }
        me.Slot = -1;
        me.SlotStore = null;
    }

    private static string Ext(Work w) => w.IsFolder ? "" : PreviewRenderer.GetExtension(w.Path);

    private static bool DiscardBlankOfficePicture(Work w, Bitmap bmp)
    {
        if (w.IsFolder || !ThumbGuards.IsOfficeDocument(Path.GetExtension(w.Path.AsSpan()))) return false;

        // Windows can return the template's empty page from either its cache or its extractor.
        try { if (!ThumbBlank.IsBlank(bmp)) return false; }
        catch { bmp.Dispose(); throw; }
        bmp.Dispose();
        return true;
    }

    // Step 4's first source: Windows for its own kinds and for the kinds asked of it first (the PDF kinds, fonts, old Word
    // and Excel files), else Rock Preview's own decoder or extractor. For folders, shortcuts and the PDF kinds Windows
    // means its thumbnail cache only (RunCacheOnly).
    private static Stage RouteStage(Work w)
        => w.Route == ThumbRoute.Shell || (w.Route == ThumbRoute.OwnExtractor && !w.ShellTried && ThumbKinds.ShellFirst(w.Path))
            ? Stage.Shell : Stage.Decode;

    /// <summary>
    /// The real key, from the file as it is now (a stat on this worker costs microseconds): the index can lag behind an
    /// edit (a network drive rescanned now and then), and a stored picture must never outlive the file it shows. The
    /// arrival still carries the key the request made. A file whose contents aren't on this PC goes to Windows only.
    /// </summary>
    private static bool Resolve(Work w)
    {
        if (w.Resolved) return true;
        var given = w.Job.Stamp;
        if (!ThumbSources.Stat(w.Path, out var stamp, out bool folder, out bool notHere)) return false;
        w.IsFolder = folder;
        w.CacheOnly = ThumbKinds.WindowsCacheOnly(w.Path, folder);
        w.NotHere = notHere;
        w.Length = stamp.Length;
        w.RealKey = !given.IsUnknown && given == stamp ? w.Job.Key : w.Job.Key.WithStamp(stamp);
        w.Route = notHere ? ThumbRoute.Shell : ThumbKinds.RouteOf(w.Path, w.IsFolder);
        w.Resolved = true;
        return true;
    }

    // ── Applying an outcome ─────────────────────────────────────────────────────────────────────────────────────────

    // True only when this stage finished the job with an answer. A dropped or requeued suspect keeps its record.
    private bool Apply(Worker me, Work w, Outcome o, bool mine)
    {
        // A good Final is stored even when the item was given up meanwhile: the work is done, keep it (not a text page).
        // When it is shown, it is handed over first and stored after (see the Final's branch below).
        bool keep = o.Final != null && o.StoreFinal && !o.TextPage;
        if (!mine)
        {
            // Given up by the watchdog meanwhile: it finished the item already.
            if (keep) StoreFinal(w, o.Final!, me.Lane);
            o.DisposeAll();
            return false;
        }
        if (w.Client.IsDisposed)
        {
            if (keep) StoreFinal(w, o.Final!, me.Lane);
            o.DisposeAll();
            Drop(w);
            return false;
        }
        if (IdleOnly(w))
        {
            // Idle work only fills the store: what it made is kept there (above), nobody is shown it, a stored picture
            // isn't decoded again, and no icon is fetched. Only a source still to ask carries on.
            bool more = o.Next is Stage further && further != Stage.Icon && o.Final == null && !o.HasStored && !o.None && !o.NoneReady && !o.Skip;
            if (more)
            {
                o.DisposeAll();
                w.ReleaseLow();
                w.Stage = o.Next!.Value;
                Enqueue(w);
                return false;
            }
            // Finished as idle work only if nobody asked for it on screen in the meantime (checked and finished at once);
            // else it is answered as wanted work below, with what it made.
            if (w.Client.Queue.CompleteIfIdle(w.Job))
            {
                if (keep) StoreFinal(w, o.Final!, me.Lane);
                if (o.None) RecordNone(w);
                o.DisposeAll();
                w.ReleaseLow();
                return !o.Skip;
            }
        }
        if (o.WarmWindows && o.Final != null && !IdleOnly(w)) EnqueueWarm(w);
        // Skipped as idle work, but asked for on screen meanwhile: made as usual.
        if (o.Skip)
        {
            o.Skip = false;
            o.Next = RouteStage(w);
        }
        if (o.Low != null)
        {
            w.HadLow = true;
            Interlocked.Increment(ref _arrivedLow);
            foreach (int size in w.Client.Queue.SizesOf(w.Job))
                if (ThumbSources.TryToDisplay(o.Low, size) is { } shown)
                    w.Client.Deliver(new ThumbArrival(w.Job.Key, w.Path, size, shown, ThumbQuality.Low, w.Job.ListNumber, w.Job.Index));
            w.ReleaseLow();
            w.LowMaster = o.Low;
            o.Low = null;
        }
        if (o.Final != null)
        {
            w.ReleaseLow();
            var sizes = w.Client.Queue.Complete(w.Job);
            Interlocked.Increment(ref _arrivedFinal);
            foreach (int size in sizes)
                w.Client.Deliver(new ThumbArrival(w.Job.Key, w.Path, size, ThumbSources.TryToDisplay(o.Final, size), ThumbQuality.Final, w.Job.ListNumber, w.Job.Index));
            if (keep)
            {
                // Kept on disk once shown, below the UI thread's priority: the encoding is work nobody waits for (it made
                // every picture wait a millisecond or more before it was handed over).
                try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
                StoreFinal(w, o.Final, me.Lane);
            }
            o.Final.Dispose();
            return true;
        }
        if (o.HasStored)
        {
            var sizes = w.Client.Queue.SizesOf(w.Job);
            var shown = new List<(int Size, Bitmap Bitmap)>(sizes.Count);
            foreach (int size in sizes)
                if (ThumbSources.DecodeStored(o.Stored, size) is { } bmp) shown.Add((size, bmp));
            if (shown.Count == 0)
            {
                // A stored picture that doesn't decode as a thumbnail: forgotten, so the next time makes it again; the
                // icon this time.
                Store().Forget(w.RealKey);
                RememberFailed(w.RealKey);
                w.Stage = Stage.Icon;
                Enqueue(w);
                return false;
            }
            w.ReleaseLow();
            var all = w.Client.Queue.Complete(w.Job);
            Interlocked.Increment(ref _arrivedFinal);
            foreach (var (size, bmp) in shown)
                w.Client.Deliver(new ThumbArrival(w.Job.Key, w.Path, size, bmp, ThumbQuality.Final, w.Job.ListNumber, w.Job.Index));
            // A size asked for while the picture was decoded.
            foreach (int size in all)
                if (!shown.Exists(s => s.Size == size) && ThumbSources.DecodeStored(o.Stored, size) is { } late)
                    w.Client.Deliver(new ThumbArrival(w.Job.Key, w.Path, size, late, ThumbQuality.Final, w.Job.ListNumber, w.Job.Index));
            return true;
        }
        if (o.NoneReady)
        {
            w.ReleaseLow();
            var sizes = w.Client.Queue.Complete(w.Job);
            Interlocked.Increment(ref _arrivedNone);
            // Make the late sizes before handing any icon to the grid, which can dispose it at once.
            if (o.Icons is { } icons && sizes.Count > icons.Count && icons.Values.FirstOrDefault(b => b != null) is { } source)
                foreach (int size in sizes)
                    if (!icons.ContainsKey(size)) icons[size] = ThumbSources.TryToDisplay(source, size);
            foreach (int size in sizes)
            {
                Bitmap? icon = null;
                o.Icons?.Remove(size, out icon);
                w.Client.Deliver(new ThumbArrival(w.Job.Key, w.Path, size, icon, ThumbQuality.None, w.Job.ListNumber, w.Job.Index));
            }
            if (o.Icons != null) foreach (var b in o.Icons.Values) b?.Dispose();
            return true;
        }
        if (o.None)
        {
            if (TryFinishWithLow(w)) return true;
            RecordNone(w);
            w.Stage = Stage.Icon;
            Enqueue(w);
            return false;
        }
        if (o.Next is Stage next)
        {
            w.Stage = next;
            Enqueue(w);
        }
        return false;
    }

    // No better source remains. Hand the stand-in over as Final so the receiver stops waiting.
    private bool TryFinishWithLow(Work w)
    {
        if (!w.HadLow) return false;
        using var low = Interlocked.Exchange(ref w.LowMaster, null);
        if (low == null) return false;
        var sizes = w.Client.Queue.Complete(w.Job);
        Interlocked.Increment(ref _arrivedFinal);
        foreach (int size in sizes)
            w.Client.Deliver(new ThumbArrival(w.Job.Key, w.Path, size, ThumbSources.TryToDisplay(low, size), ThumbQuality.Final, w.Job.ListNumber, w.Job.Index));
        return true;
    }

    private void Enqueue(Work w)
    {
        w.Seq = Interlocked.Increment(ref _seq);
        (w.Stage switch
        {
            Stage.Probe => _probe,
            Stage.Decode => _decode,
            Stage.Shell => _shell,
            Stage.Icon => _icon,
            _ => throw new InvalidOperationException("front work isn't queued here"),
        }).Add(w);
        // Sent to a lane with no thread left: the watchdog gives it that lane's fallback (one flag read while lanes live).
        if (w.Stage == Stage.Shell ? _shellDead : (w.Stage is Stage.Probe or Stage.Icon) && _fastDead) _watch.Set();
        Wake();
    }

    /// <summary>"No picture" for good when every source answered cleanly; for this run only when something may pass.</summary>
    private void RecordNone(Work w)
    {
        if (!w.Resolved) return; // the file is gone: nothing to remember
        if (!w.Unclean && Store().PutNone(w.RealKey))
        {
            Interlocked.Increment(ref _nonesStored);
            return;
        }
        Interlocked.Increment(ref _failures);
        // A folder, shortcut or PDF not on this PC that only missed in Windows' cache isn't remembered: a later look asks
        // the cache again.
        if (ThumbLanes.RemembersFailure(w.CacheOnly, w.DecodeTried)) RememberFailed(w.RealKey);
    }

    /// <summary>A key that failed for a reason that may pass, remembered for this run (the memory starts again when it grows big).</summary>
    private void RememberFailed(in ThumbKey key)
    {
        lock (_failed)
        {
            if (_failed.Count >= MaxFailed) _failed.Clear();
            _failed.Add(key);
        }
    }

    /// <summary>A key whose Windows call hung: remembered for the rest of the run (see <see cref="_hung"/>).</summary>
    private void RememberHung(in ThumbKey key)
    {
        lock (_failed) _hung.Add(key);
    }

    private void StoreFinal(Work w, Bitmap master, LaneKind lane)
    {
        // A page of a file's own text is shown, never kept (the caller checks what was drawn; this checks the kind).
        if (!ThumbKinds.KeepOnDisk(w.Path, w.IsFolder)) return;
        // Nor a kind the user chose not to keep (the Instant thumbnails card in Settings).
        if (!_keep.Keeps(w.Path, w.IsFolder)) return;
        // No store (the option off, one still opening, or a location that can't be used): nothing to encode for.
        var store = Store();
        if (!store.IsEnabled) return;
        try
        {
            // Recheck a decoded or extracted picture in case the file changed while it was made. A Windows cache hit
            // uses the stamp already read: a stalled share mustn't hold a fast thread here after delivery.
            if (lane != LaneKind.Fast)
            {
                bool readNow = ThumbSources.Stat(w.Path, out var now, out _);
                if (!ThumbGuards.MayStore(w.RealKey.Stamp, readNow, now)) return;
            }
            var bytes = ThumbSources.Encode(master, Ext(w), w.SizeClass, out var kind);
            // The file's group goes with it: the store keeps pictures longest and deletes a group switched off.
            if (bytes != null && store.Put(w.RealKey, kind, master.Width, master.Height, bytes, ThumbKeep.GroupOf(w.Path, w.IsFolder)))
                Interlocked.Increment(ref _finalsStored);
        }
        catch
        {
            // not stored: made again next time
        }
    }

    /// <summary>
    /// The store to use now. Without a chosen store it is <see cref="Off"/>, which makes no folder and writes nothing,
    /// not even the in-flight marker. A chosen store is opened on the thread pool, never on the calling thread: until it
    /// is open this answers <see cref="Off"/> (a miss, and nothing stored), after waiting up to <paramref name="wait"/>
    /// for it. Only decode threads (and calls off the workers' lanes) wait; fast and shell threads never do, since their
    /// time-out would count the wait. A location that couldn't be used is tried again every 30 s, on the next request.
    /// The calls the grid and Settings make take <see cref="_gate"/> briefly and never wait for an open.
    /// </summary>
    private ThumbStore Store(TimeSpan wait = default)
    {
        var s = _store;
        if (s != null && !DueForRetry(s)) return s;
        System.Threading.Tasks.Task opening;
        lock (_gate)
        {
            s = _store;
            if (s != null && !DueForRetry(s)) return s;
            if (_openStore == null || _disposed) return Off;
            opening = _opening ??= StartOpening();
        }
        if (wait > TimeSpan.Zero && !opening.IsCompleted)
        {
            // A location already known not to work, being tried again: never waited for (the store that can't be used
            // answers meanwhile, as before the retry).
            if (s != null) return s;
            // The wait counts from when the opening began, not from this call: a slow open (a drive asleep) holds each
            // waiting thread once, for what is left of the wait, not every job afresh.
            long left = (long)wait.TotalMilliseconds - (Environment.TickCount64 - Volatile.Read(ref _openStartedAt));
            if (left > 0)
            {
                try { opening.Wait(TimeSpan.FromMilliseconds(left)); } catch { /* opening never throws; belt and braces */ }
            }
        }
        return _store ?? Off;
    }

    /// <summary>A store whose location couldn't be used, last tried long enough ago to try again.</summary>
    private bool DueForRetry(ThumbStore s)
        => !s.IsEnabled && ThumbGuards.RetriesOpen(s.Problem) // never a folder of another account: waiting doesn't change whose it is
           && Environment.TickCount64 - Volatile.Read(ref _openedAt) >= RetryOpenMs;

    /// <summary>Starts opening the chosen store on the thread pool (under <see cref="_gate"/>); it publishes itself when open.</summary>
    private System.Threading.Tasks.Task<bool> StartOpening()
    {
        var open = _openStore!;
        var closing = _closing;
        var openReturned = new System.Threading.Tasks.TaskCompletionSource();
        _openReturned = openReturned.Task;
        long cap = _capOverride;
        int generation = _storeGeneration;
        Volatile.Write(ref _openStartedAt, Environment.TickCount64);
        return System.Threading.Tasks.Task.Run(() =>
        {
            // A folder serves one store at a time: the one closing first (a change of setting) finishes.
            try { closing.Wait(); } catch { /* the old store closed badly: open anyway */ }
            ThumbStore s;
            try { s = open(); }
            catch { s = ThumbStore.Disabled(); }
            openReturned.SetResult();
            ThumbStore? replaced = null;
            List<ThumbClient>? clients = null;
            lock (_gate)
            {
                if (generation == _storeGeneration && !_disposed)
                {
                    // The limit as it is now: one changed while this opened applies too.
                    cap = _capOverride;
                    _opening = null;
                    replaced = _store; // a location that couldn't be used, tried again
                    _store = s;
                    Volatile.Write(ref _openedAt, Environment.TickCount64);
                    clients = _clients.ToList();
                }
            }
            if (clients == null)
            {
                // The choice changed while this one opened (or the service closed): it is not used. UseStore's task
                // waits for this, so the folder can be removed after it.
                return CloseStore(s);
            }
            if (cap > 0 && s.IsEnabled) s.CapBytes = cap;
            if (!ReferenceEquals(replaced, s)) replaced?.Dispose();
            if (s.IsEnabled)
            {
                // Told only while it is the store in use (a later choice replaces it). Subscribed before IsFull is read, so
                // a filling during the open is not missed.
                s.BecameFull += () => { if (ReferenceEquals(_store, s)) RaiseStoreBecameFull(); };
                if (s.IsFull) RaiseStoreBecameFull();
                // A group switched off while no store was open (or in an earlier run) goes now; nothing to do once empty.
                DropUnkept(s, _keep, null);
            }
            // A location that can't be used keeps the store idle: no idle step, whose pictures would go nowhere.
            foreach (var c in clients) c.Queue.AllowIdle = s.IsEnabled;
            Wake();
            return true;
        });
    }

    // Dispose can return while the writer still holds the folder. Only the writer's exit makes it safe to remove.
    private static bool CloseStore(ThumbStore store)
    {
        store.Dispose();
        return store.WaitUntilClosed(TimeSpan.FromSeconds(10));
    }

    // ── The watchdog ────────────────────────────────────────────────────────────────────────────────────────────────

    private void WatchdogLoop()
    {
        long timeout = (long)_options.ShellTimeout.TotalMilliseconds;
        var waits = new[] { _stop.Token.WaitHandle, _watch };
        while (!_stop.IsCancellationRequested)
        {
            List<(Worker Worker, Work Work, long StartedAt)> stuck;
            bool watching;
            lock (_gate)
            {
                long now = Environment.TickCount64;
                watching = _workers.Any(x => x.Lane != LaneKind.Decode && !x.Abandoned && x.Current != null);
                // Each overdue worker with the job it runs and when that run began, read once: GiveUp gives up only that
                // run, never a job the worker took after it.
                stuck = [];
                foreach (var x in _workers)
                {
                    if (x.Lane == LaneKind.Decode || x.Abandoned) continue;
                    long started = Volatile.Read(ref x.StartedAt);
                    var current = x.Current;
                    if (current != null && now - started > timeout && Volatile.Read(ref x.StartedAt) == started) stuck.Add((x, current, started));
                }
            }
            if (stuck.Count > 0) WatchdogHookForProbes?.Invoke();
            foreach (var (x, w, started) in stuck) GiveUp(x, w, started);
            DrainDeadLanes();
            // Four looks a second while a fast or shell job runs; asleep until one starts otherwise.
            if (watching) _stop.Token.WaitHandle.WaitOne(250);
            else WaitHandle.WaitAny(waits);
        }
    }

    /// <summary>
    /// A thread stuck in a Windows call: replaced (while the budget lasts), and its item finished without it. Only the run
    /// the watchdog noted (<paramref name="noted"/>, begun at <paramref name="startedAt"/>): when the worker has finished it
    /// and begun another since, nothing is done this round. What is left is the few instructions between this check and
    /// the job's own state change: a job that ends at that very moment and is begun again at once would be given up while
    /// healthy, which costs one file its thumbnail (its icon until restart) and one replacement thread.
    /// </summary>
    private void GiveUp(Worker x, Work noted, long startedAt)
    {
        if (!ThumbLanes.StillSameRun(noted, startedAt, x.Current, Volatile.Read(ref x.StartedAt))) return;
        var w = noted;
        if (!w.GiveUp()) return;
        // A suspect stuck here gives the suspect turn back, so the others don't wait for it; it is not cleared in the
        // store (its stuck thread's return is not "came back normally": see Run), and the copy below doesn't carry the
        // flag, so its end clears nothing either.
        if (w.Suspect) EndSuspectTurn(w);
        Interlocked.Increment(ref _shellTimeouts);
        lock (_gate)
        {
            x.Abandoned = true;
            _workers.Remove(x);
            // The stuck thread ends by itself when its call returns; one new thread takes its place. Warming (nobody asked
            // for it) doesn't spend the budget the pictures on screen need: it stops for the rest of the run instead, so
            // it can cost one thread at most.
            if (w.WarmOnly) _warmStopped = true;
            bool replaced = w.WarmOnly || Budget.TryTake();
            if (replaced) StartWorker(x.Lane, x.Index);
            // The budget is spent and this was the lane's last thread: from now on work sent to it wakes the watchdog
            // (Enqueue), which gives it the lane's fallback as DrainDeadLanes does below.
            else if (ThumbLanes.LaneDies(replaced, _workers.Count(y => y.Lane == x.Lane)))
            {
                if (x.Lane == LaneKind.Fast) _fastDead = true;
                else if (x.Lane == LaneKind.Shell) _shellDead = true;
            }
        }
        if (w.WarmOnly)
        {
            lock (_warm) _warm.Clear();
            Interlocked.Decrement(ref _warmRunning); // nobody waits for warming
            return;
        }
        if (w.Stage == Stage.Icon)
        {
            FinishIconWithoutWorker(w);
            return;
        }
        Interlocked.Increment(ref _failures);
        RememberHung(w.RealKey);
        Enqueue(w.CopyAt(Stage.Icon));
    }

    /// <summary>A lane whose threads were all given up (the budget spent): its waiting items finish without it.</summary>
    private void DrainDeadLanes()
    {
        bool fastAlive, shellAlive;
        lock (_gate)
        {
            fastAlive = _workers.Any(x => x.Lane == LaneKind.Fast);
            shellAlive = _workers.Any(x => x.Lane == LaneKind.Shell);
        }
        if (!shellAlive)
        {
            foreach (var w in _shell.TakeAll())
            {
                w.Unclean = true;
                w.ShellTried = true;
                // A kind asked of Windows first still has Rock Preview's own extractor.
                if (ThumbLanes.DecodeAfterShellMiss(w.Route, w.DecodeTried, w.GateToShell)) { w.Stage = Stage.Decode; Enqueue(w); continue; }
                if (w.GateToShell && TryFinishWithLow(w)) continue;
                RecordNone(w);
                w.Stage = Stage.Icon;
                Enqueue(w);
            }
            lock (_warm) _warm.Clear();
        }
        if (!fastAlive)
        {
            foreach (var w in _probe.TakeAll())
            {
                w.Stage = RouteStage(w);
                Enqueue(w);
            }
            foreach (var w in _icon.TakeAll()) FinishIconWithoutWorker(w);
        }
    }

    private void FinishIconWithoutWorker(Work w)
    {
        w.ReleaseLow();
        foreach (int size in w.Client.Queue.Complete(w.Job))
        {
            // With no fast worker left, the watchdog can still hand over an icon this kind already has in memory.
            var icon = _fastDead ? _icons.Get(w.Path, w.IsFolder, size, cacheOnly: true) : null;
            w.Client.Deliver(new ThumbArrival(w.Job.Key, w.Path, size, icon, ThumbQuality.None, w.Job.ListNumber, w.Job.Index));
        }
    }
}

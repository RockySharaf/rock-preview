using Anything.Core.Content.Store;

namespace Anything.Core.Content.Search;

/// <summary>
/// Owned by the ContentSource, shared by every engine and tab: resolved sets per (term key, segment id) as one-segment sets
/// (segments are immutable, so a commit resolves only its new segment; deletions apply at map time), each term released
/// after 15 s unused and its sets of segments merged away dropped when it is used on a newer view, and the in-flight
/// lookups keyed by term key that later searches join. A lookup handed to the background lane that nobody asked for in
/// about 10 s is announced once (the windows still showing its term search again and join it); only when nobody asks
/// within another 10 s is it cancelled. At most 64 terms are kept (the least recently used goes first). Thread-safe.
/// </summary>
internal sealed class DocSetCache
{
    sealed class TermEntry
    {
        public readonly Dictionary<uint, DocSet> Sets = [];
        public long LastUsed;   // use counter: the least recently used term is evicted first
        public long LastUsedAt; // Environment.TickCount64 of the last use (idle release)
    }

    readonly object _gate = new();
    readonly Dictionary<string, TermEntry> _terms = new(StringComparer.Ordinal);
    readonly Dictionary<string, Lookup> _lookups = new(StringComparer.Ordinal);
    Timer? _sweep;
    long _uses; // counts uses

    /// <summary>Terms kept at most.</summary>
    internal int Capacity { get; init; } = 64;
    /// <summary>A term's resolved sets are released when nothing used them for this long.</summary>
    internal TimeSpan IdleRelease { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>
    /// A lookup no search asked for this long is cancelled; one handed to the background lane is first announced, and
    /// cancelled only when nobody asked for it again within as long.
    /// </summary>
    internal TimeSpan UnaskedLimit { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>How often the idle and unasked rules are checked.</summary>
    internal TimeSpan SweepEvery { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A term's documents in one segment (a one-segment set, ordinal 0), when resolved before.</summary>
    public bool TryGet(string termKey, uint segmentId, out DocSet set)
    {
        lock (_gate)
        {
            if (_terms.TryGetValue(termKey, out var entry) && entry.Sets.TryGetValue(segmentId, out var found))
            {
                Touch(entry);
                set = found;
                return true;
            }
        }
        set = DocSet.Empty;
        return false;
    }

    /// <summary>Keeps a term's documents in one segment.</summary>
    public void Put(string termKey, uint segmentId, DocSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        lock (_gate)
        {
            if (!_terms.TryGetValue(termKey, out var entry))
            {
                if (_terms.Count >= Capacity) EvictOldest();
                entry = new TermEntry();
                _terms[termKey] = entry;
            }
            entry.Sets[segmentId] = set;
            Touch(entry);
            StartSweep();
        }
    }

    /// <summary>
    /// A term is used on a view: its sets of segments the view no longer holds (merged away) are dropped, so a term in
    /// constant use keeps one set per current segment, not one per segment it ever met. A search still reading an older
    /// view merely resolves such a segment again.
    /// </summary>
    internal void Retain(string termKey, IReadOnlyList<IContentSegment> segments)
    {
        lock (_gate)
        {
            if (!_terms.TryGetValue(termKey, out var entry) || entry.Sets.Count == 0) return;
            List<uint>? gone = null;
            foreach (uint id in entry.Sets.Keys)
            {
                bool held = false;
                foreach (var seg in segments)
                    if (seg.Id == id) { held = true; break; }
                if (!held) (gone ??= []).Add(id);
            }
            if (gone != null) foreach (uint id in gone) entry.Sets.Remove(id);
        }
    }

    // Under _gate.
    void Touch(TermEntry entry)
    {
        entry.LastUsed = ++_uses;
        entry.LastUsedAt = Environment.TickCount64;
    }

    /// <summary>Sets kept for a term (tests).</summary>
    internal int SetCount(string termKey)
    {
        lock (_gate) return _terms.TryGetValue(termKey, out var entry) ? entry.Sets.Count : 0;
    }

    /// <summary>Drops every resolved set and cancels every lookup (the text index was deleted or rebuilt).</summary>
    public void Clear()
    {
        Lookup[] lookups;
        lock (_gate)
        {
            _terms.Clear();
            lookups = [.. _lookups.Values];
            _lookups.Clear();
        }
        foreach (var l in lookups) l.Cancel();
    }

    /// <summary>Terms with resolved sets (tests).</summary>
    internal int TermCount { get { lock (_gate) return _terms.Count; } }
    /// <summary>Lookups still running (tests).</summary>
    internal int LookupCount { get { lock (_gate) return _lookups.Count; } }

    void EvictOldest()
    {
        string? oldest = null;
        long when = long.MaxValue;
        foreach (var (key, entry) in _terms)
            if (entry.LastUsed < when && !_lookups.ContainsKey(key)) { when = entry.LastUsed; oldest = key; }
        oldest ??= _terms.MinBy(t => t.Value.LastUsed).Key;
        _terms.Remove(oldest);
    }

    /// <summary>
    /// The running lookup of a term (a new one when none runs), asked to resolve these segments of the view too: a later
    /// search joins the task an earlier one started instead of starting again. Each queued segment takes its own view
    /// reference, so the lookup can outlive the search that asked.
    /// </summary>
    internal Lookup Join(string key, ContentTerm term, ContentSource? source, ContentView view, IReadOnlyList<int> ordinals)
    {
        lock (_gate)
        {
            if (_lookups.TryGetValue(key, out var running) && running.Ensure(view, ordinals)) return running;
            var lookup = new Lookup(this, key, term, source);
            if (!lookup.Ensure(view, ordinals)) throw new ObjectDisposedException(nameof(ContentView), "The view was released while it was read.");
            _lookups[key] = lookup;
            StartSweep();
            return lookup;
        }
    }

    /// <summary>A lookup finished or was cancelled: forget it (it never answers again).</summary>
    internal void Forget(Lookup lookup)
    {
        lock (_gate)
            if (_lookups.TryGetValue(lookup.Key, out var l) && ReferenceEquals(l, lookup)) _lookups.Remove(lookup.Key);
    }

    void StartSweep()
    {
        if (_sweep != null) return;
        _sweep = new Timer(_ => Sweep());
        _sweep.Change(SweepEvery, SweepEvery);
    }

    /// <summary>Announces or cancels lookups nobody asked for lately, and releases the sets of terms unused for a while.</summary>
    internal void Sweep()
    {
        try { SweepCore(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { } // a timer callback must never take the app down
    }

    void SweepCore()
    {
        var cancel = new List<Lookup>();
        var announce = new List<Lookup>();
        lock (_gate)
        {
            long now = Environment.TickCount64, limit = (long)UnaskedLimit.TotalMilliseconds;
            foreach (var l in _lookups.Values)
            {
                // Asked again since it was announced, even when this sweep comes later than the limit (a busy thread
                // pool): it is wanted, so it may be announced again rather than cancelled.
                if (l.AnnouncedAt != 0 && l.LastAsked >= l.AnnouncedAt) l.AnnouncedAt = 0;
                if (l.Held || now - l.LastAsked <= limit)
                {
                    l.AnnouncedAt = 0; // asked again since: it may be announced again later
                    continue;
                }
                // A lookup in the background lane serves a window that showed results without it: before dropping its
                // work, say it is done for now, so a window still showing the term searches again and joins it (its
                // progress kept). Nobody asking within the limit after that means nobody wants it.
                if (l.GaveUp && l.AnnouncedAt == 0)
                {
                    l.AnnouncedAt = now;
                    announce.Add(l);
                }
                else if (!l.GaveUp || now - l.AnnouncedAt > limit) cancel.Add(l);
            }
            foreach (var l in cancel) _lookups.Remove(l.Key);
            long idle = (long)IdleRelease.TotalMilliseconds;
            List<string>? released = null;
            foreach (var (key, entry) in _terms)
                if (now - entry.LastUsedAt > idle) (released ??= []).Add(key);
            if (released != null) foreach (var key in released) _terms.Remove(key);
            if (_terms.Count == 0 && _lookups.Count == 0 && _sweep != null)
            {
                _sweep.Dispose();
                _sweep = null;
            }
        }
        foreach (var l in cancel) l.Cancel();
        foreach (var l in announce) l.AnnounceUnasked();
    }
}

/// <summary>
/// One term's lookup over segments of one or more views: a queue of work items (resolve a segment; confirm a slice of its
/// candidates in the stored text) that the searches asking for it and, past their budget, the background lane work
/// through. Results go to the cache per segment. It ends when its queue is empty, or when nobody asked for it for a while.
/// </summary>
internal sealed class Lookup
{
    /// <summary>Candidates confirmed per work item (so a long confirmation can be shared and interrupted).</summary>
    internal const int ConfirmSlice = 256;

    abstract class Item;

    /// <summary>Resolve one segment of a view (holds its own reference on the view).</summary>
    sealed class ResolveItem(ContentView lease, IContentSegment segment) : Item
    {
        public readonly ContentView Lease = lease;
        public readonly IContentSegment Segment = segment;
    }

    /// <summary>Confirm candidates [From, To) of one segment.</summary>
    sealed class ConfirmItem(Confirmation work, int from, int to) : Item
    {
        public readonly Confirmation Work = work;
        public readonly int From = from, To = to;
    }

    /// <summary>One segment's confirmation, shared by its slices; the last slice to finish (or be dropped) completes it.</summary>
    sealed class Confirmation(ContentView lease, IContentSegment segment, DocSet matches, int[] candidates, int slices)
    {
        public readonly ContentView Lease = lease;
        public readonly IContentSegment Segment = segment;
        public readonly DocSet Matches = matches;
        public readonly int[] Candidates = candidates;
        public readonly bool[] Confirmed = new bool[candidates.Length];
        public int Remaining = slices;
        public volatile bool Failed;
    }

    readonly DocSetCache _cache;
    readonly ContentTerm _term;
    readonly ContentSource? _source;
    readonly CancellationTokenSource _cts = new();
    readonly object _gate = new();
    readonly LinkedList<Item> _queue = new();
    readonly HashSet<uint> _known = [];
    readonly Dictionary<uint, DocSet?> _results = [];
    readonly HashSet<uint> _unanswered = [];
    int _running;
    bool _completed, _cancelled, _submitted;
    volatile bool _gaveUp;
    long _lastAsked;
    int _holders; // searches in progress that asked for it
    volatile int _queued;

    internal Lookup(DocSetCache cache, string key, ContentTerm term, ContentSource? source)
    {
        _cache = cache;
        Key = key;
        _term = term;
        _source = source;
        _lastAsked = Environment.TickCount64;
    }

    /// <summary>The term's cache key.</summary>
    public string Key { get; }
    /// <summary>When a search last asked for it (Environment.TickCount64).</summary>
    public long LastAsked => Interlocked.Read(ref _lastAsked);
    /// <summary>
    /// A search in progress holds it: asking the whole time, even while it waits for another term or does an item itself
    /// for long.
    /// </summary>
    internal bool Held => Volatile.Read(ref _holders) > 0;
    /// <summary>Work is waiting to be taken.</summary>
    public bool HasQueued => _queued > 0;
    /// <summary>It finished or was cancelled; it never takes work again.</summary>
    public bool Finished { get { lock (_gate) return _completed; } }
    /// <summary>A search stopped waiting for it: the background lane works on it, and its end is announced.</summary>
    internal bool GaveUp => _gaveUp;
    /// <summary>When the cache announced it as unasked (Environment.TickCount64; 0 = not since it was last asked). Under the cache's lock.</summary>
    internal long AnnouncedAt;

    void Asked() => Interlocked.Exchange(ref _lastAsked, Environment.TickCount64);

    /// <summary>A search asks for it until <see cref="Release"/>, however long the search's other work takes.</summary>
    internal void Hold() => Interlocked.Increment(ref _holders);

    /// <summary>The search is done with it: from now on its last ask counts, made here (covering what the search does next).</summary>
    internal void Release()
    {
        Asked();
        Interlocked.Decrement(ref _holders);
    }

    /// <summary>
    /// Nobody asked for it lately although its results are still missing somewhere: says so as if it had finished, so
    /// the windows still showing its term search again and join it (nothing is announced for a lookup that ended).
    /// </summary>
    internal void AnnounceUnasked()
    {
        lock (_gate)
            if (_completed) return;
        _source?.PrefetchCompleted();
    }

    /// <summary>Queues the view's segments not asked for yet; false when the lookup already ended (start a new one).</summary>
    internal bool Ensure(ContentView view, IReadOnlyList<int> ordinals)
    {
        bool added = false;
        lock (_gate)
        {
            if (_completed || _cancelled) return false;
            Asked();
            var segments = view.Store.Segments;
            foreach (int ordinal in ordinals)
            {
                var seg = segments[ordinal];
                if (_known.Contains(seg.Id)) continue;
                if (!view.TryAddRef()) throw new ObjectDisposedException(nameof(ContentView), "The view was released while it was read.");
                _known.Add(seg.Id);
                _queue.AddLast(new ResolveItem(view, seg));
                _queued++;
                added = true;
            }
        }
        if (added && _submitted) LookupLane.Poke();
        return true;
    }

    /// <summary>
    /// The set of a segment once resolved (null when resolving it failed); false while it isn't done, and for a segment
    /// that can't be asked yet (made by other word rules: its documents are missing, so the search says ContentPending).
    /// </summary>
    internal bool TryResult(uint segmentId, out DocSet? set)
    {
        lock (_gate)
        {
            if (_unanswered.Contains(segmentId))
            {
                set = null;
                return false;
            }
            return _results.TryGetValue(segmentId, out set);
        }
    }

    bool AllDone(IReadOnlyList<uint> segmentIds)
    {
        foreach (uint id in segmentIds) if (!_results.ContainsKey(id)) return false;
        return true;
    }

    /// <summary>
    /// Has the lookup worked on until the segments are resolved; OperationCanceledException once ct is cancelled (see
    /// <see cref="WaitFor"/>). For explicit terms, which have no budget. The lookup itself goes on when the caller stops.
    /// </summary>
    internal bool Pump(IReadOnlyList<uint> segmentIds, CancellationToken ct, int helpers = 1)
    {
        Assist(segmentIds, long.MaxValue, ct, helpers);
        return WaitFor(segmentIds, long.MaxValue, ct);
    }

    /// <summary>How long a waiting search leaves the work to its helpers before it takes some itself (a busy thread pool).</summary>
    internal const int HelpGraceMs = 10;

    /// <summary>
    /// Starts up to <paramref name="helpers"/> threads working on the lookup until the segments are resolved, the deadline
    /// passes or ct is cancelled. The search itself waits (<see cref="WaitFor"/>), so it keeps its budget and stops quickly
    /// when cancelled even while one item is slow (a cold disk). A confirmation stops between candidates when its
    /// search is cancelled or its deadline passes, and the candidates it didn't reach go back to the queue, so a refresh
    /// that cancels its search throws no work away; resolving a segment (dictionary only) is never interrupted by a search.
    /// </summary>
    internal void Assist(IReadOnlyList<uint> segmentIds, long deadline, CancellationToken ct, int helpers)
    {
        // Confirmations queue more work once a segment is resolved: then every helper gets some.
        int n = _term.Lookup?.Verifies == true ? helpers : Math.Min(helpers, _queued);
        for (int i = 0; i < n; i++)
            _ = Task.Run(() =>
            {
                try { PumpOne(segmentIds, deadline, ct); }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);
    }

    /// <summary>
    /// Waits until the segments are resolved (true), the deadline passes (false) or ct is cancelled
    /// (OperationCanceledException, within about 10 ms, or one candidate's confirmation when the search was doing that
    /// itself). A waiting search counts as asking for the lookup, also while it does an item itself; when nothing has run
    /// for <see cref="HelpGraceMs"/> (no helper got a thread), it takes work itself, stopping at ct or the deadline like a
    /// helper.
    /// </summary>
    internal bool WaitFor(IReadOnlyList<uint> segmentIds, long deadline, CancellationToken ct)
    {
        Hold();
        try
        {
            long helpAfter = Environment.TickCount64 + HelpGraceMs;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Item? item = null;
                lock (_gate)
                {
                    if (AllDone(segmentIds)) return true;
                    if (_cancelled) return false;
                    long now = Environment.TickCount64;
                    if (now >= deadline) return false;
                    if (_running == 0 && now >= helpAfter) item = Take();
                    if (item == null) Monitor.Wait(_gate, (int)Math.Min(deadline - now, 10));
                }
                Asked();
                if (item != null) Run(item, ct, deadline);
            }
        }
        finally
        {
            Release();
        }
    }

    /// <summary>Takes items until the segments are resolved, the deadline passes or ct is cancelled (checked between items).</summary>
    bool PumpOne(IReadOnlyList<uint> segmentIds, long deadline, CancellationToken ct)
    {
        while (true)
        {
            Item? item;
            lock (_gate)
            {
                if (AllDone(segmentIds)) return true;
                if (_cancelled) return false;
                item = Take();
                if (item == null)
                {
                    // Others work on our segments: wait for them (woken at every finished item).
                    long wait = Math.Min(50, deadline - Environment.TickCount64);
                    if (wait > 0) Monitor.Wait(_gate, (int)wait);
                }
            }
            if (ct.IsCancellationRequested || Environment.TickCount64 >= deadline)
            {
                // This helper's search stopped: the item goes back for the next search or the background lane.
                if (item != null) Requeue(item);
                ct.ThrowIfCancellationRequested();
                lock (_gate) return AllDone(segmentIds);
            }
            if (item == null) continue;
            Asked();
            Run(item, ct, deadline);
        }
    }

    /// <summary>A search stops waiting: the background lane finishes the work, and its end is announced.</summary>
    internal void GiveUp()
    {
        lock (_gate)
        {
            if (_completed) return;
            _gaveUp = true;
            if (_submitted) return;
            _submitted = true;
        }
        LookupLane.Submit(this);
    }

    /// <summary>Takes and does one work item (the background lane); false when there was none.</summary>
    internal bool TryRunOne()
    {
        Item? item;
        lock (_gate)
        {
            if (_completed || _cancelled) return false;
            item = Take();
        }
        if (item == null) return false;
        Run(item, default, long.MaxValue);
        return true;
    }

    /// <summary>Stops the lookup: queued work is dropped, running work stops at its next check, and the view references go.</summary>
    internal void Cancel()
    {
        var drop = new List<Item>();
        lock (_gate)
        {
            if (_completed || _cancelled) return;
            _cancelled = true;
            drop.AddRange(_queue);
            _queue.Clear();
            _queued = 0;
            _running += drop.Count; // dropped items finish like running ones, below
        }
        try { _cts.Cancel(); } catch (AggregateException) { }
        foreach (var item in drop) Finish(Drop(item));
    }

    // Under _gate.
    Item? Take()
    {
        var first = _queue.First;
        if (first == null) return null;
        _queue.RemoveFirst();
        _queued--;
        _running++;
        return first.Value;
    }

    /// <summary>Puts a taken item back at the front (its search stopped), or drops it when the lookup was cancelled meanwhile.</summary>
    void Requeue(Item item)
    {
        lock (_gate)
        {
            if (!_cancelled)
            {
                _running--;
                _queue.AddFirst(item);
                _queued++;
                Monitor.PulseAll(_gate);
                return;
            }
        }
        Finish(Drop(item));
    }

    /// <summary>
    /// Does one item under the lookup's own token; a confirmation also stops between candidates once stop is cancelled or
    /// the deadline passes (the search it works for gave up), handing back the candidates it didn't reach.
    /// </summary>
    void Run(Item item, CancellationToken stop, long deadline)
    {
        var token = _cts.Token;
        Action? done;
        try
        {
            done = item switch
            {
                ResolveItem r => Resolve(r, token),
                ConfirmItem c => Confirm(c, token, stop, deadline),
                _ => null,
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            done = Drop(item); // the lookup was cancelled (nobody asked for it lately, or the index went away)
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A damaged segment or another fault: that segment answers nothing this time (not cached, so asked again later).
            done = Fail(item);
        }
        Finish(done);
    }

    Action? Resolve(ResolveItem r, CancellationToken token)
    {
        var found = ContentResolve.Segment(_term, r.Segment, token);
        if (found.Unanswered)
        {
            Complete(r.Segment.Id, null, unanswered: true);
            return r.Lease.Dispose;
        }
        if (found.Candidates is not { Length: > 0 } candidates)
        {
            Complete(r.Segment.Id, found.Matches);
            return r.Lease.Dispose;
        }
        int slices = (candidates.Length + ConfirmSlice - 1) / ConfirmSlice;
        var work = new Confirmation(r.Lease, r.Segment, found.Matches, candidates, slices);
        lock (_gate)
        {
            // Confirm this segment before starting others, so finished segments pile up for partial answers.
            for (int s = slices - 1; s >= 0; s--)
                _queue.AddFirst(new ConfirmItem(work, s * ConfirmSlice, Math.Min(candidates.Length, (s + 1) * ConfirmSlice)));
            _queued += slices;
            Monitor.PulseAll(_gate);
        }
        if (_submitted) LookupLane.Poke();
        return null; // the lease now belongs to the confirmation
    }

    Action? Confirm(ConfirmItem c, CancellationToken token, CancellationToken stop, long deadline)
    {
        var w = c.Work;
        for (int i = c.From; i < c.To; i++)
        {
            token.ThrowIfCancellationRequested(); // each candidate is a decompression: cheap to check before
            // The search this thread works for stopped: the rest goes back to the queue (at least one candidate is done
            // per item, so work always moves on).
            if (i > c.From && (stop.IsCancellationRequested || (deadline != long.MaxValue && Environment.TickCount64 >= deadline)))
                return Split(c, i);
            w.Confirmed[i] = ContentResolve.Confirm(_term, w.Segment, w.Candidates[i]);
        }
        return SliceDone(w);
    }

    /// <summary>A confirmation stopped at candidate at: [at, To) goes back to the front of the queue as a slice of its own.</summary>
    Action? Split(ConfirmItem c, int at)
    {
        var w = c.Work;
        Interlocked.Increment(ref w.Remaining); // the remainder counts as one more slice before this one counts down
        bool requeued = false;
        lock (_gate)
        {
            if (!_cancelled)
            {
                _queue.AddFirst(new ConfirmItem(w, at, c.To));
                _queued++;
                requeued = true;
                Monitor.PulseAll(_gate);
            }
        }
        if (!requeued) MarkFailed(w); // cancelled meanwhile: the remainder is dropped (its count goes; this slice still holds one)
        else if (_submitted) LookupLane.Poke();
        return SliceDone(w);
    }

    Action? SliceDone(Confirmation w)
    {
        if (Interlocked.Decrement(ref w.Remaining) != 0) return null;
        if (!w.Failed && !_cts.IsCancellationRequested)
        {
            var b = new DocSetBuilder([w.Segment.DocCount]);
            w.Matches.ForEach((_, d) => b.Add(0, d), CancellationToken.None);
            for (int i = 0; i < w.Candidates.Length; i++) if (w.Confirmed[i]) b.Add(0, w.Candidates[i]);
            Complete(w.Segment.Id, b.Build());
        }
        else Complete(w.Segment.Id, null);
        return w.Lease.Dispose;
    }

    /// <summary>The item is dropped (cancelled): its references go; a dropped slice still counts down its segment.</summary>
    Action? Drop(Item item) => item switch
    {
        ResolveItem r => r.Lease.Dispose,
        ConfirmItem c => MarkFailed(c.Work),
        _ => null,
    };

    Action? MarkFailed(Confirmation w)
    {
        w.Failed = true;
        return SliceDone(w);
    }

    Action? Fail(Item item)
    {
        if (item is ResolveItem r)
        {
            Complete(r.Segment.Id, null);
            return r.Lease.Dispose;
        }
        return item is ConfirmItem c ? MarkFailed(c.Work) : null;
    }

    /// <summary>A segment is done: its set is cached (not when it failed or can't be asked yet) and waiting searches wake.</summary>
    void Complete(uint segmentId, DocSet? set, bool unanswered = false)
    {
        if (set != null && !_cts.IsCancellationRequested) _cache.Put(Key, segmentId, set);
        lock (_gate)
        {
            _results[segmentId] = set;
            if (unanswered) _unanswered.Add(segmentId);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>After an item: release what it held, and end the lookup when nothing is queued or running.</summary>
    void Finish(Action? release)
    {
        try { release?.Invoke(); }
        finally
        {
            bool ended = false, announce = false;
            lock (_gate)
            {
                _running--;
                if (!_completed && _running == 0 && (_queue.Count == 0 || _cancelled))
                {
                    _completed = ended = true;
                    announce = _gaveUp && !_cancelled;
                }
                Monitor.PulseAll(_gate);
            }
            if (ended)
            {
                _cache.Forget(this);
                if (announce) _source?.PrefetchCompleted();
            }
        }
    }
}

/// <summary>
/// The background lane for lookups that ran past a search's budget: at most two threads at below-normal priority for the
/// whole process, taking work from the lookup a search asked for most recently, one item at a time.
/// </summary>
internal static class LookupLane
{
    const int MaxWorkers = 2;
    static readonly object Gate = new();
    static readonly List<Lookup> Active = [];
    static int _workers, _waiting;

    /// <summary>Threads working or waiting for work (tests).</summary>
    internal static int Workers { get { lock (Gate) return _workers; } }

    /// <summary>Hands a lookup to the lane.</summary>
    public static void Submit(Lookup lookup)
    {
        lock (Gate)
        {
            if (!Active.Contains(lookup)) Active.Add(lookup);
            Wake();
        }
    }

    /// <summary>A submitted lookup has new work.</summary>
    public static void Poke()
    {
        lock (Gate) Wake();
    }

    // Under Gate.
    static void Wake()
    {
        if (_waiting > 0) { Monitor.Pulse(Gate); return; }
        if (_workers >= MaxWorkers) return;
        _workers++;
        new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Rock Preview content lookups" }.Start();
    }

    static void Work()
    {
        while (true)
        {
            Lookup? next = null;
            lock (Gate)
            {
                while (true)
                {
                    Active.RemoveAll(l => l.Finished);
                    long newest = long.MinValue;
                    foreach (var l in Active)
                        if (l.HasQueued && l.LastAsked > newest) { newest = l.LastAsked; next = l; }
                    if (next != null) break;
                    _waiting++;
                    bool woken = Monitor.Wait(Gate, TimeSpan.FromSeconds(30));
                    _waiting--;
                    if (!woken && !Active.Any(l => l.HasQueued))
                    {
                        _workers--;
                        return;
                    }
                }
            }
            try { next.TryRunOne(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { } // a lookup never takes the app down
        }
    }
}

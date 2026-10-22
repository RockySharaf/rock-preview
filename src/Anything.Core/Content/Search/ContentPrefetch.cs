using Anything.Core.Content.Store;
using Anything.Core.Search;

namespace Anything.Core.Content.Search;

/// <summary>
/// Every content term resolved to document sets before FileIndex.Lock; holds its own view lease. Sets come from the shared
/// cache per segment, and what is missing from the term's lookup in the source's cache, which a later search joins. Explicit
/// content terms are waited for (no budget); plain words get the budget, then compile from the segments resolved so far
/// (the last completed sets, after a commit everything but the new segment) and the lookup goes on in the background.
/// </summary>
internal sealed class ContentPrefetch : IDisposable
{
    /// <summary>One distinct term (several nodes may share it) and what was found of it.</summary>
    sealed class TermState(ContentTerm term, string key)
    {
        public readonly ContentTerm Term = term;
        public readonly string Key = key;
        public DocSet?[] Parts = [];
        public readonly List<int> Missing = [];
        public Lookup? Lookup;
        public DocSet Set = DocSet.Empty;
        public int Resolved;
        public bool Pending;
    }

    readonly Dictionary<QueryNode, TermState> _byNode = new(ReferenceEqualityComparer.Instance);
    int _disposed;

    ContentPrefetch(ContentPlan plan, ContentView? view, ContentSource? source)
    {
        Plan = plan;
        View = view;
        Source = source;
    }

    /// <summary>
    /// Resolves the plan on the view (cache first, then the terms' shared lookups); plain-word terms past the budget stay
    /// pending and finish in the background. Takes its own reference on the view (the caller keeps and releases its own).
    /// </summary>
    public static ContentPrefetch Run(ContentPlan plan, ContentView? view, ContentSource? source, TimeSpan plainBudget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var prefetch = new ContentPrefetch(plan, view != null && view.TryAddRef() ? view : null, source);
        try
        {
            prefetch.Resolve(plainBudget, ct);
            return prefetch;
        }
        catch
        {
            prefetch.Dispose();
            throw;
        }
    }

    /// <summary>The plan it resolved.</summary>
    public ContentPlan Plan { get; }
    /// <summary>The view the sets refer to (null = no index: content terms match nothing).</summary>
    public ContentView? View { get; }
    /// <summary>The index it read.</summary>
    public ContentSource? Source { get; }
    /// <summary>
    /// Some plain-word lookups ran past the budget, a segment can't be asked yet (made by other word rules, rebuilt
    /// meanwhile), or the index has no view yet although it is on.
    /// </summary>
    public bool Pending { get; private set; }

    /// <summary>Helpers working on a lookup while a search waits (verification runs on several cores).</summary>
    static int Helpers => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>
    /// The set for a node; null when the node isn't content, or when its lookup is still pending and no segment of it is
    /// resolved yet (a pending plain word then compiles as name-only).
    /// </summary>
    public DocSet? For(QueryNode node)
    {
        if (View == null || !_byNode.TryGetValue(node, out var state)) return null;
        return state.Pending && state.Resolved == 0 ? null : state.Set;
    }

    void Resolve(TimeSpan plainBudget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (View == null)
        {
            // Off, names only, or switched on but not linked yet (before the first reconcile): matches will come.
            Pending = !Plan.IsEmpty && Source != null && Source.Mode != ContentMode.NamesOnly;
            return;
        }
        var cache = Source?.Cache ?? new DocSetCache();
        var segments = View.Store.Segments;
        var states = new Dictionary<string, TermState>(StringComparer.Ordinal);
        // Every lookup is held until the search is done: one it isn't waiting on right now (a plain word behind a slow
        // explicit term) is still asked for, so it can't be cancelled unannounced meanwhile.
        var held = new List<Lookup>();
        try
        {
            foreach (var (node, term) in Plan.Terms)
            {
                string key = term.Key;
                if (!states.TryGetValue(key, out var state))
                {
                    cache.Retain(key, segments); // the sets of segments merged away go
                    state = new TermState(term, key) { Parts = new DocSet?[segments.Count] };
                    for (int s = 0; s < segments.Count; s++)
                    {
                        if (cache.TryGet(key, segments[s].Id, out var set)) state.Parts[s] = set;
                        else state.Missing.Add(s);
                    }
                    if (state.Missing.Count > 0)
                    {
                        state.Lookup = cache.Join(key, term, Source, View, state.Missing);
                        state.Lookup.Hold();
                        held.Add(state.Lookup);
                    }
                    states[key] = state;
                }
                _byNode[node] = state;
            }

            try
            {
                // Plain words share one budget: helpers start on all of them at once while the search waits, so one slow
                // item (a cold disk) can't hold it past the budget. Explicit terms (content:, fields, pictures) have none.
                long deadline = Environment.TickCount64 + (long)Math.Max(0, plainBudget.TotalMilliseconds);
                foreach (var state in states.Values)
                    if (state.Term.Plain && state.Lookup is { } lookup) lookup.Assist(SegmentIds(state, segments), deadline, ct, Helpers);
                foreach (var state in states.Values)
                    if (!state.Term.Plain && state.Lookup is { } lookup) lookup.Pump(SegmentIds(state, segments), ct, Helpers);
                foreach (var state in states.Values)
                    if (state.Term.Plain && state.Lookup is { } lookup && !lookup.WaitFor(SegmentIds(state, segments), deadline, ct))
                        lookup.GiveUp();
            }
            catch (OperationCanceledException)
            {
                // The search was cancelled, not the lookups: they go on for the next search (announced after ~10 s
                // unasked, cancelled when nobody asks again within as long).
                foreach (var state in states.Values) state.Lookup?.GiveUp();
                throw;
            }

            foreach (var state in states.Values)
            {
                foreach (int s in state.Missing)
                {
                    uint id = segments[s].Id;
                    if (state.Lookup != null && state.Lookup.TryResult(id, out var set)) state.Parts[s] = set ?? DocSet.Empty;
                    else if (cache.TryGet(state.Key, id, out var cached)) state.Parts[s] = cached;
                    else state.Pending = true;
                }
                foreach (var p in state.Parts) if (p != null) state.Resolved++;
                state.Set = DocSet.Join(state.Parts);
                Pending |= state.Pending;
            }
        }
        finally
        {
            foreach (var lookup in held) lookup.Release();
        }
    }

    static List<uint> SegmentIds(TermState state, IReadOnlyList<IContentSegment> segments)
    {
        var ids = new List<uint>(state.Missing.Count);
        foreach (int s in state.Missing) ids.Add(segments[s].Id);
        return ids;
    }

    /// <summary>Releases the view lease (pending lookups keep their own).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) View?.Dispose();
    }
}

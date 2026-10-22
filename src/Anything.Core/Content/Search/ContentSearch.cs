using System.Diagnostics;
using Anything.Core.Search;

namespace Anything.Core.Content.Search;

/// <summary>
/// The calls SearchEngine makes: before the index lock (plan and prefetch, where all content work happens), under it
/// (attach the id map), and at the end (name matches first, and whether matches inside files may be missing).
/// </summary>
internal static class ContentSearch
{
    /// <summary>How long a search waits for plain words' lookups inside files before answering with names (D27).</summary>
    public static readonly TimeSpan PlainBudget = TimeSpan.FromMilliseconds(100);

    /// <summary>Plans and prefetches the query's content terms outside FileIndex.Lock; null when the query never looks inside files.</summary>
    public static ContentPrefetch? Begin(FileIndex index, QueryNode query, SearchOptions options, DateTime now, CancellationToken ct)
        => Begin(index, query, options, now, ct, filter: null, PlainBudget);

    /// <summary>
    /// As <see cref="Begin(FileIndex, QueryNode, SearchOptions, DateTime, CancellationToken)"/>, with the filter's tree (its
    /// content functions are planned, its plain words never) and the plain-word budget. Throws QueryException for bad
    /// content values, OperationCanceledException when ct is cancelled.
    /// </summary>
    public static ContentPrefetch? Begin(FileIndex index, QueryNode query, SearchOptions options, DateTime now, CancellationToken ct,
        QueryNode? filter, TimeSpan plainBudget)
    {
        if (index.Content is not { } source) return null;
        var plan = ContentPlan.Build(query, options, source.Mode, now, filter);
        if (plan.IsEmpty) return null;
        var view = source.Mode == ContentMode.NamesOnly ? null : source.AcquireView();
        try { return ContentPrefetch.Run(plan, view, source, plainBudget, ct); }
        finally { view?.Dispose(); }
    }

    /// <summary>Under the read lock: sets ctx.Content and ctx.ContentMap (null map = stale: ctx.ContentStale).</summary>
    public static void Attach(SearchContext ctx, ContentPrefetch? prefetch)
    {
        ctx.Content = prefetch;
        if (prefetch?.View is not { } view) return;
        var map = prefetch.Source?.MapFor(ctx.Index, view);
        ctx.ContentMap = map;
        ctx.ContentStale = map == null;
    }

    /// <summary>
    /// The results of a search, ordered: with plain words expanded, name matches first, then those found only inside files
    /// (NameMatches is the first group's size, else -1); ContentPending when matches inside files may be missing.
    /// </summary>
    internal static SearchResults Results(int[] ids, Matcher matcher, SearchContext ctx, Stopwatch elapsed, CancellationToken ct)
    {
        int nameMatches = ctx.ContentExpanded ? ContentRanking.NameFirst(ids, matcher, ctx, ct) : -1;
        return new SearchResults { Ids = ids, Elapsed = elapsed.Elapsed, NameMatches = nameMatches, ContentPending = IsPending(ctx) };
    }

    /// <summary>A lookup ran past its budget, the id map is behind the store or the name index, or the index is catching up.</summary>
    static bool IsPending(SearchContext ctx)
        => ctx.Content is { } prefetch && (prefetch.Pending || ctx.ContentStale || (prefetch.View != null && prefetch.Source?.CatchingUp == true));
}

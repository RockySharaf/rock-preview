using System.Buffers;
using System.Diagnostics;

namespace Anything.Core.Search;

/// <summary>
/// Everything-compatible search engine: parses the query (see <see cref="QueryParser"/>), scans every live
/// entry in parallel under the index read lock, applies post-filters (dupe:) and sorts the result.
/// </summary>
public sealed class SearchEngine : ISearchEngine
{
    const int Chunk = 16384;

    // Folder paths are cached across searches while typing, but released shortly after the last search:
    // for millions of entries the cache holds ~100 MB of path strings an idle window does not need.
    FolderPathCache? _paths;
    Timer? _releasePaths;
    const int PathCacheIdleMs = 15_000;

    // Presorted order for the current sort key (large indexes only); see SortOrderCache.
    readonly object _orderGate = new();
    SortOrderCache? _order;

    /// <summary>Index size from which searches use the presorted order (tests lower it).</summary>
    internal int SortCacheThreshold { get; set; } = 200_000;

    /// <summary>Clock used for relative dates (today, last2weeks, ...). Tests inject a fixed "now".</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>How long plain words wait for their lookups inside files (tests change it).</summary>
    internal TimeSpan ContentBudget { get; set; } = Content.Search.ContentSearch.PlainBudget;

    /// <summary>Test hook: "before-lock", "locked" (read lock held) and "unlocking" (still held).</summary>
    internal Action<string>? Probe { get; set; }

    public SearchEngine() { }

    public SearchResults Search(FileIndex index, string query, SearchOptions options, SortKey sort, bool descending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(index);
        options ??= new SearchOptions();
        var sw = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();

        // The query and the filter are parsed apart, so only the query's plain words can look inside files (both parser versions).
        QueryNode q = QueryParser.Parse(query, options);
        QueryNode? filter = string.IsNullOrWhiteSpace(options.Filter) ? null : QueryParser.ParseWithFilter(null, options);
        QueryNode node = filter is null ? q : QueryNode.And(q, filter);
        var now = Clock();
        Content.Search.ContentPrefetch? content; // every content lookup happens here, before the index lock
        try { content = Content.Search.ContentSearch.Begin(index, q, options, now, cancellationToken, filter, ContentBudget); }
        catch (QueryException ex) { return new SearchResults { Ids = [], Error = ex.Message, Elapsed = sw.Elapsed }; }
        using var lease = content;

        Probe?.Invoke("before-lock");
        index.Lock.EnterReadLock();
        try
        {
            Probe?.Invoke("locked");
            var paths = _paths;
            if (paths == null || !paths.IsValidFor(index))
            {
                paths = new FolderPathCache(index);
                _paths = paths;
            }
            (_releasePaths ??= new Timer(_ => _paths = null)).Change(PathCacheIdleMs, Timeout.Infinite);
            var ctx = new SearchContext(index, paths, now);
            Content.Search.ContentSearch.Attach(ctx, content);
            if (sort is SortKey.RunCount or SortKey.DateRun) ctx.NeedRuns = true;
            Matcher matcher;
            try
            {
                var compiler = new QueryCompiler(ctx, largeIndex: index.Count > 200_000);
                matcher = options.InsideFolders
                    ? new InsideFoldersMatcher(compiler.Compile(q), filter is null ? null : compiler.Compile(filter), ctx.Count)
                    : compiler.Compile(node);
                ctx.Prepare(cancellationToken);
            }
            catch (QueryException ex)
            {
                return new SearchResults { Ids = [], Error = ex.Message, Elapsed = sw.Elapsed };
            }

            // Date accessed is never presorted (Everything's fast date accessed sort is off by default too): what the index
            // doesn't know is read from the disk per result, which a lasting order can't follow.
            if (index.Count >= SortCacheThreshold && sort != SortKey.DateAccessed)
            {
                // Large index: filter a persistent presorted order instead of sorting the results.
                lock (_orderGate)
                {
                    var cache = _order?.Key == sort ? _order : new SortOrderCache(sort);
                    cache.Refresh(index, ctx, cancellationToken);
                    _order = cache;
                    int[] found;
                    if (matcher is AllMatcher && !ctx.Dupe) found = cache.All(descending);
                    else
                    {
                        found = Scan(index, matcher, ctx, cancellationToken);
                        if (ctx.Dupe) found = DupeFilter(index, found);
                        cancellationToken.ThrowIfCancellationRequested();
                        found = cache.Order(found, descending);
                    }
                    return Content.Search.ContentSearch.Results(found, matcher, ctx, sw, cancellationToken);
                }
            }

            int[] ids = Scan(index, matcher, ctx, cancellationToken);
            if (ctx.Dupe) ids = DupeFilter(index, ids);
            cancellationToken.ThrowIfCancellationRequested();
            ResultSorter.Sort(index, ctx, ids, sort, descending, cancellationToken);
            return Content.Search.ContentSearch.Results(ids, matcher, ctx, sw, cancellationToken);
        }
        finally
        {
            Probe?.Invoke("unlocking");
            index.Lock.ExitReadLock();
        }
    }

    static int[] Scan(FileIndex index, Matcher matcher, SearchContext ctx, CancellationToken ct)
    {
        int n = ctx.Count;
        int chunks = (n + Chunk - 1) / Chunk;
        var parts = new int[]?[chunks];
        var counts = new int[chunks];
        bool all = matcher is AllMatcher;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, c =>
        {
            int lo = c * Chunk, hi = Math.Min(n, lo + Chunk);
            int[]? buf = null;
            int cnt = 0;
            for (int id = lo; id < hi; id++)
            {
                // Checked for every 2048 entries, matching or not: a slow test (date accessed from the disk) must stop too.
                if ((id & 2047) == 0 && ct.IsCancellationRequested) break;
                if (!index.IsAlive(id)) continue;
                if (!all && !matcher.IsMatch(id, ctx)) continue;
                buf ??= ArrayPool<int>.Shared.Rent(hi - lo);
                buf[cnt++] = id;
            }
            if (buf != null)
            {
                parts[c] = buf;
                counts[c] = cnt;
            }
        });
        ct.ThrowIfCancellationRequested();

        int total = 0;
        foreach (int c in counts) total += c;
        var result = new int[total];
        int pos = 0;
        for (int c = 0; c < chunks; c++)
        {
            var b = parts[c];
            if (b == null) continue;
            Array.Copy(b, 0, result, pos, counts[c]);
            pos += counts[c];
            ArrayPool<int>.Shared.Return(b);
        }
        return result;
    }

    /// <summary>dupe: keeps only entries whose name (case-insensitive) occurs more than once in the result set.</summary>
    static int[] DupeFilter(FileIndex index, int[] ids)
    {
        var counts = new Dictionary<string, int>(ids.Length, StringComparer.OrdinalIgnoreCase);
        foreach (int id in ids)
        {
            string name = index.Name(id);
            counts[name] = counts.TryGetValue(name, out int c) ? c + 1 : 1;
        }
        return ids.Where(id => counts[index.Name(id)] > 1).ToArray();
    }

    public IReadOnlyList<(int Start, int Length)> GetHighlights(string query, SearchOptions options, string text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query)) return [];
        options ??= new SearchOptions();
        if (options.Regex) return [];
        QueryNode node = QueryParser.Parse(query, options);
        var terms = new List<TextNode>();
        CollectPositive(node, negated: false, terms);
        var ranges = new List<(int Start, int Length)>();
        foreach (var t in terms)
        {
            var f = t.Flags;
            var pieces = new List<string>();
            if (t.HasWildcards)
                pieces.AddRange(t.Text.Split('*', '?').Where(s => s.Length > 0));
            else
            {
                pieces.Add(t.Text);
                if (t.Text.Contains('\\')) pieces.AddRange(t.Text.Split('\\').Where(s => s.Length > 0));
            }
            bool ww = f.WholeWord && !t.HasWildcards;
            foreach (var piece in pieces)
            {
                var needle = new Needle(piece, f.MatchCase, f.MatchDiacritics);
                if (needle.Length == 0) continue;
                int pos = 0;
                while (pos <= text.Length - needle.Length)
                {
                    int i = needle.IndexOf(text, pos);
                    if (i < 0) break;
                    if (!ww || TextUtil.IsWordBoundary(text, i, needle.Length))
                    {
                        ranges.Add((i, needle.Length));
                        pos = i + needle.Length;
                    }
                    else pos = i + 1;
                }
            }
        }
        return MergeRanges(ranges);
    }

    static void CollectPositive(QueryNode node, bool negated, List<TextNode> terms)
    {
        switch (node)
        {
            case TextNode t when !negated && !t.Flags.Regex:
                terms.Add(t);
                break;
            case AndNode a:
                foreach (var c in a.Children) CollectPositive(c, negated, terms);
                break;
            case OrNode o:
                foreach (var c in o.Children) CollectPositive(c, negated, terms);
                break;
            case NotNode n:
                CollectPositive(n.Child, !negated, terms);
                break;
        }
    }

    static List<(int Start, int Length)> MergeRanges(List<(int Start, int Length)> ranges)
    {
        if (ranges.Count <= 1) return ranges;
        ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        var merged = new List<(int Start, int Length)>(ranges.Count);
        var (s, e) = (ranges[0].Start, ranges[0].Start + ranges[0].Length);
        for (int i = 1; i < ranges.Count; i++)
        {
            var r = ranges[i];
            if (r.Start <= e) e = Math.Max(e, r.Start + r.Length);
            else
            {
                merged.Add((s, e - s));
                (s, e) = (r.Start, r.Start + r.Length);
            }
        }
        merged.Add((s, e - s));
        return merged;
    }
}

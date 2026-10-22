namespace Anything.Core;

/// <summary>Composition root. Implementations live in Indexing/ and Search/.</summary>
public static class Services
{
    public static IIndexManager CreateIndexManager() => new Indexing.IndexManager();
    public static ISearchEngine CreateSearchEngine() => new Search.SearchEngine();

    /// <summary>
    /// A search engine whose plain words wait up to <paramref name="contentBudget"/> for their lookups inside files (a
    /// window waits 100 ms and searches again when they finish; a one-shot command line can't, so it waits for them:
    /// TimeSpan.MaxValue).
    /// </summary>
    public static ISearchEngine CreateSearchEngine(TimeSpan contentBudget)
        => new Search.SearchEngine { ContentBudget = contentBudget < TimeSpan.Zero ? TimeSpan.MaxValue : contentBudget };
}

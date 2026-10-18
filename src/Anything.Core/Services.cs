namespace Anything.Core;

/// <summary>Composition root. Implementations live in Indexing/ and Search/.</summary>
public static class Services
{
    public static IIndexManager CreateIndexManager() => new Indexing.IndexManager();
    public static ISearchEngine CreateSearchEngine() => new Search.SearchEngine();
}

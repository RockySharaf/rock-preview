using Anything.Core;

namespace Anything.Demo;

/// <summary>
/// The demo's search engine: while search inside files is on (as applied, not as Settings is editing it), searches go to
/// the real engine, which reads the demo's in-memory text index through the real compiler, prefetch and ranking;
/// otherwise to the demo's own name-only engine, as before.
/// </summary>
internal sealed class DemoContentEngine(ISearchEngine names) : ISearchEngine
{
    readonly ISearchEngine _real = Services.CreateSearchEngine();

    /// <summary>Whether search inside files is on; the app sets it once the text index has started.</summary>
    public Func<bool> ContentOn { get; set; } = () => false;

    ISearchEngine Engine => ContentOn() ? _real : names;

    public SearchResults Search(FileIndex index, string query, SearchOptions options, SortKey sort, bool descending, CancellationToken cancellationToken)
        => Engine.Search(index, query, options, sort, descending, cancellationToken);

    public IReadOnlyList<(int Start, int Length)> GetHighlights(string query, SearchOptions options, string text)
        => Engine.GetHighlights(query, options, text);
}

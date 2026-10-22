using Anything.Core.Content;
using Anything.Core.Content.Store;

// Content tests: the seed's (S0) live in this folder, namespace Anything.Tests.Content, and only S0 edits them and this
// file. Each unit puts its tests, fakes and helpers in its own folder and namespace, so two units' FakeStoreView or
// FakeContentSource can't collide when they are merged:
//   U1 Store     Content/Store     Anything.Tests.Content.Store
//   U2 PDF       Content/Pdf       Anything.Tests.Content.Pdf
//   U3 Readers   Content/Readers   Anything.Tests.Content.Readers
//   U4 Search    Content/Search    Anything.Tests.Content.Search
//   U5 Indexing  Content/Indexing  Anything.Tests.Content.Indexing
//   U6 App       Content/App       Anything.Tests.Content.App
//   U7 Preview   Content/Preview   Anything.Tests.Content.Preview
// The helpers below (ListSink, CountingStoreView) are visible from all of them.

namespace Anything.Tests.Content;

/// <summary>Collects tokens as (folded, start, length, kind).</summary>
internal sealed class ListSink : ITokenSink
{
    public List<(string Folded, int Start, int Length, TokenKind Kind)> Tokens { get; } = [];

    public void Token(ReadOnlySpan<char> folded, int start, int length, TokenKind kind) => Tokens.Add((folded.ToString(), start, length, kind));

    public string[] Folded() => [.. Tokens.Select(t => t.Folded)];

    public static ListSink Of(string text)
    {
        var sink = new ListSink();
        ContentTokenizer.Tokenize(text, sink);
        return sink;
    }
}

/// <summary>A store view that only counts its references and releases (for view lifetime tests).</summary>
internal sealed class CountingStoreView : IStoreView
{
    int _disposed;
    public int DisposeCount => Volatile.Read(ref _disposed);
    public long CommitGeneration { get; init; }
    public IReadOnlyList<IContentSegment> Segments => [];
    public IReadOnlyList<VolumeEntry> Volumes => [];
    public bool IsDeleted(DocRef doc) => false;
    public DocRef? Find(ulong key) => null;
    public IEnumerable<(DocRef Ref, DocRow Row)> RowsByKey() => [];
    public IReadOnlyDictionary<ContentKind, KindStats> Stats { get; } = new Dictionary<ContentKind, KindStats>();
    public IStoreView AddRef() => this;
    public bool TryAddRef() => true;
    public void Dispose() => Interlocked.Increment(ref _disposed);
}

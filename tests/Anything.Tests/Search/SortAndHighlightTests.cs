using Anything.Core;
using Anything.Core.Search;

namespace Anything.Tests.Search;

public class SortTests
{
    readonly TreeBuilder _t = new();
    readonly SearchEngine _engine = new();

    public SortTests()
    {
        _t.Folder(@"X:\b");
        _t.Folder(@"X:\a");
        _t.File(@"X:\b\Zeta.txt", 300, TreeBuilder.Ft(2024, 1, 3), TreeBuilder.Ft(2024, 2, 1), FileAttributes.Archive);
        _t.File(@"X:\a\alpha.doc", 100, TreeBuilder.Ft(2024, 1, 2), TreeBuilder.Ft(2024, 2, 3), FileAttributes.Archive | FileAttributes.Hidden);
        _t.File(@"X:\a\Beta.txt", 200, TreeBuilder.Ft(2024, 1, 1), TreeBuilder.Ft(2024, 2, 2), FileAttributes.ReadOnly);
        _t.File(@"X:\gamma", 50, TreeBuilder.Ft(2024, 1, 4), TreeBuilder.Ft(2024, 2, 4), FileAttributes.Normal);
    }

    string[] Names(SortKey key, bool desc) =>
        _engine.Search(_t.Index, "", new SearchOptions(), key, desc, CancellationToken.None).Ids.Select(_t.Index.Name).ToArray();

    [Theory]
    [InlineData(SortKey.Name, "a,b,X:,alpha.doc,Beta.txt,gamma,Zeta.txt")]
    [InlineData(SortKey.Path, "X:,a,b,gamma,alpha.doc,Beta.txt,Zeta.txt")]
    [InlineData(SortKey.Size, "a,b,X:,gamma,alpha.doc,Beta.txt,Zeta.txt")]
    [InlineData(SortKey.Extension, "a,b,gamma,X:,alpha.doc,Beta.txt,Zeta.txt")]
    [InlineData(SortKey.Type, "a,b,X:,gamma,alpha.doc,Beta.txt,Zeta.txt")]
    [InlineData(SortKey.DateModified, "a,b,X:,Beta.txt,alpha.doc,Zeta.txt,gamma")]
    [InlineData(SortKey.DateCreated, "a,b,X:,Zeta.txt,Beta.txt,alpha.doc,gamma")]
    [InlineData(SortKey.Attributes, "Beta.txt,a,b,X:,Zeta.txt,alpha.doc,gamma")]
    public void Sorts_ascending_and_descending(SortKey key, string expectedAscending)
    {
        var asc = expectedAscending.Split(',');
        Assert.Equal(asc, Names(key, false));
        Assert.Equal(asc.Reverse(), Names(key, true));
    }

    [Fact]
    public void Ties_are_deterministic()
    {
        var t = new TreeBuilder();
        for (int i = 0; i < 50; i++) t.File($@"Y:\d{i % 5}\same.txt", 7);
        var a = _engine.Search(t.Index, "same", new SearchOptions(), SortKey.Size, false, default).Ids;
        var b = _engine.Search(t.Index, "same", new SearchOptions(), SortKey.Size, true, default).Ids;
        Assert.Equal(a.Order(), a);   // equal keys keep id order
        Assert.Equal(b.Order(), b);
    }

    [Fact]
    public void Large_result_parallel_sort_is_correct()
    {
        var t = new TreeBuilder();
        var rnd = new Random(42);
        t.Folder(@"Z:\f");
        int folder = t.Id(@"Z:\f");
        t.Index.BulkAdd(add =>
        {
            for (int i = 0; i < 120_000; i++)
                add.Add($"n{rnd.Next(1_000_000):D7}.{(i % 3 == 0 ? "a" : "b")}", folder, FileAttributes.Archive, rnd.Next(1000), 0, 0);
        });
        foreach (var key in new[] { SortKey.Name, SortKey.Size, SortKey.Extension })
        {
            var ids = _engine.Search(t.Index, "file:", new SearchOptions(), key, false, default).Ids;
            Assert.Equal(120_000, ids.Length);
            for (int i = 1; i < ids.Length; i++)
            {
                int c = key switch
                {
                    SortKey.Size => t.Index.Size(ids[i - 1]).CompareTo(t.Index.Size(ids[i])),
                    SortKey.Extension => string.Compare(t.Index.Extension(ids[i - 1]), t.Index.Extension(ids[i]), StringComparison.OrdinalIgnoreCase),
                    _ => string.Compare(t.Index.Name(ids[i - 1]), t.Index.Name(ids[i]), StringComparison.OrdinalIgnoreCase),
                };
                Assert.True(c <= 0, $"{key} out of order at {i}");
            }
        }
    }
}

public class HighlightTests
{
    readonly SearchEngine _engine = new();
    IReadOnlyList<(int Start, int Length)> H(string q, string text, SearchOptions? o = null) => _engine.GetHighlights(q, o ?? new SearchOptions(), text);

    [Fact]
    public void Plain_terms()
    {
        Assert.Equal([(0, 4)], H("song", "Song Two.MP3"));
        Assert.Equal([(0, 4), (5, 3)], H("two song", "Song Two.MP3"));
        Assert.Equal([(0, 4), (5, 4)], H("song", "song song"));
        Assert.Equal([(1, 2)], H("o", "foo"));
        Assert.Equal([(0, 4)], H("cafe", "Café.flac"));
        Assert.Empty(H("diacritics:cafe", "Café.flac"));
    }

    [Fact]
    public void Wildcards_highlight_literal_segments()
    {
        Assert.Equal([(8, 4)], H("*.mp3", "Song Two.MP3"));
        Assert.Equal([(0, 1), (8, 4)], H("s*.mp3", "Song Two.MP3"));
    }

    [Fact]
    public void Negated_function_and_regex_terms_are_not_highlighted()
    {
        Assert.Equal([(5, 3)], H("!song two", "Song Two.MP3"));
        Assert.Equal([(0, 4)], H("ext:mp3 song", "Song Two.MP3"));
        Assert.Empty(H("regex:song", "song"));
        Assert.Empty(H("song", "song", new SearchOptions { Regex = true }));
        Assert.Equal([(0, 4)], H("audio:song", "song.mp3"));
    }

    [Fact]
    public void Case_and_whole_word()
    {
        Assert.Equal([(5, 4)], H("case:song", "Song song"));
        Assert.Equal([(0, 3)], H("ww:foo", "foo foobar"));
        Assert.Equal([(0, 3)], H("foo", "foo foobar", new SearchOptions { MatchWholeWord = true }));
    }

    [Fact]
    public void Path_terms()
    {
        Assert.Equal([(3, 10)], H(@"music\song", @"C:\Music\song.mp3"));
        Assert.Equal([(0, 4)], H(@"music\song", "song.mp3"));
    }

    [Fact]
    public void Empty_inputs() => Assert.Empty(H("", "abc"));
}

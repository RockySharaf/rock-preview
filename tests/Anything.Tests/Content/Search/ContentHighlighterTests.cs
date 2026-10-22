using Anything.Core;
using Anything.Core.Content;

namespace Anything.Tests.Content.Search;

public class ContentHighlighterTests
{
    static readonly SearchOptions Off = new(), On = new() { MatchContent = true };

    static ContentHighlighter H(string query, SearchOptions? o = null) => ContentHighlighter.Create(query, o ?? Off)!;

    static string[] Words(ContentHighlighter h, string text) => [.. h.Find(text).Select(r => text.Substring(r.Start, r.Length))];

    [Fact]
    public void Null_when_the_query_never_looks_inside_files()
    {
        Assert.Null(ContentHighlighter.Create("", On));
        Assert.Null(ContentHighlighter.Create("   ", On));
        Assert.Null(ContentHighlighter.Create("ext:pdf report", Off));
        Assert.Null(ContentHighlighter.Create("budget", Off));
        Assert.Null(ContentHighlighter.Create("bu", On));
        Assert.Null(ContentHighlighter.Create("content:x", Off)); // invalid: the search shows the error
        Assert.Null(ContentHighlighter.Create("content:budget", new SearchOptions { Regex = true }));
        Assert.NotNull(ContentHighlighter.Create("budget", On));
        Assert.NotNull(ContentHighlighter.Create("content:budget", Off));
        Assert.NotNull(ContentHighlighter.Create("dpi:<300", Off));
    }

    [Fact]
    public void Needs_mode_and_plain_expansion()
    {
        Assert.Equal(ContentMode.Documents, H("content:invoice").NeedsMode);
        Assert.Equal(ContentMode.NamesOnly, H("budget", On).NeedsMode); // plain words still match names: no mode hint
        Assert.Equal(ContentMode.Documents, H("budget content:invoice", On).NeedsMode);
        Assert.Equal(ContentMode.Documents, H("title:x1 pagecount:>2").NeedsMode);
        Assert.Equal(ContentMode.DocumentsAndPictures, H("dpi:<300").NeedsMode);
        Assert.Equal(ContentMode.DocumentsAndPictures, H("content:x1 cmyk:").NeedsMode);
        Assert.Equal(ContentMode.DocumentsAndPictures, H("camera:canon").NeedsMode);
        Assert.True(H("budget", On).ExpandsPlainWords);
        Assert.False(H("content:budget", On).ExpandsPlainWords);
    }

    [Fact]
    public void Keys_follow_the_content_terms_only()
    {
        Assert.Equal(H("content:invoice").Key, H("content:invoice").Key);
        Assert.Equal(H("content:invoice").Key, H("content:invoice ext:pdf").Key);
        Assert.NotEqual(H("content:invoice").Key, H("ww:content:invoice").Key);
        Assert.NotEqual(H("content:invoice").Key, H("content:invoices").Key);
        Assert.NotEqual(H("content:invoice").Key, H("!content:invoice").Key);
        Assert.Equal(H("datetaken:today").Key, H("datetaken:today").Key); // no clock in the key
        Assert.NotEqual(H("budget", On).Key, H("content:budget").Key);
    }

    [Fact]
    public void Finds_words_prefixes_phrases_and_codes_with_the_index_rules()
    {
        Assert.Equal(["Report", "report"], Words(H("content:report"), "The Annual Report and reports"));
        Assert.Equal(["Annual Report"], Words(H("content:\"annual report\""), "The Annual Report and reports"));
        Assert.Equal(["INV-2024", "INV2024", "INV 2024"], Words(H("content:INV-2024"), "see INV-2024-001, INV2024 and INV 2024"));
        Assert.Equal(["Café", "cafe"], Words(H("content:cafe"), "Café or cafe"));
        Assert.Equal(["Café"], Words(H("case:content:Café"), "café Café cafe"));
        Assert.Equal(["東京都"], Words(H("content:東京都"), "東京都に行く"));
        Assert.Equal(["Budget"], Words(H("budget", On), "Budget approved"));
        Assert.Equal(["report"], Words(H("report.p", On), "the report.pdf file")); // the incomplete part is ignored, as in the lookup
        Assert.Equal(["INV-2024-001"], Words(H("content:inv*01"), "x INV-2024-001 y"));
        Assert.Equal(["invoice"], Words(H("content:*voice"), "an invoice"));
    }

    [Fact]
    public void Ranges_merge_and_negated_or_field_terms_are_not_found_in_the_body()
    {
        var h = H("content:inv content:invoice");
        Assert.Equal([(3, 7)], h.Find("an invoice"));
        Assert.Equal(["budget"], Words(H("!content:report budget", On), "report budget"));
        Assert.Empty(H("title:annual").Find("annual report"));
        Assert.Empty(H("dpi:<300").Find("300 dpi"));
        Assert.Empty(H("content:").Find("anything"));
    }

    [Fact]
    public void First_match_from_a_position()
    {
        var h = H("content:report");
        Assert.Equal(2, h.FirstMatch("a report and a report", 0, out int length));
        Assert.Equal(6, length);
        Assert.Equal(15, h.FirstMatch("a report and a report", 3, out _));
        Assert.Equal(-1, h.FirstMatch("a report", 3, out _));
        Assert.Equal(-1, h.FirstMatch("", 0, out _));
        Assert.Equal(2, H("content:rep*").FirstMatch("a report", 0, out length));
        Assert.Equal(6, length);
    }

    [Theory]
    [InlineData("dpi:<300 cmyk:", "dpi < 300 · CMYK")]
    [InlineData("width:>=3000", "width ≥ 3000")]
    [InlineData("dpi:72..150", "dpi 72–150")]
    [InlineData("colormode:rgb;cmyk", "RGB or CMYK")]
    [InlineData("orientation:landscape", "landscape")]
    [InlineData("datetaken:2023", "taken 2023")]
    [InlineData("gps:", "has a location")]
    [InlineData("!gps:", "no location")]
    [InlineData("!cmyk:", "not CMYK")]
    [InlineData("dimensions:>=1920x1080", "size ≥ 1920 × 1080")]
    [InlineData("rating:>=4", "rating ≥ 4")]
    public void Picture_conditions_in_words(string query, string expected) => Assert.Equal(expected, H(query).PictureConditions);

    [Fact]
    public void No_picture_conditions_without_picture_functions() => Assert.Null(H("content:invoice").PictureConditions);

    [Fact]
    public void Snippet_prefixes()
    {
        static ContentSnippet S(ContentKind kind, int page = 0, int line = 0, string? sheet = null, DocField? field = null)
            => new("text", [], kind, page, line, sheet, field);
        Assert.Equal("p. 4 · ", ContentPresentation.SnippetPrefix(S(ContentKind.Pdf, page: 4)));
        Assert.Equal("p. 2 · ", ContentPresentation.SnippetPrefix(S(ContentKind.Design, page: 2)));
        Assert.Equal("Slide 3 · ", ContentPresentation.SnippetPrefix(S(ContentKind.Office, page: 3)));
        Assert.Equal("Sheet \"2024\" · ", ContentPresentation.SnippetPrefix(S(ContentKind.Office, page: 2, sheet: "2024")));
        Assert.Equal("Line 120 · ", ContentPresentation.SnippetPrefix(S(ContentKind.Text, line: 120)));
        Assert.Equal("Keywords · ", ContentPresentation.SnippetPrefix(S(ContentKind.Picture, field: DocField.Keywords)));
        Assert.Equal("", ContentPresentation.SnippetPrefix(S(ContentKind.Office)));
        Assert.Equal("Camera model", ContentPresentation.FieldName(DocField.CameraModel));
    }

    [Fact]
    public void A_highlighter_is_safe_from_many_threads()
    {
        var h = H("content:\"annual report\" content:inv*", On);
        string text = string.Concat(Enumerable.Repeat("the annual report on INV-2024 is here. ", 200));
        var expected = h.Find(text);
        Parallel.For(0, 64, _ => Assert.Equal(expected, h.Find(text)));
        Assert.Equal(400, expected.Count);
    }
}

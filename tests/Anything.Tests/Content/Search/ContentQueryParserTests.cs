using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Search;
using Anything.Core.Search;

namespace Anything.Tests.Content.Search;

public class ContentQueryParserTests
{
    static string P(string q, SearchOptions? o = null) => QueryParser.Parse(q, o ?? new SearchOptions()).ToString()!;

    [Theory]
    [InlineData("content:invoice", "content:invoice")]
    [InlineData("ansicontent:invoice", "content:invoice")]
    [InlineData("contenta:invoice", "content:invoice")]
    [InlineData("utf8content:invoice", "content:invoice")]
    [InlineData("utf16content:invoice", "content:invoice")]
    [InlineData("utf16lecontent:invoice", "content:invoice")]
    [InlineData("utf16becontent:invoice", "content:invoice")]
    [InlineData("unicodecontent:invoice", "content:invoice")]
    [InlineData("wcharcontent:invoice", "content:invoice")]
    [InlineData("contentw:invoice", "content:invoice")]
    [InlineData("CONTENT:Invoice", "content:Invoice")]
    [InlineData("content:\"annual report\"", "content:annual report")]
    [InlineData("content:", "content:")]
    [InlineData("width:>=3000", "width:>=3000")]
    [InlineData("wide:100", "width:100")]
    [InlineData("height:<10", "height:<10")]
    [InlineData("high:10", "height:10")]
    [InlineData("dimensions:1920x1080", "dimensions:1920x1080")]
    [InlineData("dimension:>=10x10", "dimensions:>=10x10")]
    [InlineData("orientation:landscape", "orientation:landscape")]
    [InlineData("bitdepth:16", "bitdepth:16")]
    [InlineData("bitsperpixel:16", "bitdepth:16")]
    [InlineData("bpp:8", "bitdepth:8")]
    [InlineData("dpi:<300", "dpi:<300")]
    [InlineData("horizontalresolution:300", "horizontalresolution:300")]
    [InlineData("verticalresolution:300", "verticalresolution:300")]
    [InlineData("colormode:rgb;cmyk", "colormode:rgb;cmyk")]
    [InlineData("colorspace:srgb", "colorspace:srgb")]
    [InlineData("colorrepresentation:srgb", "colorspace:srgb")]
    [InlineData("datetaken:2023", "datetaken:2023")]
    [InlineData("rating:>=4", "rating:>=4")]
    [InlineData("cameramake:canon", "cameramake:canon")]
    [InlineData("cameramaker:canon", "cameramake:canon")]
    [InlineData("cameramodel:eos", "cameramodel:eos")]
    [InlineData("camera:canon", "camera:canon")]
    [InlineData("keywords:beach", "keywords:beach")]
    [InlineData("keyword:beach", "keywords:beach")]
    [InlineData("tags:beach", "keywords:beach")]
    [InlineData("tag:beach", "keywords:beach")]
    [InlineData("title:annual", "title:annual")]
    [InlineData("subject:x", "subject:x")]
    [InlineData("author:smith", "author:smith")]
    [InlineData("authors:smith", "author:smith")]
    [InlineData("comment:draft", "comment:draft")]
    [InlineData("pagecount:>3", "pagecount:>3")]
    [InlineData("contentstatus:failed", "contentstatus:failed")]
    public void Content_functions_have_canonical_names(string query, string expected) => Assert.Equal(expected, P(query));

    [Theory]
    [InlineData("cmyk:", "cmyk:")]
    [InlineData("rgb:", "rgb:")]
    [InlineData("gps:", "gps:")]
    [InlineData("CMYK:", "cmyk:")]
    [InlineData("cmyk: logo", "AND(cmyk:,'logo')")]
    [InlineData("rgb:logo", "AND(rgb:,'logo')")]
    [InlineData("!gps:", "NOT(gps:)")]
    [InlineData("dpi:<300 rgb:", "AND(dpi:<300,rgb:)")]
    public void Restrictions_filter_alone_and_combine_with_the_next_term(string query, string expected) => Assert.Equal(expected, P(query));

    [Theory]
    [InlineData("date-taken:2023", "datetaken:2023")]
    [InlineData("camera-make:canon", "cameramake:canon")]
    [InlineData("camera-maker:canon", "cameramake:canon")]
    [InlineData("camera-model:eos", "cameramodel:eos")]
    [InlineData("page-count:>3", "pagecount:>3")]
    [InlineData("bit-depth:8", "bitdepth:8")]
    [InlineData("bits-per-pixel:8", "bitdepth:8")]
    [InlineData("horizontal-resolution:300", "horizontalresolution:300")]
    [InlineData("vertical-resolution:300", "verticalresolution:300")]
    [InlineData("color-space:srgb", "colorspace:srgb")]
    [InlineData("color-mode:cmyk", "colormode:cmyk")]
    [InlineData("content-status:ok", "contentstatus:ok")]
    [InlineData("date-modified:2024", "dm:2024")]
    [InlineData("no-case:foo", "'foo'")]
    [InlineData("whole-word:foo", "ww:'foo'")]
    public void Dashes_in_names_are_left_out(string query, string expected) => Assert.Equal(expected, P(query));

    [Theory]
    [InlineData("unknownfn:abc", "'unknownfn:abc'")]
    [InlineData("foo-bar:x", "'foo-bar:x'")]
    [InlineData("-content:x", "'-content:x'")]
    [InlineData("e-mail:x", "'e-mail:x'")]
    [InlineData("2024-05-01:x", "'2024-05-01:x'")]
    [InlineData("pictures:x", "'pictures:x'")]
    [InlineData("contents:x", "'contents:x'")]
    [InlineData("a-b-c", "'a-b-c'")]
    [InlineData("report-2024.pdf", "'report-2024.pdf'")]
    public void Unknown_names_stay_text(string query, string expected) => Assert.Equal(expected, P(query));

    [Fact]
    public void Existing_names_are_unchanged()
    {
        Assert.Equal("AND(pic:,'cat')", P("pic:cat"));
        Assert.Equal("AND(doc:,'report')", P("doc:report"));
        Assert.Equal("type:jpg", P("type:jpg"));
        Assert.Equal("size:>1mb", P("size:>1mb"));
        Assert.Equal("len:5", P("len:5"));
        Assert.Equal("dc:2024", P("dc:2024"));
        Assert.Equal("depth:2", P("parents:2"));
    }

    [Fact]
    public void Every_alias_is_known_and_canonical_names_are_content_names()
    {
        foreach (var (alias, canonical) in ContentFunctions.FunctionAliases)
        {
            var node = Assert.IsType<FunctionNode>(QueryParser.Parse(alias + ":x", new SearchOptions()));
            Assert.Equal(canonical, node.Name);
            Assert.True(ContentFunctions.IsContentName(canonical), canonical);
        }
        foreach (var (alias, canonical) in ContentFunctions.RestrictionAliases)
        {
            var node = Assert.IsType<FunctionNode>(QueryParser.Parse(alias + ":", new SearchOptions()));
            Assert.Equal(canonical, node.Name);
            Assert.True(ContentFunctions.IsContentName(canonical), canonical);
        }
        Assert.False(ContentFunctions.IsContentName("ext"));
        Assert.False(ContentFunctions.IsContentName("size"));
    }

    [Fact]
    public void The_filter_is_parsed_apart_and_its_plain_words_are_never_planned()
    {
        // The way SearchEngine parses: the query alone, and the filter alone (ParseWithFilter(null, …) returns just the
        // filter, whatever options the parser applies to it), so the plan sees the filter only for its content functions.
        var options = new SearchOptions { MatchContent = true, Filter = "report content:invoice" };
        var query = QueryParser.Parse("budget", options);
        var filter = QueryParser.ParseWithFilter(null, options);
        // Its structure, not its printed flags (the filter's own options print as diacritics: and the like).
        var and = Assert.IsType<AndNode>(filter);
        Assert.Collection(and.Children,
            c => Assert.Equal("report", Assert.IsType<TextNode>(c).Text),
            c => Assert.Equal("content", Assert.IsType<FunctionNode>(c).Name));

        var plan = ContentPlan.Build(query, options, ContentMode.Documents, DateTime.Now, filter);
        Assert.True(plan.HasPlainExpansion);
        string[] planned = [.. plan.Terms.Keys.Select(k => k.ToString()!).Order()];
        Assert.Equal(["'budget'", "content:invoice"], planned);

        // A filter with options of its own (the other session's FilterOptions style: regex off, whole word on): still apart.
        var strict = new SearchOptions { MatchContent = true, MatchWholeWord = true, Filter = "report" };
        var strictFilter = QueryParser.ParseWithFilter(null, strict);
        var strictPlan = ContentPlan.Build(QueryParser.Parse("budget", strict), strict, ContentMode.Documents, DateTime.Now, strictFilter);
        Assert.DoesNotContain(strictPlan.Terms.Keys, k => k.ToString()!.Contains("report"));
    }

    [Fact]
    public void Regex_mode_makes_the_query_one_regex_with_nothing_to_plan()
    {
        var options = new SearchOptions { Regex = true, MatchContent = true };
        var plan = ContentPlan.Build(QueryParser.Parse("content:invoice", options), options, ContentMode.Documents, DateTime.Now);
        Assert.True(plan.IsEmpty);
    }
}

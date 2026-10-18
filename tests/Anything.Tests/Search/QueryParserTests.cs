using Anything.Core;
using Anything.Core.Search;

namespace Anything.Tests.Search;

public class QueryParserTests
{
    static string P(string q, SearchOptions? o = null) => QueryParser.Parse(q, o ?? new SearchOptions()).ToString()!;

    [Theory]
    [InlineData("", "ALL")]
    [InlineData("   ", "ALL")]
    [InlineData("a", "'a'")]
    [InlineData("a b", "AND('a','b')")]
    [InlineData("a|b", "OR('a','b')")]
    [InlineData("a | b", "OR('a','b')")]
    [InlineData("a|b c", "AND(OR('a','b'),'c')")]
    [InlineData("a b|c", "AND('a',OR('b','c'))")]
    [InlineData("!a b", "AND(NOT('a'),'b')")]
    [InlineData("!a|b", "OR(NOT('a'),'b')")]
    [InlineData("<a b>|c", "OR(AND('a','b'),'c')")]
    [InlineData("!<a|b>", "NOT(OR('a','b'))")]
    [InlineData("\"a b\"", "'a b'")]
    [InlineData("\"a|b<c>\"", "'a|b<c>'")]
    [InlineData("foo\" bar\"", "'foo bar'")]
    [InlineData("<a b", "AND('a','b')")]
    [InlineData("a>", "'a'")]
    [InlineData("\"abc", "'abc'")]
    [InlineData("a || b", "OR('a','b')")]
    [InlineData("| a |", "'a'")]
    [InlineData("<>", "ALL")]
    [InlineData(@"C:\Windows", @"'C:\Windows'")]
    [InlineData("foo!bar", "'foo!bar'")]
    public void Operators_and_grouping(string query, string expected) => Assert.Equal(expected, P(query));

    [Theory]
    [InlineData("case:foo", "case:'foo'")]
    [InlineData("CASE:foo", "case:'foo'")]
    [InlineData("case:<a b>", "AND(case:'a',case:'b')")]
    [InlineData("case:ww:foo", "case:ww:'foo'")]
    [InlineData("diacritics:foo", "diacritics:'foo'")]
    [InlineData("path:foo", "path:'foo'")]
    [InlineData("nopath:foo", "nopath:'foo'")]
    [InlineData("wholeword:foo", "ww:'foo'")]
    [InlineData("nowildcards:a*", "nowildcards:'a*'")]
    [InlineData("regex:^a.*z$", "regex:'^a.*z$'")]
    [InlineData("regex:(a|b)", "regex:'(a|b)'")]
    [InlineData("<regex:a.b>", "regex:'a.b'")]
    [InlineData("<regex:a.b> c", "AND(regex:'a.b','c')")]
    [InlineData("case:!foo", "NOT(case:'foo')")]
    [InlineData("case:", "ALL")]
    [InlineData("file:", "file:")]
    [InlineData("files: foo", "AND(file:,'foo')")]
    [InlineData("folder:foo", "AND(folder:,'foo')")]
    [InlineData("folders:<a|b>", "AND(folder:,OR('a','b'))")]
    [InlineData("!file:", "NOT(file:)")]
    [InlineData("audio:", "audio:")]
    [InlineData("pic:cat", "AND(pic:,'cat')")]
    [InlineData("root:", "root:")]
    [InlineData("empty: dupe:", "AND(empty:,dupe:)")]
    public void Modifiers_and_restrictions(string query, string expected) => Assert.Equal(expected, P(query));

    [Theory]
    [InlineData("ext:mp3;wav", "ext:mp3;wav")]
    [InlineData("extension:mp3", "ext:mp3")]
    [InlineData("size:>1mb", "size:>1mb")]
    [InlineData("size:>=1mb", "size:>=1mb")]
    [InlineData("size:<1mb", "size:<1mb")]
    [InlineData("size:<=1mb", "size:<=1mb")]
    [InlineData("size:=1mb", "size:=1mb")]
    [InlineData("size:!=1mb", "size:!=1mb")]
    [InlineData("size:1mb..2mb", "size:1mb..2mb")]
    [InlineData("<size:>1mb>", "size:>1mb")]
    [InlineData("dm:today|dc:today", "OR(dm:today,dc:today)")]
    [InlineData("datemodified:2024", "dm:2024")]
    [InlineData("datecreated:2024", "dc:2024")]
    [InlineData("attributes:H", "attrib:H")]
    [InlineData("parent:\"C:\\Program Files\"", "parent:C:\\Program Files")]
    [InlineData("infolder:C:\\x", "parent:C:\\x")]
    [InlineData("nosubfolders:C:\\x", "parent:C:\\x")]
    [InlineData("parents:2", "depth:2")]
    [InlineData("!ext:txt", "NOT(ext:txt)")]
    [InlineData("ext:", "ext:")]
    [InlineData("unknownfn:abc", "'unknownfn:abc'")]
    public void Functions(string query, string expected) => Assert.Equal(expected, P(query));

    [Theory]
    [InlineData("quot:hi", "'\"hi'")]
    [InlineData("apos:", "'''")]
    [InlineData("amp:", "'&'")]
    [InlineData("lt:gt:", "'<>'")]
    [InlineData("default:", "'default:'")]
    public void Literal_macros(string query, string expected) => Assert.Equal(expected, P(query));

    [Fact]
    public void Global_options_become_term_defaults()
    {
        Assert.Equal("case:'foo'", P("foo", new SearchOptions { MatchCase = true }));
        Assert.Equal("'foo'", P("nocase:foo", new SearchOptions { MatchCase = true }));
        Assert.Equal("ww:'foo'", P("foo", new SearchOptions { MatchWholeWord = true }));
        Assert.Equal("path:'foo'", P("foo", new SearchOptions { MatchPath = true }));
        Assert.Equal("nopath:'foo'", P("nopath:foo", new SearchOptions { MatchPath = true }));
        Assert.Equal("diacritics:'foo'", P("foo", new SearchOptions { MatchDiacritics = true }));
    }

    [Fact]
    public void Regex_option_makes_whole_query_one_regex()
    {
        Assert.Equal("regex:'^a b|c$'", P("  ^a b|c$ ", new SearchOptions { Regex = true }));
    }

    [Fact]
    public void Filter_is_anded()
    {
        var node = QueryParser.ParseWithFilter("song", new SearchOptions { Filter = "ext:mp3;wav", Regex = false });
        Assert.Equal("AND('song',ext:mp3;wav)", node.ToString());
        var regexNode = QueryParser.ParseWithFilter("^s", new SearchOptions { Filter = "audio:", Regex = true });
        Assert.Equal("AND(regex:'^s',audio:)", regexNode.ToString());
    }

    [Fact]
    public void Function_nodes_expose_operator_and_value()
    {
        var f = Assert.IsType<FunctionNode>(QueryParser.Parse("size:>=10kb", new SearchOptions()));
        Assert.Equal("size", f.Name);
        Assert.Equal(CompareOp.GreaterOrEqual, f.Op);
        Assert.Equal("10kb", f.Value);
    }
}

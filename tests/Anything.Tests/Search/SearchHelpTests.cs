using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Search;
using Anything.Core.Search;

namespace Anything.Tests.Search;

/// <summary>
/// <see cref="SearchHelp"/>: the F1 help as data. Nothing the plain-text help of 1.7.28 said may be lost, and every
/// name the search understands is either explained or left out on purpose (so a new one can't slip in unexplained).
/// </summary>
public sealed class SearchHelpTests
{
    /// <summary>Every name, sign, value and example the help of 1.7.28 showed, taken from its text.</summary>
    public static readonly string[] OldHelp =
    [
        // Operators, wildcards
        "space", "|", "!", "< >", "\" \"", "*", "?", "path:", "\\",
        // Modifiers
        "case:", "nocase:", "ww:", "wholeword:", "noww:", "path:", "nopath:", "diacritics:", "nodiacritics:", "regex:",
        "noregex:", "file:", "folder:",
        // Functions and their examples
        "ext:", "ext:jpg;png", "size:", "size:>1mb", "size:1kb..10kb", "empty", "tiny", "small", "medium", "large", "huge",
        "gigantic", "dm:", "dc:", "dm:today", "dm:2024", "dm:>=2024-01-01", "dc:lastweek", "attrib:", "attrib:H",
        "parent:", "depth:", "len:", "type:",
        // Comparisons, filter macros
        "=", "<", "<=", ">", ">=", "start..end", "audio:", "zip:", "doc:", "exe:", "pic:", "video:",
        // Search inside files
        "Settings › Indexing › Search inside files", "plain words", "Match inside files", "3 or more letters",
        "Chinese, Japanese, Korean", "Match Path", "Regex", "content:", "content:invoice", "ww:content:report",
        "content:\"annual report\"", "content:INV-2024", "content:inv*24", "case:content:Café", "diacritics:content:café",
        "utf8content:", "ansicontent:", "title:", "subject:", "comment:", "description:", "author:", "authors:",
        "keywords:", "keyword:", "tags:", "tag:", "from:", "sender:", "to:", "recipients:", "To and Cc", "pagecount:",
        "pagecount:>20", "page-count:", "contentstatus:", "ok", "truncated", "notext", "cloud", "optedout", "efs",
        "password", "toobig", "damaged", "unsupported", "denied", "timeout", "crashed", "failed",
        // Pictures' details
        "Also search inside documents and pictures' details", "width:", "height:", "width:>=3000", "wide:", "high:",
        "dimensions:", "dimensions:2480x3508", "dimensions:>=1920x1080", "dimension:", "orientation:", "landscape",
        "portrait", "square", "dpi:", "dpi:<300", "dpi:72..150", "horizontalresolution:", "verticalresolution:",
        "colormode:", "rgb", "cmyk", "gray", "lab", "indexed", "bitmap", "multichannel", "duotone", "cmyk:", "rgb:",
        "cmyk: logo", "colorspace:", "srgb", "uncalibrated", "colorrepresentation:", "bitdepth:", "bitsperpixel:", "bpp:",
        "rating:", "0-5", "datetaken:", "datetaken:2023", "date-taken:lastweek", "camera:", "cameramake:", "cameramaker:",
        "cameramodel:", "camera:canon", "gps:", "!gps:", "date-taken:", "\"title:draft\"", "\"camera:old.jpg\"",
        // Examples
        "report pdf", "*.mp3 | *.flac", "ext:jpg;png size:>2mb", "\"my documents\" !temp", "c:\\projects\\ .cs",
        "dm:today folder:", "content:invoice ext:pdf", "dpi:<300 cmyk:", "tags:beach datetaken:2024",
        // The Search menu's switches
        "Ctrl+I", "Ctrl+B", "Ctrl+U", "Ctrl+M", "Ctrl+R",
        // Keys
        "Alt+Down", "F2", "Delete", "Shift+Delete", "Recycle Bin", "Ctrl+Shift+C", "Ctrl+Z", "Ctrl+Y", "F4", "Alt+D",
        "Ctrl+L", "Alt+Left", "Alt+Right", "Alt+Up", "Ctrl+N", "Ctrl+Shift+N", "Ctrl+Shift+1 .. 8", "extra large",
        "thumbnail sizes",
    ];

    /// <summary>The four names the README shows that the old help lacked.</summary>
    public static readonly string[] Added = ["da:", "rc:", "dr:", "runcount:", "da:yesterday", "rc:today", "dr:thisweek", "runcount:>2"];

    /// <summary>Names the search understands that the help leaves out on purpose: long spellings of names it shows,
    /// and Everything's rarer functions (the decision for this release was to add only what the README shows).</summary>
    static readonly HashSet<string> NotShown = new(StringComparer.OrdinalIgnoreCase)
    {
        "wholewords", "nowholeword", "nowholewords", "wildcards", "nowildcards", "files", "folders", "root", "empty", "dupe",
        "extension", "datemodified", "datecreated", "dateaccessed", "recentchange", "daterun", "attributes", "infolder",
        "nosubfolders", "startwith", "endwith", "parents", "child", "childcount", "childfilecount", "childfoldercount",
        // Everything's other spellings of content:, which the help sums up as "Everything's other content: names".
        "contenta", "utf16content", "utf16lecontent", "utf16becontent", "unicodecontent", "wcharcontent", "contentw",
    };

    static readonly string Plain = SearchHelp.PlainText();

    public static TheoryData<string> OldNames() => [.. OldHelp.Distinct()];
    public static TheoryData<string> AddedNames() => [.. Added];

    [Theory, MemberData(nameof(OldNames))]
    public void Nothing_the_old_help_said_is_lost(string name) => Assert.Contains(name, Plain, StringComparison.Ordinal);

    [Theory, MemberData(nameof(AddedNames))]
    public void The_names_the_README_shows_are_added(string name) => Assert.Contains(name, Plain, StringComparison.Ordinal);

    [Fact]
    public void Every_section_has_entries_and_no_entry_is_empty()
    {
        Assert.NotEmpty(SearchHelp.Sections);
        foreach (var s in SearchHelp.Sections)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
            Assert.NotEmpty(s.Entries);
            foreach (var e in s.Entries)
            {
                Assert.NotEmpty(e.Code);
                Assert.All(e.Code, c => Assert.False(string.IsNullOrWhiteSpace(c)));
                Assert.False(string.IsNullOrWhiteSpace(e.Meaning));
                Assert.All(e.Examples, x => Assert.False(string.IsNullOrWhiteSpace(x)));
                // One wrapping sentence or a few: no hand-made line breaks or space-made columns.
                Assert.DoesNotContain('\n', e.Meaning);
                Assert.DoesNotContain("   ", e.Meaning);
                Assert.DoesNotContain(":.", e.Meaning); // a name then a full stop reads as a typo: names go in brackets
            }
        }
    }

    [Fact]
    public void Plain_text_holds_every_section_and_every_entry_as_drawn()
    {
        foreach (var s in SearchHelp.Sections)
        {
            Assert.Contains(SearchHelp.PlainHeading(s), Plain);
            if (s.Note is not null) Assert.Contains(s.Note, Plain);
            foreach (var e in s.Entries)
            {
                Assert.Contains(e.PlainText(), Plain);
                // The very strings the window draws, so Copy gives what is shown.
                Assert.Contains(e.Meaning, e.PlainText());
                Assert.All(e.Code, c => Assert.Contains(c, e.PlainText()));
                Assert.All(e.Examples, x => Assert.Contains(x, e.PlainText()));
            }
        }
        Assert.StartsWith(SearchHelp.Title, Plain);
        Assert.DoesNotContain("\n\n\n", Plain.Replace("\r", ""));
    }

    [Fact]
    public void Every_name_the_search_understands_is_explained_or_left_out_on_purpose()
    {
        var missing = QueryParser.KnownNames.Where(n => !NotShown.Contains(n) && !Plain.Contains(n + ":", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(missing.Count == 0, "Not in the help: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("", 0, 0, "ext:", HelpInsert.AtCaret, "ext:", 4)]
    [InlineData("report", 6, 0, "ext:", HelpInsert.AtCaret, "report ext:", 11)]
    [InlineData("report ", 7, 0, "ext:", HelpInsert.AtCaret, "report ext:", 11)]
    [InlineData("report", 0, 0, "case:", HelpInsert.AtCaret, "case: report", 5)]
    [InlineData("a b", 1, 0, "file:", HelpInsert.AtCaret, "a file: b", 7)]
    [InlineData("a old b", 2, 3, "ext:", HelpInsert.AtCaret, "a ext: b", 6)]
    [InlineData("anything", 3, 0, "report pdf", HelpInsert.Replace, "report pdf", 10)]
    [InlineData("keep", 2, 0, "F2", HelpInsert.None, "keep", 2)]
    [InlineData("short", 99, 99, "ext:", HelpInsert.AtCaret, "short ext:", 10)]
    public void A_double_click_puts_a_name_at_the_caret_and_an_example_in_place_of_the_search(
        string box, int start, int length, string text, HelpInsert how, string expected, int caret)
        => Assert.Equal((expected, caret), SearchHelp.Insert(box, start, length, text, how));

    [Fact]
    public void Examples_parse_and_keys_and_signs_are_not_put_in_the_search_box()
    {
        foreach (var s in SearchHelp.Sections)
            foreach (var e in s.Entries)
            {
                foreach (var x in e.Examples) AssertValidSearch(x);
                if (e.Insert == HelpInsert.Replace) AssertValidSearch(e.Code[0]);
            }
        var keys = SearchHelp.Sections.Single(s => s.Title == "Keys");
        Assert.All(keys.Entries, e => Assert.Equal(HelpInsert.None, e.Insert));
        Assert.All(SearchHelp.Sections.Single(s => s.Title == "Operators").Entries, e => Assert.Equal(HelpInsert.None, e.Insert));
        Assert.All(SearchHelp.Sections.Single(s => s.Title == "Examples").Entries, e => Assert.Equal(HelpInsert.Replace, e.Insert));
    }

    // The parser never throws: a misspelt name (titel:draft) just becomes a plain word, and a bad value only fails when
    // the search is put together. So an example counts as valid when no plain word in it still looks like "name:", and
    // it compiles the way a search does, names and contents both, without a QueryException.
    static readonly System.Text.RegularExpressions.Regex LooksLikeName = new("^[A-Za-z][A-Za-z-]+:");

    static void AssertValidSearch(string search)
    {
        var options = new SearchOptions { MatchContent = true };
        var node = QueryParser.Parse(search, options);
        foreach (var text in Words(node))
            Assert.False(LooksLikeName.IsMatch(text), $"'{search}': '{text}' is not a name the search knows");
        var index = new FileIndex();
        var ctx = new SearchContext(index, new FolderPathCache(index), DateTime.Now);
        var ex = Record.Exception(() =>
        {
            new QueryCompiler(ctx, largeIndex: false).Compile(node);
            ContentPlan.Build(node, options, ContentMode.DocumentsAndPictures, DateTime.Now);
        });
        Assert.True(ex is null, $"'{search}' does not compile: {ex?.Message}");
    }

    static IEnumerable<string> Words(QueryNode node) => node switch
    {
        TextNode t => [t.Text],
        AndNode a => a.Children.SelectMany(Words),
        OrNode o => o.Children.SelectMany(Words),
        NotNode n => Words(n.Child),
        _ => [],
    };

    [Theory]
    [InlineData("titel:draft")]
    [InlineData("size:>2mbb")]
    public void A_wrong_example_would_be_caught(string search) =>
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertValidSearch(search));
}

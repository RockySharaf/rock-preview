using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Search;
using Anything.Core.Search;

namespace Anything.Tests.Content.Search;

public class ContentPlanTests
{
    static readonly DateTime Now = new(2024, 5, 15, 12, 0, 0, DateTimeKind.Local);

    static ContentPlan Plan(string query, SearchOptions? options = null, ContentMode mode = ContentMode.Documents)
    {
        var o = options ?? new SearchOptions { MatchContent = true };
        return ContentPlan.Build(QueryParser.Parse(query, o), o, mode, Now);
    }

    static ContentTerm Only(string query, SearchOptions? options = null) => Assert.Single(Plan(query, options).Terms).Value;

    [Theory]
    [InlineData("budget")]
    [InlineData("Budget")]
    [InlineData("bud")]
    [InlineData("budget report")]
    [InlineData("INV-2024")]
    [InlineData("report.pdf")]
    [InlineData("東京")]
    [InlineData("nopath:budget")]
    [InlineData("ww:budget")]
    public void Plain_words_that_expand(string query)
    {
        var plan = Plan(query);
        Assert.True(plan.HasPlainExpansion, query);
        Assert.All(plan.Terms.Values, t => Assert.True(t.Plain));
    }

    [Theory]
    [InlineData("bu")]                  // under 3 letters
    [InlineData("x")]
    [InlineData("東")]                  // one character of a bigram script
    [InlineData("東京都")]              // several bigrams: only the stored text could prove them adjacent (A9)
    [InlineData("!budget")]             // negated
    [InlineData("!<budget|report>")]
    [InlineData("budg*")]               // wildcards
    [InlineData("b?dget")]
    [InlineData("regex:budget")]        // regex
    [InlineData(@"docs\budget")]        // a path term (backslash)
    [InlineData("path:budget")]
    [InlineData("case:INV-2024")]       // a code under case or accents would be confirmed part by part (A9)
    [InlineData("diacritics:café-crème")]
    [InlineData(".pdf")]                // an extension typed alone narrows names (Everything's habit)
    [InlineData(".jpg")]
    [InlineData(".docx")]
    [InlineData(".NET")]
    [InlineData("\"annual report\"")]   // a phrase: never AND-of-parts for plain words
    [InlineData("iPhone15を")]          // mixed scripts: several runs
    [InlineData("ab-cd")]               // a code without a part of 3+ characters
    [InlineData("child:budget")]        // function values never expand
    [InlineData("type:budget")]
    public void Plain_words_that_stay_names_only(string query)
    {
        var plan = Plan(query);
        Assert.False(plan.HasPlainExpansion, query);
        Assert.DoesNotContain(plan.Terms.Values, t => t.Plain);
    }

    [Fact]
    public void Expansion_needs_the_toggle_and_a_mode()
    {
        Assert.False(Plan("budget", new SearchOptions()).HasPlainExpansion);
        Assert.False(Plan("budget", mode: ContentMode.NamesOnly).HasPlainExpansion);
        Assert.True(Plan("budget", mode: ContentMode.DocumentsAndPictures).HasPlainExpansion);
        // Explicit content terms are planned whatever the toggle and the mode (they match nothing in names-only mode).
        Assert.False(Plan("content:budget", new SearchOptions(), ContentMode.NamesOnly).IsEmpty);
        // The global Match case and Match diacritics options make every plain word case: or diacritics: — a word still
        // looks inside files, confirmed with those rules; Match path and Regex keep plain words to names.
        Assert.True(Plan("budget", new SearchOptions { MatchContent = true, MatchCase = true }).HasPlainExpansion);
        Assert.True(Plan("café", new SearchOptions { MatchContent = true, MatchDiacritics = true }).HasPlainExpansion);
        Assert.False(Plan("budget", new SearchOptions { MatchContent = true, MatchPath = true }).HasPlainExpansion);
        Assert.False(Plan("budget", new SearchOptions { MatchContent = true, Regex = true }).HasPlainExpansion);
    }

    [Theory]
    [InlineData("case:Budget", true, false)]
    [InlineData("diacritics:café", false, true)]
    [InlineData("case:ww:Budget", true, false)]
    public void Under_match_case_or_diacritics_one_word_looks_inside_files_confirmed_with_those_rules(string query, bool matchCase, bool diacritics)
    {
        var term = Only(query);
        Assert.True(term.Plain);
        Assert.Equal((matchCase, diacritics), (term.MatchCase, term.MatchDiacritics));
        var lookup = Assert.IsType<WordPlan>(term.Lookup);
        Assert.True(lookup.DirectNeedsVerify); // the folded word's documents, confirmed in the stored text
        Assert.Empty(lookup.Groups);
        // Its own cache key: the case-blind word's documents aren't this one's.
        Assert.NotEqual(Only(query.Replace("case:", "").Replace("diacritics:", "")).Key, term.Key);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("report.pdf")]
    [InlineData(".gitignore")]
    public void Extension_like_words_with_more_to_them_still_expand(string query) => Assert.True(Plan(query).HasPlainExpansion, query);

    [Theory]
    [InlineData("budget", "budget", true)]
    [InlineData("ww:budget", "budget", false)]
    [InlineData("INV-2024", "inv2024", true)]
    [InlineData("inv-2024-001", "inv2024001", true)]
    [InlineData("report.pdf", "reportpdf", true)]
    [InlineData("report.p", "report", true)]      // a trailing part of one character is ignored (A9)
    [InlineData("report.pd", "reportpd", true)]
    [InlineData("IMG_1", "img", true)]
    [InlineData("budget-2024.x", "budget2024", true)]
    [InlineData("ww:report.p", "reportp", false)] // whole word: the code is complete as typed
    [InlineData("東京", "東京", true)]
    [InlineData("Café", "cafe", true)]
    public void Plain_words_resolve_through_one_prefix_only(string query, string direct, bool prefix)
    {
        var term = Only(query);
        var lookup = Assert.IsType<WordPlan>(term.Lookup);
        Assert.Equal(direct, lookup.Direct);
        Assert.Equal(prefix, lookup.DirectPrefix);
        Assert.False(lookup.DirectNeedsVerify);
        Assert.Empty(lookup.Groups); // never AND-of-parts verification for plain words
    }

    [Fact]
    public void No_plain_word_is_ever_confirmed_in_the_stored_text()
    {
        // A9: plain words are answered by the dictionary alone; a longer bigram run (東京都) stays name-only instead.
        foreach (var query in new[] { "budget", "ww:budget", "INV-2024", "report.p", "東京", "東京都", "iPhone15を", "budget report" })
            foreach (var term in Plan(query).Terms.Values)
                Assert.False(term.Lookup!.Verifies, query);
        Assert.Empty(Plan("東京都").Terms);
    }

    [Fact]
    public void Short_trailing_parts_are_dropped_with_their_joiner()
    {
        Assert.Equal("report", ContentPlan.WithoutShortTrailingParts("report.p"));
        Assert.Equal("INV-2024", ContentPlan.WithoutShortTrailingParts("INV-2024-0"));
        Assert.Equal("a", ContentPlan.WithoutShortTrailingParts("a-b"));
        Assert.Equal("report.pdf", ContentPlan.WithoutShortTrailingParts("report.pdf"));
        Assert.Equal("report.", ContentPlan.WithoutShortTrailingParts("report."));
        Assert.Equal("x-ab", ContentPlan.WithoutShortTrailingParts("x-ab"));
        Assert.Equal("budget p", ContentPlan.WithoutShortTrailingParts("budget p")); // a space is not a joiner
    }

    [Fact]
    public void Explicit_words_keep_full_verification()
    {
        var single = Only("content:invoice").Lookup!;
        Assert.Equal(("invoice", true, false), (single.Direct, single.DirectPrefix, single.Verifies));
        var exact = Only("ww:content:invoice").Lookup!;
        Assert.Equal(("invoice", false), (exact.Direct, exact.DirectPrefix));
        // The exact word (§7), plus the word joined from a code's parts (confirmed); words merely starting with it never.
        Assert.Empty(exact.Groups);
        Assert.True(exact.Joins);
        Assert.True(Only("content:invoice", new SearchOptions { MatchWholeWord = true }).Lookup!.Joins); // the global option too
        var cased = Only("case:content:Invoice").Lookup!;
        Assert.True(cased.DirectNeedsVerify);

        var phrase = Only("content:\"annual report\"").Lookup!;
        Assert.Null(phrase.Direct);
        Assert.Equal(2, phrase.Groups.Count);
        Assert.Equal([("annual", false)], phrase.Groups[0].Parts);
        Assert.Equal([("report", true)], phrase.Groups[1].Parts);

        var code = Only("content:INV-2024-001").Lookup!;
        Assert.Equal("inv2024001", code.Direct);
        var codeParts = Assert.Single(code.Groups).Parts;
        Assert.Equal(["inv", "2024", "001"], codeParts.Select(p => p.Token));
        Assert.All(codeParts, p => Assert.True(p.Prefix));

        // A code inside a phrase: its parts, or a word starting with its compound ("e-mail" stands as "email" too).
        var mixed = Only("content:\"send e-mail now\"").Lookup!;
        Assert.Equal(3, mixed.Groups.Count);
        Assert.Equal(("mail", "email"), (Assert.Single(mixed.Groups[1].Parts).Token, mixed.Groups[1].Compound));

        var shortPart = Only("content:a-team").Lookup!;
        Assert.Equal("ateam", shortPart.Direct);
        Assert.Equal(["team"], Assert.Single(shortPart.Groups).Parts.Select(p => p.Token)); // "a" is only confirmed in the text

        var oneCjk = Only("content:東").Lookup!;
        Assert.Equal(("東", true), (oneCjk.Direct, oneCjk.DirectPrefix));
    }

    [Theory]
    [InlineData("regex:content:inv.*", "content: can't use regex")]
    [InlineData("content:x", "content: needs a word of at least 2 letters or digits")]
    [InlineData("content:5", "content: needs a word of at least 2 letters or digits")]
    [InlineData("content:\"a b\"", "content: needs a word of at least 2 letters or digits")]
    [InlineData("content:--", "content: needs a word of at least 2 letters or digits")]
    [InlineData("content:\"inv *\"", "content: wildcards work within one word")]
    [InlineData("content:inv,x*", "content: wildcards work within one word")]
    [InlineData("content:?", "content: needs a word of at least 2 letters or digits")]
    [InlineData("regex:title:x", "title: can't use regex")]
    [InlineData("width:abc", "width: invalid value 'abc'")]
    [InlineData("dimensions:big", "dimensions: invalid value 'big'")]
    [InlineData("orientation:sideways", "orientation: invalid value 'sideways'")]
    [InlineData("colormode:purple", "colormode: invalid value 'purple'")]
    [InlineData("colorspace:adobe", "colorspace: invalid value 'adobe'")]
    [InlineData("contentstatus:lost", "contentstatus: invalid value 'lost'")]
    [InlineData("datetaken:someday", "datetaken: invalid value 'someday'")]
    [InlineData("pagecount:many", "pagecount: invalid value 'many'")]
    public void Bad_values_are_query_errors(string query, string message)
    {
        var ex = Assert.Throws<QueryException>(() => Plan(query));
        Assert.StartsWith(message, ex.Message);
    }

    [Fact]
    public void Words_longer_than_the_index_keeps_are_an_error()
    {
        var ex = Assert.Throws<QueryException>(() => Plan("content:" + new string('a', 70)));
        Assert.Contains("aren't indexed", ex.Message);
    }

    [Fact]
    public void Globs_fold_like_the_index_and_drop_joiners()
    {
        var glob = Only("content:INV-20*");
        Assert.Equal(ContentTermKind.Glob, glob.Kind);
        Assert.Equal("inv20", glob.GlobPrefix);
        Assert.True(glob.Glob!.IsMatch("inv2024001"));
        Assert.Equal("caf", Only("content:Caf*").GlobPrefix);
        Assert.Equal("", Only("content:*voice").GlobPrefix);
        Assert.Equal(ContentTermKind.HasText, Only("content:*").Kind); // any word
    }

    [Fact]
    public void Term_kinds_and_needs()
    {
        Assert.Equal(ContentTermKind.HasText, Only("content:").Kind);
        Assert.Equal((ContentTermKind.Field, DocField.Title), (Only("title:annual").Kind, Only("title:annual").Field));
        Assert.Equal(DocField.Keywords, Only("tags:beach").Field);
        var camera = Only("camera:canon");
        Assert.Equal((DocField.CameraMake, DocField.CameraModel), (camera.Field, camera.AlsoField));
        Assert.Equal(PictureProp.Dpi, Only("dpi:<300").Prop);
        Assert.Equal([ColorMode.Cmyk], Only("cmyk:").Modes);
        Assert.Equal(ContentTermKind.Pages, Only("pagecount:>2").Kind);
        Assert.Equal(ContentTermKind.Status, Only("contentstatus:failed").Kind);

        Assert.Equal(ContentMode.Documents, Plan("content:x1 budget").NeedsMode);
        Assert.Equal(ContentMode.DocumentsAndPictures, Plan("content:x1 dpi:<300").NeedsMode);
        Assert.Equal(ContentMode.DocumentsAndPictures, Plan("camera:canon").NeedsMode);
        Assert.Equal(ContentMode.Documents, Plan("title:x1").NeedsMode);
        Assert.Equal(ContentMode.NamesOnly, Plan("ext:pdf").NeedsMode);
        // Plain words still match names: expanding them needs no mode (no hint in names-only mode).
        Assert.Equal(ContentMode.NamesOnly, Plan("budget report", mode: ContentMode.DocumentsAndPictures).NeedsMode);
        Assert.True(Plan("budget report").HasPlainExpansion);
    }

    [Fact]
    public void Statuses_failed_means_every_problem_and_not_equal_excludes()
    {
        var failed = Only("contentstatus:failed").Statuses!;
        Assert.Contains(DocStatus.PasswordProtected, failed);
        Assert.Contains(DocStatus.Crashed, failed);
        Assert.DoesNotContain(DocStatus.Ok, failed);
        Assert.DoesNotContain(DocStatus.NoText, failed);
        var notOk = Only("contentstatus:!=ok;truncated").Statuses!;
        Assert.DoesNotContain(DocStatus.Ok, notOk);
        Assert.Contains(DocStatus.NoText, notOk);
        Assert.Equal(Enum.GetValues<DocStatus>().Length, Only("contentstatus:").Statuses!.Length);
    }

    [Fact]
    public void Keys_tell_terms_and_flags_apart()
    {
        string K(string q) => Only(q, new SearchOptions { MatchContent = true }).Key;
        Assert.Equal(K("content:invoice"), K("content:invoice"));
        Assert.NotEqual(K("content:invoice"), K("ww:content:invoice"));
        Assert.NotEqual(K("content:invoice"), K("case:content:invoice"));
        Assert.NotEqual(K("content:invoice"), K("invoice"));        // plain words resolve differently
        Assert.NotEqual(K("title:invoice"), K("subject:invoice"));
        Assert.NotEqual(K("dpi:<300"), K("dpi:<301"));
        Assert.Equal(K("dpi:<300"), K("dpi:<300"));
    }

    static string DateKey(string query, DateTime now)
    {
        var o = new SearchOptions();
        return ContentPlan.Build(QueryParser.Parse(query, o), o, ContentMode.DocumentsAndPictures, now).Terms.Values.Single().Key;
    }

    [Fact]
    public void Dates_taken_share_a_key_exactly_while_their_periods_are_the_same()
    {
        // A relative date's key changes when its period does (the next day), never between two searches of one day.
        Assert.NotEqual(DateKey("datetaken:today", Now), DateKey("datetaken:today", Now.AddDays(1)));
        Assert.Equal(DateKey("datetaken:today", Now), DateKey("datetaken:today", Now.AddHours(1)));
        // An absolute date shares one key for ever: refreshing windows don't fill the cache with dead keys.
        Assert.Equal(DateKey("datetaken:2023", Now), DateKey("datetaken:2023", Now.AddDays(400)));
        Assert.Equal(DateKey("datetaken:>=2020-01-01;unknown", Now), DateKey("datetaken:>=2020-01-01;unknown", Now.AddMilliseconds(300)));
        Assert.NotEqual(DateKey("datetaken:2023", Now), DateKey("datetaken:2024", Now));
        // pastday moves with the clock itself.
        Assert.NotEqual(DateKey("datetaken:pastday", Now), DateKey("datetaken:pastday", Now.AddSeconds(1)));
        var o = new SearchOptions();
        var a = ContentPlan.Build(QueryParser.Parse("datetaken:today", o), o, ContentMode.DocumentsAndPictures, Now).Terms.Values.Single();
        var b = ContentPlan.Build(QueryParser.Parse("datetaken:today", o), o, ContentMode.DocumentsAndPictures, Now.AddDays(1)).Terms.Values.Single();
        Assert.Equal(a.StableKey, b.StableKey);
    }

    [Fact]
    public void Ten_searches_for_the_same_absolute_date_keep_one_cached_term()
    {
        var f = new ContentFixture();
        f.Doc(@"C:\Pictures\a.jpg", "", picture: new PictureInfo(10, 10, 72, 72, 8, ColorMode.Rgb, 1, 0, 0,
            new DateTime(2023, 6, 1, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(), false, 0, 0, 0, 0, 0, 0), status: DocStatus.NoText);
        f.Publish();
        var clock = Now;
        f.Engine.Clock = () => clock;
        for (int i = 0; i < 10; i++)
        {
            Assert.Single(f.Run("datetaken:2023").Ids);
            clock = clock.AddMilliseconds(300);
        }
        Assert.Equal(1, f.Source.Cache.TermCount);
    }

    [Fact]
    public void Dimensions_parse_both_axes_ranges_and_empty_sides()
    {
        var d = ContentPlan.ParseDims("dimensions", CompareOp.None, "1920x1080;>=3000x;x200..x300")!;
        Assert.Equal(3, d.Length);
        Assert.True(d[0].Eval(1920, 1080));
        Assert.False(d[0].Eval(1920, 1081));
        Assert.True(d[1].Eval(3000, 1));
        Assert.False(d[1].Eval(2999, 5000));
        Assert.True(d[2].Eval(5, 250));
        Assert.False(d[2].Eval(5, 301));
        var ge = ContentPlan.ParseDims("dimensions", CompareOp.GreaterOrEqual, "1920x1080")!;
        Assert.True(ge[0].Eval(1920, 1080));
        Assert.True(ge[0].Eval(4000, 3000));
        Assert.False(ge[0].Eval(4000, 1000));
        var range = ContentPlan.ParseDims("dimensions", CompareOp.None, "100x100..200x200")!;
        Assert.True(range[0].Eval(150, 150));
        Assert.False(range[0].Eval(250, 150));
        Assert.Null(ContentPlan.ParseDims("dimensions", CompareOp.None, ""));
    }
}

using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Search;
using Anything.Core.Content.Store;
using Anything.Core.Search;

namespace Anything.Tests.Content.Search;

public class ContentSearchTests
{
    /// <summary>A fixture for one test: fakes here; the integration tests run the same tests on a real ContentStore.</summary>
    private protected virtual ContentFixture NewFixture(DocSetCache? cache = null) => new(cache);

    const string Letter = @"C:\Docs\letter.docx", Notes = @"C:\Docs\notes.txt", Report = @"C:\Docs\report.pdf", Old = @"C:\Docs\old.pdf",
        Sheet = @"C:\Docs\budget-2024.xlsx", BudgetTxt = @"C:\Docs\budget.txt", Codes = @"C:\Docs\codes.txt", Spaced = @"C:\Docs\spaced.txt",
        Cafe = @"C:\Docs\cafe.txt", Scan = @"C:\Docs\scan.pdf", Secret = @"C:\Docs\secret.pdf", Archived = @"C:\Archive\notes.txt",
        InvoiceList = @"C:\Docs\invoice-list.txt", Tokyo = @"C:\Docs\tokyo.txt", Beach = @"C:\Pictures\beach.jpg";

    /// <summary>The standard corpus, split over two segments (ids 10 and 20).</summary>
    ContentFixture Build(Action<ContentFixture>? more = null, Func<uint, IReadOnlyList<FakeDoc>, FakeSegment>? make = null)
    {
        var f = NewFixture();
        f.Doc(Letter, "Dear client,\nplease find the invoice INV-2024-001 attached.", [(DocField.Title, "Invoice letter")]);
        f.Doc(Notes, "Meeting notes\nThe annual report is due.\nBudget approved.");
        f.Doc(Report, "The Annual Report 2023\fsecond page with Café", pages: 2);
        f.Doc(Old, "report first\fannual last", pages: 2);
        f.Doc(Sheet, "Summary\nTotal budget 1200\fDetails\nRent 800", pages: 2);
        f.Doc(BudgetTxt, "nothing here");
        f.NextSegment();
        f.Doc(Codes, "codes inv2024 and INV-2025-7");
        f.Doc(Spaced, "invoice INV 2024 paid");
        f.Doc(Cafe, "cafe au lait");
        f.Doc(Scan, "", status: DocStatus.NoText, pages: 3);
        f.Doc(Secret, "", status: DocStatus.PasswordProtected);
        f.Doc(Archived, "budget draft");
        f.Doc(Tokyo, "東京都に行きました。京都と東京。");
        f.Doc(Beach, "", [(DocField.Keywords, "beach; sunset"), (DocField.CameraMake, "Canon"), (DocField.CameraModel, "EOS R5")],
            new PictureInfo(4000, 3000, 72, 72, 8, ColorMode.Rgb, 1, 1, 0, 0, true, 1, 1, 0, 0, 0, 0), status: DocStatus.NoText);
        f.Tree.File(InvoiceList, 10);
        more?.Invoke(f);
        f.Publish(make);
        return f;
    }

    static void Same(string[] expected, string[] actual)
        => Assert.True(expected.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(actual.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            $"expected: {string.Join(" | ", expected)}\nactual:   {string.Join(" | ", actual)}");

    [Fact]
    public void Content_is_a_word_prefix_and_ww_is_the_exact_word()
    {
        var f = Build();
        Same([Letter, Spaced], f.Find("content:invo"));
        Same([Letter, Spaced], f.Find("ww:content:invoice"));
        Same([], f.Find("ww:content:invo"));
        Same([Report, Old, Notes], f.Find("content:report"));
        Same([], f.Find("content:repor1"));
    }

    [Fact]
    public void Content_never_looks_at_names()
    {
        var f = Build();
        Same([], f.Find("content:list"));          // invoice-list.txt has no stored text
        Same([InvoiceList], f.Find("invoice-list")); // a name match, Match inside files off
    }

    [Fact]
    public void Globs_match_within_one_word_of_the_dictionary()
    {
        var f = Build();
        Same([Letter], f.Find("content:inv*01"));      // the code's compound inv2024001
        Same([Letter, Spaced], f.Find("content:*voice"));
        Same([Letter, Codes], f.Find("content:inv-20*")); // joiners left out: compounds inv2024001, inv20257, word inv2024
        Same([Cafe, Report], f.Find("content:caf?"));
    }

    [Fact]
    public void Phrases_need_the_words_next_to_each_other()
    {
        var f = Build();
        Same([Report, Notes], f.Find("content:\"annual report\""));
        Same([Report, Notes], f.Find("content:\"annual rep\""));    // the last word is a prefix
        Same([], f.Find("ww:content:\"annual rep\""));
        Same([Notes], f.Find("content:\"report is due\""));
    }

    [Fact]
    public void Codes_match_whatever_their_separators()
    {
        var f = Build();
        // The compound finds INV-2024-001 and inv2024 directly; the AND of the parts, confirmed, finds "INV 2024".
        Same([Letter, Codes, Spaced], f.Find("content:INV-2024"));
        Same([Letter, Codes], f.Find("content:INV2024"));          // one word: no separator in the text either
        Same([Letter], f.Find("content:inv-2024-001"));
        Same([Codes], f.Find("content:INV-2025"));
    }

    [Fact]
    public void Case_and_diacritics_are_confirmed_in_the_stored_text()
    {
        var f = Build();
        Same([Report, Cafe], f.Find("content:cafe"));
        Same([Report, Cafe], f.Find("content:café"));
        Same([Report], f.Find("case:content:Café"));
        Same([Cafe], f.Find("case:content:cafe"));
        Same([Report], f.Find("diacritics:content:café"));
        Same([Cafe], f.Find("diacritics:content:cafe"));
        Same([Report], f.Find("content:café", new SearchOptions { MatchDiacritics = true }));
    }

    [Fact]
    public void Plain_words_under_match_case_or_diacritics_look_inside_files_with_those_rules()
    {
        var f = Build();
        var matchCase = new SearchOptions { MatchContent = true, MatchCase = true };
        Same([Report], f.Find("Café", matchCase));      // "cafe au lait" isn't Café; no name has it
        Same([Cafe], f.Find("cafe", matchCase));        // cafe.txt by name and inside; the report's Café isn't cafe
        var accents = new SearchOptions { MatchContent = true, MatchDiacritics = true };
        Same([Report], f.Find("café", accents));
        Same([Cafe], f.Find("cafe", accents));
        Assert.True(f.Run("Café", matchCase).NameMatches >= 0); // it looked inside files (the results are split)
        // Match path and Regex: names only, as the status bar says (cafe.txt by its path, not the report by its words).
        Same([Cafe], f.Find("Café", new SearchOptions { MatchContent = true, MatchPath = true }));
        Assert.Equal(-1, f.Run("Café", new SearchOptions { MatchContent = true, MatchPath = true }).NameMatches);
        Assert.Equal(-1, f.Run("Café", new SearchOptions { MatchContent = true, Regex = true }).NameMatches);
    }

    [Fact]
    public void Bad_content_values_are_errors_not_exceptions()
    {
        var f = Build();
        Assert.StartsWith("content: can't use regex", f.Run("regex:content:inv.*").Error);
        Assert.Equal("content: needs a word of at least 2 letters or digits", f.Run("content:x").Error);
        Assert.Equal("content: wildcards work within one word", f.Run("content:\"in v*\"").Error);
        Assert.Empty(f.Run("content:x").Ids);
    }

    [Fact]
    public void Empty_content_finds_files_with_words_and_statuses_find_the_rest()
    {
        var f = Build();
        Same([Letter, Notes, Report, Old, Sheet, BudgetTxt, Codes, Spaced, Cafe, Archived, Tokyo], f.Find("content:"));
        Same([Secret], f.Find("contentstatus:password"));
        Same([Scan, Beach], f.Find("contentstatus:notext"));
        Same([Secret], f.Find("contentstatus:failed"));
        Same([Scan, Beach, Secret], f.Find("!contentstatus:ok contentstatus:"));
        Same([Report, Old, Sheet, Scan], f.Find("pagecount:>=2"));
        Same([Scan], f.Find("pagecount:3"));
    }

    [Fact]
    public void Fields_are_searched_alone_and_as_plain_words()
    {
        var f = Build();
        Same([Letter], f.Find("title:invoice"));
        Same([], f.Find("title:dear"));
        Same([Beach], f.Find("keywords:sunset"));
        Same([Beach], f.Find("tags:beach"));
        Same([Beach], f.Find("content:sunset"));      // field words are words of the document too
        Same([Beach], f.Find("camera:canon"));
        Same([Beach], f.Find("camera:eos"));
        Same([Beach], f.Find("cameramodel:eos"));
        Same([], f.Find("cameramake:eos"));
        Same([Letter], f.Find("title:"));
        Same([Beach], f.Find("title:|keywords:sun*  !title:"));
    }

    [Fact]
    public void Bigram_scripts_are_found_by_bigrams_confirmed_in_the_run()
    {
        var f = Build();
        Same([Tokyo], f.Find("content:東京都"));
        Same([Tokyo], f.Find("content:京都"));
        Same([Tokyo], f.Find("content:東"));
        Same([], f.Find("content:都東"));
        // A plain word of two characters is one bigram (the dictionary answers); a longer run would need the stored
        // text to prove its bigrams adjacent, so it matches names only (A9), and content: finds it inside files.
        Same([Tokyo], f.Find("東京", ContentFixture.Plain));
        var longer = f.Run("東京都", ContentFixture.Plain);
        Assert.Empty(longer.Ids);
        Assert.Equal(-1, longer.NameMatches);
    }

    [Fact]
    public void A_plain_run_of_a_bigram_script_reads_no_stored_text()
    {
        // 東京 and 京都 in every document, never next to each other: confirming 東京都 would read every one of them.
        var f = NewFixture();
        for (int i = 0; i < 2000; i++) f.Doc($@"C:\Japan\memo{i:D4}.txt", "東京から新幹線で京都まで行きます");
        var store = f.Publish();
        var r = f.Run("東京都", ContentFixture.Plain);
        Assert.Empty(r.Ids);
        Assert.False(r.ContentPending);
        Assert.Equal(0, store.FakeSegments.Sum(s => s.Reads));
        Assert.Equal(2000, f.Run("東京", ContentFixture.Plain).Ids.Length);
        Assert.Equal(0, store.FakeSegments.Sum(s => s.Reads));
    }

    [Fact]
    public void Whole_word_content_is_the_exact_word_and_never_reads_longer_words()
    {
        // ww:content:report (or Match whole word) is an exact term lookup (§7): documents that only have "reporting" or
        // "reports" are not candidates, so nothing is decompressed for them.
        var f = NewFixture();
        for (int i = 0; i < 1000; i++) f.Doc($@"C:\Reports\r{i:D4}.txt", i % 2 == 0 ? "the reporting period ends" : "quarterly reports are due");
        f.Doc(@"C:\Reports\exact.txt", "the report itself");
        var store = f.Publish();
        Assert.Equal([@"C:\Reports\exact.txt"], f.Find("ww:content:report"));
        Assert.Equal([@"C:\Reports\exact.txt"], f.Find("content:report", new SearchOptions { MatchWholeWord = true }));
        Assert.Equal(0, store.FakeSegments.Sum(s => s.Reads));
        Assert.Equal(1001, f.Run("content:report").Ids.Length);
        Assert.Equal(0, store.FakeSegments.Sum(s => s.Reads));
    }

    [Fact]
    public void Whole_word_content_still_finds_the_word_joined_from_a_code()
    {
        var f = NewFixture();
        f.Doc(@"C:\Inv\a.txt", "invoice INV-2024-001 paid");       // inv + 2024: the next part ends the word
        f.Doc(@"C:\Inv\b.txt", "ref INV-2024-001-A");               // inv2024001: the compound and its next part 2024
        f.Doc(@"C:\Inv\c.txt", "version v1.2.3 shipped");           // v12: v1 then a part of one character
        f.Doc(@"C:\Inv\d.txt", "inventory of 2024 and inv2024001"); // the words are all there, never joined as whole words
        f.Doc(@"C:\Inv\e.txt", "INV 2024 with spaces");
        f.Publish();
        Same([@"C:\Inv\a.txt", @"C:\Inv\b.txt"], f.Find("ww:content:inv2024"));
        Same([@"C:\Inv\a.txt", @"C:\Inv\b.txt", @"C:\Inv\d.txt"], f.Find("ww:content:inv2024001"));
        Same([@"C:\Inv\c.txt"], f.Find("ww:content:v12"));
        Same([@"C:\Inv\c.txt"], f.Find("ww:content:v123")); // the whole code's compound: an exact word
        Same([@"C:\Inv\a.txt", @"C:\Inv\b.txt", @"C:\Inv\e.txt"], f.Find("ww:content:inv"));
    }

    [Fact]
    public void Whole_word_joins_past_the_cap_answer_with_the_exact_word()
    {
        // "in" and words starting with "voice" in every document: far more candidates than the cap, so ww:content:invoice
        // is the exact word alone, and nothing is read.
        var f = NewFixture();
        int many = ContentResolve.MaxJoinCandidates + 50;
        for (int i = 0; i < many; i++) f.Doc($@"C:\Mail\m{i:D4}.txt", "a voicemail came in today");
        f.Doc(@"C:\Mail\exact.txt", "the invoice is in");
        var store = f.Publish();
        Assert.Equal([@"C:\Mail\exact.txt"], f.Find("ww:content:invoice"));
        Assert.Equal(0, store.FakeSegments.Sum(s => s.Reads));
    }

    [Fact]
    public void Plain_words_find_names_or_content_with_names_first()
    {
        var f = Build();
        var r = f.Run("budget", ContentFixture.Plain);
        Assert.Null(r.Error);
        // Both notes.txt tie by name: the lower id (created first) comes first, as in every name sort.
        Assert.Equal([Sheet, BudgetTxt, Notes, Archived], r.Ids.Select(f.Index.GetFullPath));
        Assert.Equal(2, r.NameMatches);
        Assert.False(r.ContentPending);

        var namesOnly = f.Run("budget");
        Assert.Equal([Sheet, BudgetTxt], namesOnly.Ids.Select(f.Index.GetFullPath));
        Assert.Equal(-1, namesOnly.NameMatches);
    }

    [Fact]
    public void Plain_words_that_do_not_expand_leave_name_matches_alone()
    {
        var f = Build();
        Assert.Equal(-1, f.Run("bu", ContentFixture.Plain).NameMatches);
        Assert.Equal(-1, f.Run("content:budget", ContentFixture.Plain).NameMatches); // explicit content: alone gives no split
        Assert.Equal(-1, f.Run(@"Docs\budget", ContentFixture.Plain).NameMatches);
        // Under a NOT a word is never expanded: notes.txt (budget inside, not in its name) is kept by !budget.
        var negated = f.Run("!budget", ContentFixture.Plain);
        Assert.Equal(-1, negated.NameMatches);
        Assert.Contains(Notes, negated.Ids.Select(f.Index.GetFullPath));
        Assert.DoesNotContain(Sheet, negated.Ids.Select(f.Index.GetFullPath));
    }

    [Fact]
    public void Codes_typed_as_plain_words_use_their_compound_only()
    {
        var f = Build();
        // INV-2024 as a plain word: the compound inv2024 finds INV-2024-001 and inv2024, never "INV 2024" (no verification).
        var r = f.Run("INV-2024", ContentFixture.Plain);
        Same([Letter, Codes], [.. r.Ids.Select(f.Index.GetFullPath)]);
        Assert.Equal(0, r.NameMatches);
        // Typing on: "invoice-l" drops the trailing one-letter part and looks up "invoice": names first, then the content.
        var typing = f.Run("invoice-l", ContentFixture.Plain);
        Assert.Equal(InvoiceList, f.Index.GetFullPath(typing.Ids[0]));
        Assert.Equal(1, typing.NameMatches);
        Same([InvoiceList, Letter, Spaced], [.. typing.Ids.Select(f.Index.GetFullPath)]);
    }

    public static TheoryData<int, bool> SortPaths => new() { { 0, false }, { 0, true }, { int.MaxValue, false }, { int.MaxValue, true } };

    [Theory]
    [MemberData(nameof(SortPaths))]
    public void Name_matches_come_first_on_both_sort_paths_in_both_directions(int sortCacheThreshold, bool descending)
    {
        var f = NewFixture();
        string[] named = [@"C:\A\report-b.txt", @"C:\A\report-a.txt", @"C:\B\Report-c.txt", @"C:\B\reporter.txt"];
        string[] inside = [@"C:\A\minutes.txt", @"C:\B\agenda.txt", @"C:\A\zeta.txt", @"C:\B\alpha.txt"];
        foreach (var n in named) f.Tree.File(n, 1);
        f.Tree.File(@"C:\A\other.txt", 1);
        foreach (var n in inside) f.Doc(n, "the yearly report is here");
        f.Doc(@"C:\A\report-a.txt", "also a report inside"); // matches both ways: by name
        f.Publish();
        f.Engine.SortCacheThreshold = sortCacheThreshold;

        foreach (var sort in new[] { SortKey.Name, SortKey.Path, SortKey.Size })
        {
            var r = f.Run("report", ContentFixture.Plain, sort, descending);
            var names = f.Run("report", new SearchOptions(), sort, descending).Ids;
            var all = f.Run("report|content:report", new SearchOptions(), sort, descending).Ids;
            Assert.Equal(named.Length, r.NameMatches);
            // Each group keeps the user's order: the name group is the names-only search, the rest the full order minus it.
            Assert.Equal(names, r.Ids.Take(r.NameMatches));
            Assert.Equal(all.Where(id => !names.Contains(id)), r.Ids.Skip(r.NameMatches));
            Assert.Equal(named.Length + inside.Length, r.Ids.Length);
        }
    }

    [Fact]
    public void Dupe_filters_before_the_partition()
    {
        var f = Build();
        var r = f.Run("budget dupe:", ContentFixture.Plain);
        Same([Notes, Archived], [.. r.Ids.Select(f.Index.GetFullPath)]);
        Assert.Equal(0, r.NameMatches);
    }

    [Fact]
    public void A_stale_map_matches_nothing_inside_files_and_says_so()
    {
        var f = Build();
        f.Source.MapOverride = (_, _) => null;
        var r = f.Run("content:invoice");
        Assert.Empty(r.Ids);
        Assert.True(r.ContentPending);
        var plain = f.Run("budget", ContentFixture.Plain);
        Same([Sheet, BudgetTxt], [.. plain.Ids.Select(f.Index.GetFullPath)]);
        Assert.True(plain.ContentPending);
        Assert.False(f.Run("budget").ContentPending);
    }

    [Fact]
    public void A_map_missing_a_segment_skips_its_documents_and_says_so()
    {
        var f = Build();
        f.Source.MapOverride = (index, view) =>
        {
            var b = DocIdMap.Empty(index.Generation).ToBuilder();
            var full = view.Map;
            uint first = view.Store.Segments[0].Id;
            b.AddSegment(first, view.Store.Segments[0].DocCount);
            for (int d = 0; d < view.Store.Segments[0].DocCount; d++) b.Set(first, d, full.FileId(first, d));
            return b.Build(index.Generation); // the second segment is newer than this map
        };
        var r = f.Run("content:invoice");
        Same([Letter], [.. r.Ids.Select(f.Index.GetFullPath)]); // Spaced is in segment 20
        Assert.True(r.ContentPending);
    }

    [Fact]
    public void A_compact_between_the_prefetch_and_the_lock_maps_to_the_new_ids()
    {
        var f = Build();
        var before = f.Find("content:report");
        var beforeIds = f.Run("content:report").Ids.Order().ToArray();
        int letter = f.Tree.Id(Letter); // created before the matches: removing it moves their ids down
        long generation = f.Index.Generation;
        bool compacted = false;
        f.Probe = stage =>
        {
            if (stage != "before-lock" || compacted) return;
            compacted = true;
            f.Index.Remove(letter);
            f.Index.Compact(); // the fake source translates its map in the Compacted handler, as the real one does
        };
        var after = f.Run("content:report");
        Assert.True(compacted);
        Assert.NotEqual(generation, f.Index.Generation);
        Assert.Null(after.Error);
        Same(before, [.. after.Ids.Select(f.Index.GetFullPath)]);
        Assert.NotEqual(beforeIds, after.Ids.Order().ToArray()); // the ids really moved
        Assert.False(after.ContentPending);
    }

    [Fact]
    public void Cancellation_inside_resolving_propagates()
    {
        var f = Build();
        using var cts = new CancellationTokenSource();
        bool reachedLock = false;
        f.Probe = stage => reachedLock |= stage == "before-lock";
        foreach (var s in f.View!.FakeSegments) s.OnTerms = cts.Cancel;
        Assert.ThrowsAny<OperationCanceledException>(() => f.Run("content:invoice", ct: cts.Token));
        Assert.False(reachedLock, "the search went on to the index lock after it was cancelled while resolving");
    }

    [Fact]
    public void Cancellation_inside_verification_propagates()
    {
        var f = Build();
        using var cts = new CancellationTokenSource();
        bool reachedLock = false;
        f.Probe = stage => reachedLock |= stage == "before-lock";
        foreach (var s in f.View!.FakeSegments) s.OnRead = _ => cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => f.Run("content:\"annual report\"", ct: cts.Token));
        Assert.False(reachedLock, "the search went on to the index lock after it was cancelled while verifying");
    }

    [Fact]
    public void Segments_made_by_other_word_rules_are_missing_not_empty()
    {
        // Segment 20 has an older tokenizer version (rebuilt through merges meanwhile): its words can't be asked yet.
        var f = Build(more: null, make: (id, docs) => new FakeSegment(id, docs)
        {
            TokenizerVersion = docs.Any(d => d.Path == Codes) ? ContentTokenizer.Version - 1 : ContentTokenizer.Version,
        });
        var r = f.Run("content:invoice");
        Same([Letter], [.. r.Ids.Select(f.Index.GetFullPath)]); // Spaced (segment 20) is missing
        Assert.True(r.ContentPending);
        var key = ContentPlan.Build(QueryParser.Parse("content:invoice", new SearchOptions()), new SearchOptions(), ContentMode.Documents, ContentFixture.Now)
            .Terms.Values.Single().Key;
        Assert.True(f.Source.Cache.TryGet(key, f.View!.Segments[0].Id, out _));
        Assert.False(f.Source.Cache.TryGet(key, f.View.Segments[1].Id, out _)); // never cached as having none
        Assert.True(f.Run("budget", ContentFixture.Plain).ContentPending);
        // Picture details and rows don't depend on the word rules.
        var pictures = f.Run("gps:");
        Same([Beach], [.. pictures.Ids.Select(f.Index.GetFullPath)]);
        Assert.False(pictures.ContentPending);
    }

    [Fact]
    public void Wildcards_and_prefixes_over_many_words_read_their_postings_in_batches()
    {
        int words = 2 * ContentResolve.TermBatch + 500;
        var f = NewFixture();
        for (int i = 0; i < words; i++) f.Doc($@"C:\W\d{i:D5}.txt", $"w{i:D5}x");
        var store = f.Publish();
        var seg = store.FakeSegments[0];
        Assert.Equal(words, f.Run("content:w0").Ids.Length);
        Assert.Equal(3, seg.TermScans); // three batches of the range, each read before the next is visited
        Assert.Equal(words, f.Run("content:w*x").Ids.Length);
        Assert.Equal(Enumerable.Range(0, words).Count(i => i % 10 == 0), f.Run("content:*0x").Ids.Length); // a full scan, filtered
    }

    [Fact]
    public void Nothing_is_decompressed_while_the_index_lock_is_held()
    {
        var f = Build();
        f.Engine.ContentBudget = TimeSpan.FromSeconds(30); // no lookup may still run in the background while a search holds the lock
        Same([Report, Notes], f.Find("content:\"annual report\""));
        Same([Report], f.Find("case:content:Café"));
        Same([Letter, Codes, Spaced], f.Find("content:INV-2024"));
        Same([Tokyo], f.Find("content:東京都"));
        Same([Letter, Codes], f.Find("ww:content:inv2024")); // INV-2024-001 confirmed (joined), inv2024 exact; INV 2024 refused
        Assert.True(f.View!.FakeSegments.Sum(s => s.Reads) > 0, "nothing was verified at all");
        Assert.Equal(0, f.DecompressedUnderLock);
    }

    [Fact]
    public void Get_highlights_is_unchanged()
    {
        var f = Build();
        Assert.Empty(f.Engine.GetHighlights("content:report", new SearchOptions(), "report.pdf"));
        Assert.Empty(f.Engine.GetHighlights("title:report", new SearchOptions(), "report.pdf"));
        Assert.Equal([(0, 6)], f.Engine.GetHighlights("report", ContentFixture.Plain, "report.pdf"));
        Assert.Equal([(0, 6)], f.Engine.GetHighlights("report content:x1", ContentFixture.Plain, "report.pdf"));
    }

    [Fact]
    public void The_filter_never_looks_inside_files_but_its_content_functions_work()
    {
        var f = Build();
        var withFilter = new SearchOptions { MatchContent = true, Filter = "invoice" };
        Same([InvoiceList], f.Find("", withFilter));
        Same([InvoiceList], f.Find("list", withFilter));
        Assert.Equal(-1, f.Run("", withFilter).NameMatches);
        Same([Letter, Spaced], f.Find("", new SearchOptions { Filter = "content:invoice" }));
        Same([Letter], f.Find("letter", new SearchOptions { MatchContent = true, Filter = "content:invoice" }));
    }

    [Fact]
    public void Catching_up_marks_content_results_pending()
    {
        var f = Build();
        f.Source.CatchingUpValue = true;
        Assert.True(f.Run("content:invoice").ContentPending);
        Assert.True(f.Run("budget", ContentFixture.Plain).ContentPending);
        Assert.False(f.Run("budget").ContentPending);
        Assert.False(f.Run("ext:txt", ContentFixture.Plain).ContentPending);
    }

    [Fact]
    public void Without_a_view_content_matches_nothing_and_is_pending_while_the_mode_is_on()
    {
        var f = Build();
        f.Source.Publish(null);
        var r = f.Run("content:invoice");
        Assert.Null(r.Error);
        Assert.Empty(r.Ids);
        Assert.True(r.ContentPending);
        var plain = f.Run("budget", ContentFixture.Plain);
        Same([Sheet, BudgetTxt], [.. plain.Ids.Select(f.Index.GetFullPath)]);
        Assert.Equal(-1, plain.NameMatches);
        Assert.True(plain.ContentPending);
    }

    [Fact]
    public void Names_only_mode_and_no_index_match_nothing_silently()
    {
        var f = Build();
        f.Source.ModeValue = ContentMode.NamesOnly;
        var r = f.Run("content:invoice");
        Assert.Null(r.Error);
        Assert.Empty(r.Ids);
        Assert.False(r.ContentPending);
        Assert.Equal(-1, f.Run("budget", ContentFixture.Plain).NameMatches);
        Same([Letter], f.Find("!content:invoice letter"));

        f.Index.Content = null;
        Assert.Null(f.Run("content:invoice").Error);
        Assert.Empty(f.Run("dpi:<300").Ids);
        Assert.Null(f.Run("content:x").Error); // no index: nothing to tell
        Same([Sheet, BudgetTxt], f.Find("budget", ContentFixture.Plain));
    }

    [Fact]
    public void Deleted_documents_are_skipped()
    {
        var f = Build();
        f.Delete(Letter);
        Same([Spaced], f.Find("content:invoice"));
    }

    [Fact]
    public void Content_terms_combine_with_names_and_other_functions()
    {
        var f = Build();
        Same([Letter], f.Find("content:invoice ext:docx"));
        Same([Spaced], f.Find("content:invoice !ext:docx"));
        Same([Letter, Spaced, BudgetTxt], f.Find("content:invoice | budget.txt"));
        Same([Notes, Archived], f.Find("content:budget !budget"));
        Same([Report, Notes], f.Find("<content:annual content:report> !old"));
    }

    [Fact]
    public void Plain_and_explicit_terms_mix()
    {
        var f = Build();
        // "approved" only inside notes.txt; "notes" by name: AND of both.
        var r = f.Run("notes approved", ContentFixture.Plain);
        Same([Notes], [.. r.Ids.Select(f.Index.GetFullPath)]);
        Assert.Equal(0, r.NameMatches); // "approved" matched inside the file: not by name
        var both = f.Run("notes content:approved", ContentFixture.Plain);
        Assert.Equal(1, both.NameMatches); // the explicit content: term counts as matched in the name-first pass
    }
}

/// <summary>Search tests over views made by hand (not through a fixture's store), so they run once, on fakes.</summary>
public class ContentSearchHandMadeViewTests
{
    /// <summary>A names index of n files, each a document "budget" in one fake segment, published on a fake source.</summary>
    static (FileIndex Index, FakeStoreView Store) BigIndex(int n)
    {
        var index = new FileIndex();
        var ids = new int[n];
        index.BulkAdd(add =>
        {
            int root = add.Add("C:", FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
            for (int i = 0; i < n; i++) ids[i] = add.Add($"file{i}.txt", root, FileAttributes.Archive, 10, 133_500_000_000_000_000L, 0);
        });
        var docs = new List<FakeDoc>(n);
        for (int i = 0; i < n; i++)
            docs.Add(new FakeDoc($"file{i}.txt", new DocRow((ulong)i, 0, 10, 0, 0, ContentKind.Text, DocStatus.Ok, 1, DocFlags.HasText),
                new StoredDoc("budget", [], null)));
        var map = DocIdMap.Empty(index.Generation).ToBuilder();
        map.AddSegment(1, n);
        for (int d = 0; d < n; d++) map.Set(1, d, ids[d]);
        var store = new FakeStoreView([new FakeSegment(1, docs)], 1);
        var source = new FakeContentSource();
        source.Publish(new ContentView(store, map.Build(index.Generation), ContentMode.Documents));
        index.Content = source;
        return (index, store);
    }

    [Fact]
    public void Cancellation_inside_prepare_propagates()
    {
        // More documents than Prepare maps between two checks of the token, so only its own check can stop it.
        var (index, store) = BigIndex(70_000);
        var options = new SearchOptions();
        var query = QueryParser.Parse("content:budget", options);
        using (var prefetch = ContentSearch.Begin(index, query, options, DateTime.Now, default, null, TimeSpan.FromSeconds(10))!)
        {
            var set = prefetch.For(query)!;
            Assert.Equal(70_000, set.Count);
            index.Lock.EnterReadLock();
            try
            {
                var ctx = new SearchContext(index, new FolderPathCache(index), DateTime.Now);
                ContentSearch.Attach(ctx, prefetch);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => new ContentSetMatcher(set).Prepare(ctx, cancelled.Token));
            }
            finally { index.Lock.ExitReadLock(); }
        }

        // Through the engine: cancelled while Prepare maps the documents, it stops there, before the scan.
        var engine = new SearchEngine { ContentBudget = TimeSpan.FromSeconds(10) };
        using var cts = new CancellationTokenSource();
        engine.Probe = stage => { if (stage == "locked") store.OnIsDeleted = () => { cts.Cancel(); store.OnIsDeleted = null; }; };
        var ex = Assert.ThrowsAny<OperationCanceledException>(() => engine.Search(index, "content:budget", options, SortKey.Name, false, cts.Token));
        Assert.Contains(nameof(ContentSetMatcher) + "." + nameof(ContentSetMatcher.Prepare), ex.StackTrace);
    }

    [Fact]
    public void A_term_in_use_keeps_sets_only_for_the_segments_of_its_view()
    {
        // Each commit merges everything into one new segment: the term's sets of segments merged away are dropped.
        var f = new ContentFixture();
        int a = f.Tree.File(@"C:\M\a.txt", 1), b = f.Tree.File(@"C:\M\b.txt", 1);
        var docs = new List<FakeDoc>
        {
            new(@"C:\M\a.txt", new DocRow(1, 0, 1, 0, 0, ContentKind.Text, DocStatus.Ok, 1, DocFlags.HasText), new StoredDoc("budget one", [], null)),
            new(@"C:\M\b.txt", new DocRow(2, 0, 1, 0, 0, ContentKind.Text, DocStatus.Ok, 1, DocFlags.HasText), new StoredDoc("budget two", [], null)),
        };
        f.Index.Content = f.Source;
        var key = ContentPlan.Build(QueryParser.Parse("content:budget", new SearchOptions()), new SearchOptions(), ContentMode.Documents, ContentFixture.Now)
            .Terms.Values.Single().Key;
        for (uint id = 100; id < 140; id++)
        {
            var map = DocIdMap.Empty(f.Index.Generation).ToBuilder();
            map.AddSegment(id, 2);
            map.Set(id, 0, a);
            map.Set(id, 1, b);
            f.Source.Publish(new ContentView(new FakeStoreView([new FakeSegment(id, docs)], id), map.Build(f.Index.Generation), ContentMode.Documents));
            Assert.Equal(2, f.Run("content:budget").Ids.Length);
            Assert.Equal(1, f.Source.Cache.SetCount(key));
        }
    }
}

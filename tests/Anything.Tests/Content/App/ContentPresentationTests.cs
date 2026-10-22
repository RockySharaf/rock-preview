using System.Globalization;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Search;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.App;

/// <summary>Sets the current culture for one test (numbers are formatted in it) and puts it back.</summary>
internal sealed class CultureScope : IDisposable
{
    readonly CultureInfo _old = CultureInfo.CurrentCulture;
    public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
    public void Dispose() => CultureInfo.CurrentCulture = _old;
}

public sealed class ContentPresentationTests
{
    static ContentEstimate Est(ContentMode mode, int docs, int pictures, long bytes, TimeSpan time, long network = 0, bool atLeast = false)
        => new(mode, docs, pictures, bytes, time, network, atLeast);

    static readonly ContentSettings Defaults = new();

    // ---- sizes --------------------------------------------------------------------------------

    [Theory]
    [InlineData(0L, "under 10 MB")]
    [InlineData(-5L, "under 10 MB")]
    [InlineData(9_999_999L, "under 10 MB")]
    [InlineData(10_000_000L, "10 MB")]
    [InlineData(27_455_953L, "27 MB")]       // §9 My folders, documents (the formula's 27.46 MB)
    [InlineData(30_190_000L, "30 MB")]       // with pictures
    [InlineData(113_500_000L, "110 MB")]     // E:\ (two significant figures)
    [InlineData(99_600_000L, "100 MB")]
    [InlineData(499_000_000L, "0.5 GB")]     // 500 MB and up is in GB
    [InlineData(750_000_000L, "0.8 GB")]     // the print shop
    [InlineData(1_234_567_890L, "1.2 GB")]
    [InlineData(12_340_000_000L, "12.3 GB")]
    [InlineData(99_960_000_000L, "100 GB")]
    [InlineData(204_000_000_000L, "200 GB")] // the print shop's network reads
    [InlineData(999_600_000_000L, "1.0 TB")]
    [InlineData(10_000_000_000_000L, "10.0 TB")]
    [InlineData(250_000_000_000_000L, "250 TB")]
    public void Size_follows_the_ladder(long bytes, string expected)
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal(expected, ContentPresentation.Size(bytes));
    }

    [Fact]
    public void Size_uses_the_current_cultures_decimal_separator()
    {
        using var _ = new CultureScope("de-DE");
        Assert.Equal("0,8 GB", ContentPresentation.Size(750_000_000));
    }

    // ---- durations ------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "under a minute")]
    [InlineData(30, "1 minute")]
    [InlineData(60, "1 minute")]
    [InlineData(61, "2 minutes")]
    [InlineData(180, "3 minutes")]           // exactly 3 minutes stays 3
    [InlineData(137.4, "3 minutes")]         // §9: 91.6 s × 1.5
    [InlineData(303, "6 minutes")]           // §9: 5.05 minutes
    [InlineData(599, "10 minutes")]
    [InlineData(600, "10 minutes")]
    [InlineData(1338, "25 minutes")]         // §9: 22.3 minutes
    [InlineData(3300, "55 minutes")]
    [InlineData(3480, "1 hour")]             // 58 minutes round up to the hour
    [InlineData(3660, "2 hours")]            // always up
    [InlineData(33 * 3600, "33 hours")]      // the print shop: reading time, not a calendar
    [InlineData(48 * 3600, "48 hours")]
    [InlineData(49 * 3600, "3 days")]
    [InlineData(72 * 3600, "3 days")]
    [InlineData(73 * 3600, "4 days")]
    public void Duration_rounds_up_and_never_counts_seconds(double seconds, string expected)
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal(expected, ContentPresentation.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Duration_of_a_negative_time_is_under_a_minute()
        => Assert.Equal("under a minute", ContentPresentation.Duration(TimeSpan.FromSeconds(-3)));

    [Fact]
    public void Long_first_passes_are_said_as_reading_time()
    {
        Assert.Equal("the first pass takes about 3 minutes in the background", ContentPresentation.FirstPass(TimeSpan.FromMinutes(3), false));
        Assert.Equal("the first pass is about 33 hours of reading while this PC is on, slower while you use it",
            ContentPresentation.FirstPass(TimeSpan.FromHours(33), false));
        Assert.StartsWith("the first pass takes at least", ContentPresentation.FirstPass(TimeSpan.FromMinutes(3), true));
    }

    // ---- the choices -------------------------------------------------------------------------------

    [Fact]
    public void The_three_titles_are_distinct_and_used_by_the_hints()
    {
        var titles = Enum.GetValues<ContentMode>().Select(ContentPresentation.OptionTitle).ToList();
        Assert.Equal(3, titles.Distinct().Count());
        Assert.Equal("Search files by name", ContentPresentation.OptionTitle(ContentMode.NamesOnly));
        Assert.Contains("“" + ContentPresentation.OptionTitle(ContentMode.Documents) + "”", ContentPresentation.ModeHint(ContentMode.Documents));
        Assert.Contains("“" + ContentPresentation.OptionTitle(ContentMode.DocumentsAndPictures) + "”", ContentPresentation.ModeHint(ContentMode.DocumentsAndPictures));
        Assert.Contains("Settings › Indexing", ContentPresentation.ModeHint(ContentMode.Documents));
        Assert.Equal("", ContentPresentation.ModeHint(ContentMode.NamesOnly));
    }

    [Fact]
    public void Worked_examples_read_as_in_the_spec()
    {
        using var _ = new CultureScope("en-US");
        // §9 recomputed with the formula (amendments): about 27 MB, about 30 MB, about 110 MB, about 0.8 GB.
        Assert.Equal("About 27 MB for 5,230 documents; the first pass takes about 3 minutes in the background.",
            ContentPresentation.OptionDetail(ContentMode.Documents, Est(ContentMode.Documents, 5230, 14000, 27_455_953, TimeSpan.FromSeconds(137.4)), Defaults));
        Assert.Equal("About 30 MB in all, with 14,000 pictures; the first pass takes about 6 minutes in the background.",
            ContentPresentation.OptionDetail(ContentMode.DocumentsAndPictures, Est(ContentMode.DocumentsAndPictures, 5230, 14000, 30_190_000, TimeSpan.FromSeconds(303)), Defaults));
        Assert.Equal("About 110 MB for 23,467 documents; the first pass takes about 25 minutes in the background.",
            ContentPresentation.OptionDetail(ContentMode.Documents, Est(ContentMode.Documents, 23467, 48617, 113_500_000, TimeSpan.FromSeconds(1338)), Defaults));
        Assert.Equal("About 0.8 GB for 850,000 documents; the first pass is about 33 hours of reading while this PC is on, slower while you use it. "
                     + "Reads about 200 GB over the network (at most 8 MB/s).",
            ContentPresentation.OptionDetail(ContentMode.Documents, Est(ContentMode.Documents, 850_000, 0, 750_000_000, TimeSpan.FromHours(33), 204_000_000_000), Defaults));
    }

    [Fact]
    public void Details_say_so_while_counting_and_when_nothing_is_there()
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal("Counting your files…", ContentPresentation.OptionDetail(ContentMode.Documents, null, Defaults));
        Assert.Equal("Counting your files…", ContentPresentation.OptionDetail(ContentMode.Documents, Est(ContentMode.Documents, 0, 0, 0, TimeSpan.Zero, atLeast: true), Defaults));
        Assert.StartsWith("Nothing to read", ContentPresentation.OptionDetail(ContentMode.Documents, Est(ContentMode.Documents, 0, 0, 0, TimeSpan.Zero), Defaults));
        Assert.Equal("At least 27 MB for 5,230 documents so far; the first pass takes at least 3 minutes in the background.",
            ContentPresentation.OptionDetail(ContentMode.Documents, Est(ContentMode.Documents, 5230, 0, 27_455_953, TimeSpan.FromMinutes(3), atLeast: true), Defaults));
        Assert.Equal("Under 10 MB for 1 document; the first pass takes about 1 minute in the background.",
            ContentPresentation.OptionDetail(ContentMode.Documents, Est(ContentMode.Documents, 1, 0, 2_000, TimeSpan.FromSeconds(1)), Defaults));
        Assert.Contains("no pictures", ContentPresentation.OptionDetail(ContentMode.DocumentsAndPictures, Est(ContentMode.DocumentsAndPictures, 10, 0, 20_000_000, TimeSpan.FromMinutes(1)), Defaults));
    }

    [Fact]
    public void Names_only_says_what_it_deletes()
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal("Instant, as now. Uses no extra space.", ContentPresentation.OptionDetail(ContentMode.NamesOnly, null, Defaults));
        Assert.Equal("Instant, as now. Choosing this deletes the 28 MB of text already read.",
            ContentPresentation.OptionDetail(ContentMode.NamesOnly, Est(ContentMode.NamesOnly, 0, 0, 28_000_000, TimeSpan.Zero), Defaults));
        Assert.Equal("Instant, as now. Choosing this deletes the text already read (under 10 MB).",
            ContentPresentation.OptionDetail(ContentMode.NamesOnly, Est(ContentMode.NamesOnly, 0, 0, 3_000_000, TimeSpan.Zero), Defaults));
    }

    [Fact]
    public void The_network_limit_shows_in_the_detail()
    {
        using var _ = new CultureScope("en-US");
        var slow = new ContentSettings { NetworkKilobytesPerSecond = 512 };
        Assert.Contains("(at most 512 KB/s)", ContentPresentation.OptionDetail(ContentMode.Documents, Est(ContentMode.Documents, 10, 0, 20_000_000, TimeSpan.FromMinutes(5), 50_000_000), slow));
    }

    [Fact]
    public void No_network_limit_says_no_limit()
    {
        using var _ = new CultureScope("en-US");
        // Settings stores "No limit" as a very large number: it must not read "at most 976.6 MB/s".
        var unlimited = new ContentSettings { NetworkKilobytesPerSecond = ContentPresentation.NoNetworkLimitKbps };
        var nas = Est(ContentMode.Documents, 850_000, 0, 750_000_000, TimeSpan.FromHours(33), 204_000_000_000);
        string detail = ContentPresentation.OptionDetail(ContentMode.Documents, nas, unlimited);
        Assert.EndsWith("Reads about 200 GB over the network.", detail);
        Assert.DoesNotContain("at most", detail);
        string added = ContentPresentation.AddedFolder(@"\\nas\jobs", nas, false, ContentPresentation.NoNetworkLimitKbps);
        Assert.Contains("; reads about 200 GB over the network.", added);
        Assert.DoesNotContain("at most", added);
        Assert.DoesNotContain("976", added);
    }

    [Fact]
    public void The_mode_card_lists_every_choice_with_its_cost()
    {
        using var _ = new CultureScope("en-US");
        string text = ContentPresentation.ModeCardDescription(
            Est(ContentMode.Documents, 5230, 14000, 27_455_953, TimeSpan.FromSeconds(137.4)),
            Est(ContentMode.DocumentsAndPictures, 5230, 14000, 30_190_000, TimeSpan.FromSeconds(303)),
            indexBytesNow: 0);
        Assert.Equal("Search files by name: no extra space. Inside documents: about 27 MB for 5,230 documents, about 3 minutes. "
                     + "With pictures' details: about 30 MB in all, with 14,000 pictures, about 6 minutes.", text);
        Assert.Equal("Search files by name: no extra space. Inside documents: counting… With pictures' details: counting…",
            ContentPresentation.ModeCardDescription(null, null, 0));
        Assert.Contains("deletes the 28 MB text index there is now", ContentPresentation.ModeCardDescription(null, null, 28_000_000));
    }

    [Fact]
    public void The_mode_card_prices_a_switch_from_the_choice_in_use_not_a_pass_from_scratch()
    {
        using var _ = new CultureScope("en-US");
        var documents = Est(ContentMode.Documents, 4047, 13_073, 29_000_000, TimeSpan.FromSeconds(77));
        var pictures = Est(ContentMode.DocumentsAndPictures, 4047, 13_073, 32_000_000, TimeSpan.FromSeconds(130));
        Assert.Equal("Search files by name: no extra space (deletes the 29 MB text index there is now). "
                     + "Inside documents: about 29 MB for 4,047 documents, in use. "
                     + "With pictures' details: about 32 MB in all, with 13,073 pictures; switching reads only the pictures: about 1 minute.",
            ContentPresentation.ModeCardDescription(documents, pictures, 29_000_000, ContentMode.Documents));
        Assert.Equal("Search files by name: no extra space (deletes the 32 MB text index there is now). "
                     + "Inside documents: about 29 MB for 4,047 documents; switching reads nothing more. "
                     + "With pictures' details: about 32 MB in all, with 13,073 pictures, in use.",
            ContentPresentation.ModeCardDescription(documents, pictures, 32_000_000, ContentMode.DocumentsAndPictures));
        // Still reading (or changed in this visit): each choice's first pass, as before.
        Assert.Contains("about 2 minutes", ContentPresentation.ModeCardDescription(documents, pictures, 0));
    }

    [Fact]
    public void Adding_a_folder_already_searched_inside_costs_nothing()
    {
        Assert.Equal(@"Adding C:\Users\Me\Documents\Clients: already searched inside, as part of My folders; nothing more to read.",
            ContentPresentation.AddedFolderCovered(@"C:\Users\Me\Documents\Clients", null, false));
        Assert.Equal(@"Adding E:\Jobs\2024: already searched inside, inside E:\; nothing more to read. Also added to the folders searched by name.",
            ContentPresentation.AddedFolderCovered(@"E:\Jobs\2024", @"E:\", true));
        var s = new ContentSettings { UseMyFolders = false, Folders = [@"E:\", @"C:\Users\Me\Work"] };
        Assert.Equal(@"E:\", s.CoveredBy(@"E:\Jobs\2024"));
        Assert.Equal(@"C:\Users\Me\Work", s.CoveredBy(@"C:\Users\Me\Work"));
        Assert.Null(s.CoveredBy(@"C:\Users\Me"));
        // A parent of folders already searched: only the rest of it is new.
        Assert.Equal([@"C:\Users\Me\Work"], s.FoldersInside(@"C:\Users\Me"));
        Assert.Empty(s.FoldersInside(@"D:\Other"));
    }

    [Fact]
    public void Match_path_and_regex_keep_plain_words_to_names()
    {
        Assert.True(ContentPresentation.PlainWordsLookInside(matchContent: true, matchPath: false, regex: false));
        Assert.False(ContentPresentation.PlainWordsLookInside(matchContent: true, matchPath: true, regex: false));
        Assert.False(ContentPresentation.PlainWordsLookInside(matchContent: true, matchPath: false, regex: true));
        Assert.False(ContentPresentation.PlainWordsLookInside(matchContent: false, matchPath: false, regex: false));
        // NAMES ONLY then, and no empty Matching text column.
        Assert.Contains(ContentPresentation.NamesOnlyIndicator, ContentPresentation.StatusParts(ContentMode.Documents,
            ContentPresentation.PlainWordsLookInside(true, true, false), -1, 3, null, false));
        Assert.False(ContentPresentation.ShowsMatchingText(true, ContentMode.Documents, ContentPresentation.PlainWordsLookInside(true, false, true)));
    }

    // ---- progress and the status bar ---------------------------------------------------------------

    static ContentStatus Status(ContentPhase phase, int done = 12_400, int total = 340_000, int problems = 0, long bytes = 410_000_000,
        TimeSpan? left = null, string? reason = null) => new(phase, done, total, problems, 0, bytes, left, reason, true);

    [Fact]
    public void Progress_lines()
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal("Read 12,400 of 340,000 documents · 410 MB so far · about 2 hours left · 3 couldn't be read",
            ContentPresentation.Progress(Status(ContentPhase.Reading, problems: 3, left: TimeSpan.FromHours(2))));
        Assert.Equal("Read 12,400 of 340,000 documents · 410 MB so far", ContentPresentation.Progress(Status(ContentPhase.Reading)));
        Assert.Equal("Up to date · 5,230 documents · 28 MB", ContentPresentation.Progress(Status(ContentPhase.UpToDate, 5230, 5230, bytes: 28_000_000)));
        Assert.Equal("Up to date · 1 document · under 10 MB", ContentPresentation.Progress(Status(ContentPhase.UpToDate, 1, 1, bytes: 9_000)));
        Assert.Equal("Paused: on battery · read 12,400 of 340,000 documents", ContentPresentation.Progress(Status(ContentPhase.Paused, reason: "on battery")));
        Assert.Equal("Paused while on battery: reading resumes when you plug in · read 12,400 of 340,000 documents",
            ContentPresentation.Progress(Status(ContentPhase.Paused, reason: "Paused while on battery: reading resumes when you plug in.")));
        Assert.Equal("Paused · read 12,400 of 340,000 documents", ContentPresentation.Progress(Status(ContentPhase.Paused)));
        Assert.Equal("Preparing text search…", ContentPresentation.Progress(Status(ContentPhase.Preparing)));
        Assert.Equal("Up to date · 5 documents · under 10 MB · finishing deleting…",
            ContentPresentation.Progress(Status(ContentPhase.UpToDate, 5, 5, bytes: 1, reason: "finishing deleting…")));
        Assert.Equal("Off: only file names are searched.", ContentPresentation.Progress(ContentStatus.Off));
        Assert.Equal("Stopped reading after a file hung: a.pdf", ContentPresentation.Progress(Status(ContentPhase.Stopped, reason: "Stopped reading after a file hung: a.pdf")));
    }

    [Fact]
    public void Progress_counts_documents_and_pictures_apart()
    {
        // The reported line said "Up to date · 35,928 documents · 13 MB" for 26,183 documents and 9,754 pictures.
        using var _ = new CultureScope("en-US");
        var upToDate = Status(ContentPhase.UpToDate, 35_937, 35_937, bytes: 13_000_000) with { WithPictures = true, Pictures = 9_754 };
        Assert.Equal("Up to date · 26,183 documents and 9,754 pictures · 13 MB", ContentPresentation.Progress(upToDate));
        Assert.Equal("Up to date · 9,754 pictures · 13 MB", ContentPresentation.Progress(upToDate with { Done = 9_754, Total = 9_754 }));
        Assert.Equal("Up to date · 1 document and 1 picture · under 10 MB",
            ContentPresentation.Progress(Status(ContentPhase.UpToDate, 2, 2, bytes: 9_000) with { WithPictures = true, Pictures = 1 }));
        Assert.Equal("Up to date · 26,183 documents · 13 MB",
            ContentPresentation.Progress(Status(ContentPhase.UpToDate, 26_183, 26_183, bytes: 13_000_000) with { WithPictures = true }));
        // Without pictures' details on, every row counts as a document.
        Assert.Equal("Up to date · 35,937 documents · 13 MB", ContentPresentation.Progress(upToDate with { WithPictures = false }));
        // While reading, the count is of files (pictures can't be told apart before they are read).
        var reading = Status(ContentPhase.Reading) with { WithPictures = true, Pictures = 2_000 };
        Assert.Equal("Read 12,400 of 340,000 files · 410 MB so far", ContentPresentation.Progress(reading));
        Assert.Equal("Paused · read 12,400 of 340,000 files", ContentPresentation.Progress(reading with { Phase = ContentPhase.Paused }));
    }

    [Fact]
    public void Status_bar_parts()
    {
        using var _ = new CultureScope("en-US");
        Assert.Null(ContentPresentation.StatusBar(ContentStatus.Off, true));
        Assert.Null(ContentPresentation.StatusBar(Status(ContentPhase.UpToDate), true));
        Assert.Equal("Preparing text search…", ContentPresentation.StatusBar(Status(ContentPhase.Preparing), false));
        Assert.Equal("Reading inside files 12,400 / 340,000", ContentPresentation.StatusBar(Status(ContentPhase.Reading), false));
        Assert.Equal("Inside files: 12,400 of 340,000 read so far", ContentPresentation.StatusBar(Status(ContentPhase.Reading), true));
        Assert.Equal("Inside files paused (on battery)", ContentPresentation.StatusBar(Status(ContentPhase.Paused, reason: "on battery"), false));
        Assert.Equal("Inside files paused (on battery)", ContentPresentation.StatusBar(Status(ContentPhase.Paused, reason: "Paused while on battery: reading resumes later."), false));
        Assert.Equal("Inside files paused (on battery): 12,400 of 340,000 read so far",
            ContentPresentation.StatusBar(Status(ContentPhase.Paused, reason: "on battery"), true));
        Assert.Equal("Inside files paused",
            ContentPresentation.StatusBar(Status(ContentPhase.Paused, reason: "because the computer is being used for a presentation right now"), false));
        Assert.Equal("Inside files stopped", ContentPresentation.StatusBar(Status(ContentPhase.Stopped), false));
    }

    [Fact]
    public void Status_parts_follow_the_mode_the_toggle_and_the_split()
    {
        using var _ = new CultureScope("en-US");
        var reading = Status(ContentPhase.Reading);
        Assert.Empty(ContentPresentation.StatusParts(ContentMode.NamesOnly, true, 12, 1284, reading, true));
        Assert.Equal(new[] { "NAMES ONLY", "Reading inside files 12,400 / 340,000" },
            ContentPresentation.StatusParts(ContentMode.Documents, false, -1, 10, reading, false));
        Assert.Equal(new[] { "12 by name · 1,272 inside files", "Inside files: 12,400 of 340,000 read so far" },
            ContentPresentation.StatusParts(ContentMode.Documents, true, 12, 1284, reading, true));
        Assert.Empty(ContentPresentation.StatusParts(ContentMode.Documents, true, 50, 50, Status(ContentPhase.UpToDate), true)); // all by name: no split
        Assert.Equal(new[] { "0 by name · 7 inside files" }, ContentPresentation.StatusParts(ContentMode.DocumentsAndPictures, true, 0, 7, null, true));
    }

    [Fact]
    public void The_matching_text_column_follows_the_mode_and_the_toggle_only()
    {
        Assert.True(ContentPresentation.ShowsMatchingText(true, ContentMode.Documents, true));
        Assert.False(ContentPresentation.ShowsMatchingText(false, ContentMode.Documents, true));
        Assert.False(ContentPresentation.ShowsMatchingText(true, ContentMode.NamesOnly, true));
        Assert.False(ContentPresentation.ShowsMatchingText(true, ContentMode.DocumentsAndPictures, false));
    }

    // ---- reasons ---------------------------------------------------------------------------------

    [Fact]
    public void Every_status_has_a_reason_and_crashed_is_neutral()
    {
        foreach (var status in Enum.GetValues<DocStatus>()) Assert.False(string.IsNullOrWhiteSpace(ContentPresentation.Reason(status)));
        Assert.Equal(Enum.GetValues<DocStatus>().Length, Enum.GetValues<DocStatus>().Select(ContentPresentation.Reason).Distinct().Count());
        string crashed = ContentPresentation.Reason(DocStatus.Crashed);
        Assert.Contains("interrupted", crashed);
        Assert.DoesNotContain("Stopped the app", crashed);
        Assert.Equal("Password-protected", ContentPresentation.Reason(DocStatus.PasswordProtected));
        Assert.Equal("Only the first 1 MB of text is searchable", ContentPresentation.Reason(DocStatus.Truncated));
    }

    [Fact]
    public void Problems_are_listed_by_reason_with_no_text_apart()
    {
        using var _ = new CultureScope("en-US");
        var counts = new Dictionary<DocStatus, int>
        {
            [DocStatus.Ok] = 5000, [DocStatus.PasswordProtected] = 3, [DocStatus.TooBig] = 1, [DocStatus.NoText] = 1200, [DocStatus.Damaged] = 0,
        };
        var lines = ContentPresentation.Problems(counts).Split('\n');
        Assert.Equal(new[] { "Password-protected: 3", "Too big to read: 1", "No text to read (scans and pictures, kept by their details): 1,200" }, lines);
        Assert.Equal("", ContentPresentation.Problems(new Dictionary<DocStatus, int>()));
    }

    // ---- folders, offers, confirmations -------------------------------------------------------------

    [Fact]
    public void Adding_a_network_folder_says_what_it_costs()
    {
        using var _ = new CultureScope("en-US");
        var nas = Est(ContentMode.Documents, 850_000, 0, 750_000_000, TimeSpan.FromHours(33), 204_000_000_000);
        Assert.Equal(@"Adding \\nas\jobs: about 0.8 GB more for 850,000 documents; the first pass is about 33 hours of reading while this PC is on, "
                     + "slower while you use it; reads about 200 GB over the network (at most 8 MB/s). Also added to the folders searched by name.",
            ContentPresentation.AddedFolder(@"\\nas\jobs", nas, alsoAddedToNames: true));
        Assert.Equal(@"Adding D:\Work: about 27 MB more for 5,230 documents; the first pass takes about 3 minutes in the background.",
            ContentPresentation.AddedFolder(@"D:\Work", Est(ContentMode.Documents, 5230, 0, 27_455_953, TimeSpan.FromMinutes(3)), false));
        Assert.Equal(@"Adding D:\Empty: nothing to read there yet.", ContentPresentation.AddedFolder(@"D:\Empty", Est(ContentMode.Documents, 0, 0, 0, TimeSpan.Zero), false));
        Assert.Equal(@"Adding D:\Big: counting its files…", ContentPresentation.AddedFolder(@"D:\Big", Est(ContentMode.Documents, 0, 0, 0, TimeSpan.Zero, atLeast: true), false));
        Assert.Contains("(at most 1.5 MB/s)", ContentPresentation.AddedFolder(@"\\nas\x", nas, false, networkKilobytesPerSecond: 1536));
    }

    [Fact]
    public void A_count_cut_short_says_how_much_the_share_holds()
    {
        using var _ = new CultureScope("en-US");
        var partial = Est(ContentMode.Documents, 250_000, 0, 220_000_000, TimeSpan.FromHours(10), 60_000_000_000, atLeast: true);
        string text = ContentPresentation.AddedFolder(@"\\nas\jobs", partial, true, usedBytes: 10_000_000_000_000);
        Assert.StartsWith(@"Adding \\nas\jobs: at least 220 MB more for 250,000 documents so far;", text);
        Assert.Contains("Counting stopped early; the drive or share it is on holds 10.0 TB in all.", text);
        Assert.EndsWith("Also added to the folders searched by name.", text);
        // A complete count, or no size known, says nothing about it.
        Assert.DoesNotContain("stopped early", ContentPresentation.AddedFolder(@"D:\Work", partial with { AtLeast = false }, false, usedBytes: 10_000_000_000_000));
        Assert.DoesNotContain("stopped early", ContentPresentation.AddedFolder(@"D:\Work", partial, false));
    }

    [Fact]
    public void A_share_counted_in_part_and_worked_out_from_what_it_holds_says_so()
    {
        using var _ = new CultureScope("en-US");
        // ContentEstimator.Extrapolate scaled the part counted up to the share's used bytes: an estimate, not "at least".
        var scaled = Est(ContentMode.Documents, 850_000, 0, 800_000_000, TimeSpan.FromHours(33), 200_000_000_000);
        string text = ContentPresentation.AddedFolder(@"\\nas\jobs", scaled, true, usedBytes: 10_000_000_000_000, extrapolated: true);
        Assert.Contains("0.8 GB more for 850,000 documents", text);
        Assert.DoesNotContain("so far", text);
        Assert.Contains("Counting stopped early, so this is worked out from the 10.0 TB it holds in all", text);
        Assert.EndsWith("Also added to the folders searched by name.", text);
    }

    [Fact]
    public void The_offer_bar_text_and_when_it_shows()
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal("Search inside your documents too? About 28 MB; the first pass takes about 3 minutes in the background.",
            ContentPresentation.Offer(Est(ContentMode.Documents, 5230, 0, 28_000_000, TimeSpan.FromMinutes(2.3))));
        Assert.StartsWith("Search inside your documents too? The words", ContentPresentation.Offer(null));
        Assert.Equal("Your text index from before is still here (0.8 GB). Turn search inside files back on?",
            ContentPresentation.OfferKept(new KeptIndex(ContentMode.Documents, 750_000_000)));

        Assert.True(ContentPresentation.ShowsOffer(false, ContentMode.NamesOnly, false, false, IndexState.Ready));
        Assert.True(ContentPresentation.ShowsOffer(false, ContentMode.NamesOnly, true, false, IndexState.Ready));
        Assert.False(ContentPresentation.ShowsOffer(true, ContentMode.NamesOnly, false, false, IndexState.Ready));     // answered
        Assert.False(ContentPresentation.ShowsOffer(false, ContentMode.Documents, true, false, IndexState.Ready));     // chosen
        Assert.False(ContentPresentation.ShowsOffer(false, ContentMode.NamesOnly, false, true, IndexState.Ready));     // demo
        Assert.False(ContentPresentation.ShowsOffer(false, ContentMode.NamesOnly, false, false, IndexState.Scanning)); // names not ready
    }

    [Fact]
    public void Confirmations()
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal("Delete the 28 MB text index? Searching inside files stops.", ContentPresentation.ConfirmNamesOnly(28_000_000));
        Assert.Equal("Delete the text index (under 10 MB)? Searching inside files stops.", ContentPresentation.ConfirmNamesOnly(0));
        Assert.Equal(@"Stop searching inside \\nas\jobs? 850,000 documents (0.8 GB) leave the text index now; reading them again later takes about 33 hours and 200 GB over the network.",
            ContentPresentation.ConfirmRemoval(@"\\nas\jobs", 850_000, Est(ContentMode.Documents, 850_000, 0, 750_000_000, TimeSpan.FromHours(33), 204_000_000_000)));
        Assert.Equal("Stop searching inside My folders? 1,200 documents leave the text index now.", ContentPresentation.ConfirmRemoval("My folders", 1200, null));
    }

    [Fact]
    public void A_kind_unticked_with_pictures_on_counts_only_that_kind()
    {
        // Pictures don't follow the kinds: they are in both scopes, so they cancel out.
        var before = Est(ContentMode.DocumentsAndPictures, 5_050, 14_000, 40_000_000, TimeSpan.FromMinutes(6));
        var after = Est(ContentMode.DocumentsAndPictures, 5_000, 14_000, 39_000_000, TimeSpan.FromMinutes(5));
        var removed = ContentPresentation.Removed(before, after);
        Assert.Equal(50, removed.Documents);
        Assert.Equal(ContentMode.Documents, removed.Mode);
        Assert.Equal(50, ContentPresentation.RemovedCount(removed));
        Assert.Equal(1_000_000, removed.IndexBytes);
        Assert.Equal(TimeSpan.FromMinutes(1), removed.FirstPass);
    }

    [Fact]
    public void A_folder_still_covered_another_way_loses_nothing()
    {
        // Removing a folder My folders still covers: the scope holds the same documents before and after.
        var same = Est(ContentMode.Documents, 5_230, 0, 27_455_953, TimeSpan.FromMinutes(3));
        var removed = ContentPresentation.Removed(same, same);
        Assert.Equal(0, ContentPresentation.RemovedCount(removed));
        Assert.Equal(0, removed.IndexBytes);
        Assert.Equal(TimeSpan.Zero, removed.FirstPass);
        // Never negative, even when the scope after is counted larger.
        var odd = ContentPresentation.Removed(same, Est(ContentMode.Documents, 6_000, 0, 30_000_000, TimeSpan.FromMinutes(4), 5));
        Assert.Equal(0, odd.Documents);
        Assert.Equal(0, odd.IndexBytes);
        Assert.Equal(0, odd.NetworkBytesToRead);
        Assert.Equal(TimeSpan.Zero, odd.FirstPass);
    }

    [Fact]
    public void Back_to_documents_removes_the_pictures()
    {
        using var _ = new CultureScope("en-US");
        var before = Est(ContentMode.DocumentsAndPictures, 23_467, 48_617, 120_000_000, TimeSpan.FromMinutes(30));
        var after = Est(ContentMode.Documents, 23_467, 48_617, 110_000_000, TimeSpan.FromMinutes(25)); // pictures counted, not kept
        var removed = ContentPresentation.Removed(before, after);
        Assert.Equal(0, removed.Documents);
        Assert.Equal(48_617, removed.Pictures);
        Assert.Equal(ContentMode.DocumentsAndPictures, removed.Mode);
        Assert.Equal(48_617, ContentPresentation.RemovedCount(removed));
        Assert.Equal("Stop searching inside pictures' details? 48,617 pictures (10 MB) leave the text index now; reading them again later takes about 5 minutes.",
            ContentPresentation.ConfirmRemoval("pictures' details", removed));
    }

    [Fact]
    public void Removal_questions_name_documents_and_pictures()
    {
        using var _ = new CultureScope("en-US");
        var both = Est(ContentMode.DocumentsAndPictures, 1_200, 14_000, 31_000_000, TimeSpan.FromMinutes(6), 0);
        Assert.Equal("Stop searching inside My folders? 1,200 documents and 14,000 pictures (31 MB) leave the text index now; reading them again later takes about 6 minutes.",
            ContentPresentation.ConfirmRemoval("My folders", both));
        var docs = Est(ContentMode.Documents, 850_000, 0, 750_000_000, TimeSpan.FromHours(33), 204_000_000_000);
        Assert.Equal(@"Stop searching inside \\nas\jobs? 850,000 documents (0.8 GB) leave the text index now; reading them again later takes about 33 hours and 200 GB over the network.",
            ContentPresentation.ConfirmRemoval(@"\\nas\jobs", docs));
        // Pictures in a Documents-mode estimate were never kept: not named, not counted.
        Assert.StartsWith("Stop searching inside .log files? 3,000 documents (20 MB)",
            ContentPresentation.ConfirmRemoval(".log files", Est(ContentMode.Documents, 3_000, 9_999, 20_000_000, TimeSpan.FromMinutes(1))));
        Assert.Equal(3_000, ContentPresentation.RemovedCount(Est(ContentMode.Documents, 3_000, 9_999, 0, TimeSpan.Zero)));
        Assert.Equal(int.MaxValue, ContentPresentation.RemovedCount(Est(ContentMode.DocumentsAndPictures, int.MaxValue, int.MaxValue, 0, TimeSpan.Zero)));
    }

    // ---- snippets ------------------------------------------------------------------------------

    static ContentSnippet Snip(string text, (int, int)[] bold, ContentKind kind, int page = 0, int line = 0, string? sheet = null, DocField? field = null, int pages = 0)
        => new(text, bold, kind, page, line, sheet, field) { Pages = pages };

    [Fact]
    public void Snippet_prefixes()
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal("p. 4 · ", ContentPresentation.SnippetPrefix(Snip("x", [], ContentKind.Pdf, page: 4)));
        Assert.Equal("Slide 3 · ", ContentPresentation.SnippetPrefix(Snip("x", [], ContentKind.Office, page: 3)));
        Assert.Equal("Sheet \"2024\" · ", ContentPresentation.SnippetPrefix(Snip("x", [], ContentKind.Office, page: 2, sheet: "2024")));
        Assert.Equal("Line 1,200 · ", ContentPresentation.SnippetPrefix(Snip("x", [], ContentKind.Text, line: 1200)));
        Assert.Equal("Keywords · ", ContentPresentation.SnippetPrefix(Snip("x", [], ContentKind.Picture, field: DocField.Keywords)));
        Assert.Equal("Camera make · ", ContentPresentation.SnippetPrefix(Snip("x", [], ContentKind.Picture, field: DocField.CameraMake)));
        Assert.Equal("", ContentPresentation.SnippetPrefix(Snip("x", [], ContentKind.Office)));
    }

    [Fact]
    public void A_snippet_body_is_the_documents_own_text_even_when_it_starts_like_a_prefix()
    {
        using var _ = new CultureScope("en-US");
        // SnippetFormatter never puts the prefix in Text: words that look like one are the document's own.
        var lookalike = Snip("Line 120 · foo bar", [(11, 3)], ContentKind.Text, line: 120);
        var (text, bold) = ContentPresentation.SnippetBody(lookalike);
        Assert.Equal("Line 120 · foo bar", text);
        Assert.Equal(new[] { (11, 3) }, bold);
        var plain = Snip("foo bar", [(4, 3)], ContentKind.Text, line: 120);
        Assert.Equal("foo bar", ContentPresentation.SnippetBody(plain).Text);
        Assert.Equal(new[] { (4, 3) }, ContentPresentation.SnippetBody(plain).Bold);
    }

    [Fact]
    public void A_line_far_from_its_match_starts_near_it()
    {
        // SnippetFormatter centres about 300 characters on the first match: a 360 px cell shows only its start.
        string lead = string.Concat(Enumerable.Repeat("lorem ipsum ", 12)); // 144 characters
        string text = lead + "the invoice total is due";
        int at = text.IndexOf("invoice", StringComparison.Ordinal);
        var (cut, bold) = ContentPresentation.StartNearMatch(text, [(at, 7), (at + 8, 5)]);
        Assert.StartsWith("…", cut);
        Assert.Equal("invoice", cut.Substring(bold[0].Start, bold[0].Length));
        Assert.Equal("total", cut.Substring(bold[1].Start, bold[1].Length));
        Assert.InRange(bold[0].Start, 2, 22);         // about 20 characters of context
        Assert.Equal(' ', text[at - bold[0].Start]);  // it starts at a word: a space was cut just before it
    }

    [Fact]
    public void The_cell_keeps_at_most_three_words_before_the_match()
    {
        // The reported line: SnippetFormatter keeps about 40 characters before the hit, so a narrow cell showed
        // "…contract plan and bu". At most three words / 20 characters are left, from a word start.
        var s = SnippetFormatter.Format(new StoredDoc(
                "Minutes of the March board meeting, where we reviewed the new contract plan and budget for the second half of the year", [], null),
            ContentKind.Office, "docx", 0, null, ContentHighlighter.Create("content:budget", new SearchOptions())!)!;
        Assert.True(s.Text.IndexOf("budget", StringComparison.Ordinal) > 25, s.Text); // the snippet itself keeps more (the tooltip shows it)
        var (cut, bold) = ContentPresentation.StartNearMatch(s.Text, s.Highlights);
        Assert.StartsWith("…contract plan and budget for the second", cut);
        Assert.Equal("budget", cut.Substring(bold[0].Start, bold[0].Length));
        Assert.Equal(s.Text.Trim(), ContentPresentation.SnippetToolTip(s)); // the tooltip keeps the whole line

        // Short words: three of them, though more would fit in 20 characters.
        Assert.Equal("…c d e match", ContentPresentation.StartNearMatch("a b c d e match", [(10, 5)]).Text);
        // Long words: as many whole words as fit in 20 characters.
        string longer = "documentation alpha considerations match";
        Assert.Equal("…considerations match", ContentPresentation.StartNearMatch(longer, [(longer.IndexOf("match"), 5)]).Text);
        // One word longer than 20 characters: the line starts at the match.
        string word = "the supercalifragilisticexpialidocious invoice";
        var (w, wb) = ContentPresentation.StartNearMatch(word, [(word.IndexOf("invoice"), 7)]);
        Assert.Equal("…invoice", w);
        Assert.Equal((1, 7), wb[0]);
        // Fewer words for a narrower cell, down to none.
        Assert.Equal("…e match", ContentPresentation.StartNearMatch("a b c d e match", [(10, 5)], keepWords: 1).Text);
        Assert.Equal("…match", ContentPresentation.StartNearMatch("a b c d e match", [(10, 5)], keepWords: 0).Text);
        Assert.Equal("…match", ContentPresentation.StartNearMatch("a b c d e(match", [(10, 5)], keepWords: 0).Text); // punctuation glued on
    }

    [Fact]
    public void Text_without_spaces_keeps_20_characters_before_the_match()
    {
        string japanese = string.Concat(Enumerable.Repeat("今日は良い天気ですね。", 5)) + "東京都に行きました。";
        int at = japanese.IndexOf("東京都", StringComparison.Ordinal);
        var (cut, bold) = ContentPresentation.StartNearMatch(japanese, [(at, 3)]);
        Assert.Equal("…" + japanese[(at - 20)..], cut);
        Assert.Equal((21, 3), bold[0]);
    }

    [Fact]
    public void A_line_whose_match_is_near_its_start_is_left_alone()
    {
        var bold = new List<(int, int)> { (10, 4) };
        var (text, moved) = ContentPresentation.StartNearMatch("0123456789abcd and more", bold);
        Assert.Equal("0123456789abcd and more", text);
        Assert.Same(bold, moved);
        Assert.Equal("no bold", ContentPresentation.StartNearMatch("no bold", []).Text);
        // Ranges past the end (an index that changed underneath) don't count as the first match.
        Assert.Equal("short", ContentPresentation.StartNearMatch("short", [(400, 3)]).Text);
    }

    [Fact]
    public void Trimming_never_splits_a_character_and_drops_odd_ranges()
    {
        // No space to cut at, and the cut lands on the second half of an emoji.
        string text = new string('x', 61) + "\uD83D\uDE00" + new string('y', 30) + "match";
        int at = text.IndexOf("match", StringComparison.Ordinal);
        var (cut, bold) = ContentPresentation.StartNearMatch(text, [(-3, 2), (at, 5)], keepChars: 31); // (-3, 2): an odd range
        Assert.Equal('y', cut[1]);
        Assert.Single(bold);
        Assert.Equal("match", cut.Substring(bold[0].Start, bold[0].Length));
    }

    [Fact]
    public void Tooltips_say_where_the_line_is()
    {
        using var _ = new CultureScope("en-US");
        Assert.Equal("…the invoice total…\nPage 4 of 12", ContentPresentation.SnippetToolTip(Snip("…the invoice total…", [(5, 7)], ContentKind.Pdf, page: 4, pages: 12)));
        Assert.Equal("…the invoice total…\nPage 4", ContentPresentation.SnippetToolTip(Snip("…the invoice total…", [], ContentKind.Pdf, page: 4)));
        Assert.Equal("Agenda\nSlide 3 of 9", ContentPresentation.SnippetToolTip(Snip("Agenda", [], ContentKind.Office, page: 3, pages: 9)));
        Assert.Equal("sunset; beach\nKeywords", ContentPresentation.SnippetToolTip(Snip("sunset; beach", [], ContentKind.Picture, field: DocField.Keywords)));
        Assert.Equal("plain words", ContentPresentation.SnippetToolTip(Snip("plain words", [], ContentKind.Office)));
    }

    [Fact]
    public void Banners_for_pages_fields_and_pictures_only()
    {
        using var _ = new CultureScope("en-US");
        var page = ContentPresentation.Banner(Snip("…the invoice total…", [(5, 7)], ContentKind.Pdf, page: 4));
        Assert.Equal("Page 4: …the invoice total…", page.Text);
        Assert.Equal(new[] { (13, 7) }, page.Bold);
        Assert.Equal("invoice", page.Text![13..20]);
        var field = ContentPresentation.Banner(Snip("sunset, beach", [(8, 5)], ContentKind.Picture, field: DocField.Keywords));
        Assert.Equal("Keywords: sunset, beach", field.Text);
        Assert.Equal("beach", field.Text![field.Bold[0].Start..(field.Bold[0].Start + field.Bold[0].Length)]);
        var numbers = ContentPresentation.Banner(Snip("2480 × 3508 · 300 dpi · CMYK", [], ContentKind.Picture));
        Assert.Equal("2480 × 3508 · 300 dpi · CMYK", numbers.Text);
        Assert.Null(ContentPresentation.Banner(Snip("a line", [(0, 1)], ContentKind.Text, line: 4)).Text);
        Assert.Null(ContentPresentation.Banner(Snip("a paragraph", [(0, 1)], ContentKind.Office)).Text);
    }

    [Fact]
    public void Extreme_values_never_throw()
    {
        using var _ = new CultureScope("en-US");
        Assert.EndsWith(" TB", ContentPresentation.Size(long.MaxValue));
        Assert.EndsWith(" days", ContentPresentation.Duration(TimeSpan.MaxValue));
        Assert.Equal("under a minute", ContentPresentation.Duration(TimeSpan.MinValue));
        var huge = Est(ContentMode.DocumentsAndPictures, int.MaxValue, int.MaxValue, long.MaxValue, TimeSpan.MaxValue, long.MaxValue, atLeast: true);
        Assert.NotEmpty(ContentPresentation.OptionDetail(ContentMode.DocumentsAndPictures, huge, Defaults));
        Assert.NotEmpty(ContentPresentation.AddedFolder("X", huge, true, networkKilobytesPerSecond: 0));
        Assert.NotEmpty(ContentPresentation.ConfirmRemoval("X", int.MaxValue, huge));
        Assert.NotEmpty(ContentPresentation.Progress(new ContentStatus(ContentPhase.Reading, -1, -1, -1, -1, -1, TimeSpan.MinValue, "", false)));
        Assert.Empty(ContentPresentation.StatusParts(ContentMode.Documents, true, 0, 0, null, false));
        // Highlights past the end of the text (an index that changed underneath) are kept for the drawing code to clamp.
        var odd = new ContentSnippet("p. 2 · ab", [(50, 9), (-3, 2)], ContentKind.Pdf, 2, 0, null, null);
        Assert.Equal("p. 2 · ab", ContentPresentation.SnippetBody(odd).Text);
        Assert.NotNull(ContentPresentation.Banner(odd).Text);
    }

    [Fact]
    public void Field_names_are_words()
    {
        foreach (var f in Enum.GetValues<DocField>())
        {
            string name = ContentPresentation.FieldName(f);
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.All(name.Skip(1), c => Assert.False(char.IsUpper(c))); // "CameraMake" reads "Camera make"
        }
    }
}

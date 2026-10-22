using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Search;
using Anything.Core.Search;

namespace Anything.Tests.Content.Search;

/// <summary>
/// Plain words' cache keys (keystrokes that look up the same words share one set), the email field functions, the
/// description: alias, the column and status-bar rules for a query's own content functions, and the preview banners.
/// </summary>
public class ContentReviewSearchTests
{
    static readonly DateTime Now = new(2024, 5, 15, 12, 0, 0, DateTimeKind.Local);

    static ContentTerm Only(string query, SearchOptions? options = null)
    {
        var o = options ?? new SearchOptions { MatchContent = true };
        return Assert.Single(ContentPlan.Build(QueryParser.Parse(query, o), o, ContentMode.Documents, Now).Terms).Value;
    }

    [Theory]
    [InlineData("inv", "inv-", "inv-2", "INV", "Inv-2")]
    [InlineData("inv-2024", "inv-2024-", "inv-2024-0", "INV-2024")]
    [InlineData("report", "report.", "report.p", "Report.P")]
    public void Keystrokes_that_look_up_the_same_words_share_one_key_and_keep_their_own_highlighting(params string[] keystrokes)
    {
        var terms = keystrokes.Select(k => Only(k)).ToList();
        Assert.All(terms, t => Assert.True(t.Plain));
        Assert.Single(terms.Select(t => t.Key).Distinct());
        // Highlighting depends on the word typed: those keys stay apart.
        Assert.Equal(terms.Count, terms.Select(t => t.StableKey).Distinct().Count());
        // An explicit content: term keeps its own key (its word is confirmed in the stored text).
        Assert.NotEqual(terms[0].Key, Only("content:" + keystrokes[0]).Key);
    }

    [Fact]
    public void A_second_keystroke_of_the_same_lookup_resolves_nothing_again()
    {
        using var f = new ContentFixture();
        f.Doc(@"C:\Data\a.txt", "invoice INV-2024-0012 and report.pdf");
        f.Publish();
        Assert.Single(f.Find("inv", ContentFixture.Plain));
        int terms = f.Source.Cache.TermCount;
        foreach (var keystroke in new[] { "inv-", "inv-2", "INV" })
            Assert.Single(f.Find(keystroke, ContentFixture.Plain));
        Assert.Equal(terms, f.Source.Cache.TermCount);
    }

    [Fact]
    public void A_search_engine_for_the_command_line_waits_for_its_plain_word_lookups()
    {
        using var f = new ContentFixture();
        for (int s = 0; s < 4; s++)
        {
            if (s > 0) f.NextSegment();
            f.Doc($@"C:\Slow\doc{s}.txt", "the budget for next year");
        }
        var store = f.Publish();
        foreach (var seg in store.FakeSegments) seg.OnTerms = () => Thread.Sleep(150);
        // A window's short budget: the lookups run on in the background and the results say so (it searches again).
        var hurried = Services.CreateSearchEngine(TimeSpan.FromMilliseconds(20)).Search(f.Index, "budget", ContentFixture.Plain, SortKey.Name, false, CancellationToken.None);
        Assert.True(hurried.ContentPending);
        // A one-shot command line can't search again: it waits for every lookup, and nothing is missing.
        var patient = Services.CreateSearchEngine(TimeSpan.MaxValue).Search(f.Index, "year", ContentFixture.Plain, SortKey.Name, false, CancellationToken.None);
        Assert.False(patient.ContentPending);
        Assert.Equal(4, patient.Ids.Length);
    }

    [Fact]
    public void An_emails_subject_sender_and_recipients_are_found_by_their_functions()
    {
        using var f = new ContentFixture();
        f.Doc(@"C:\Mail\quote.eml", "Please find the quote attached.",
            [(DocField.Subject, "Quote for ACME"), (DocField.Sender, "Alice Example <alice@example.com>"), (DocField.Recipients, "Bob Buyer; Carol Copy")],
            kind: ContentKind.Mail);
        f.Doc(@"C:\Docs\report.docx", "Annual figures", [(DocField.Author, "Alice Writer"), (DocField.Title, "Quote book")]);
        f.Publish();
        Assert.Equal([@"C:\Mail\quote.eml"], f.Find("subject:quote"));
        Assert.Equal([@"C:\Mail\quote.eml"], f.Find("from:alice"));
        Assert.Equal([@"C:\Mail\quote.eml"], f.Find("sender:example"));
        Assert.Equal([@"C:\Mail\quote.eml"], f.Find("to:carol"));
        // author: finds a document's author and an email's sender.
        Assert.Equal([@"C:\Docs\report.docx", @"C:\Mail\quote.eml"], f.Set("author:alice"));
        // title: is a document's title, not an email's subject.
        Assert.Equal([@"C:\Docs\report.docx"], f.Find("title:quote"));
    }

    [Fact]
    public void Description_is_an_alias_of_comment()
    {
        var term = Only("description:sunset");
        Assert.Equal(ContentTermKind.Field, term.Kind);
        Assert.Equal(DocField.Comment, term.Field);
        using var f = new ContentFixture();
        f.Doc(@"C:\Pictures\bay.jpg", "", [(DocField.Comment, "Sunset over the bay")], status: DocStatus.NoText);
        f.Publish();
        Assert.Equal([@"C:\Pictures\bay.jpg"], f.Find("description:sunset"));
    }

    [Fact]
    public void A_querys_own_content_functions_show_the_matching_text_whatever_the_toggle_says()
    {
        // The toggle is for plain words: off, they stay names only; content:, title:, dpi: still look inside files.
        Assert.False(ContentPresentation.ShowsMatchingText(true, ContentMode.Documents, matchContent: false));
        Assert.True(ContentPresentation.ShowsMatchingText(true, ContentMode.Documents, matchContent: false, explicitContent: true));
        Assert.False(ContentPresentation.ShowsMatchingText(false, ContentMode.Documents, matchContent: false, explicitContent: true));
        Assert.False(ContentPresentation.ShowsMatchingText(true, ContentMode.NamesOnly, matchContent: true, explicitContent: true));
        Assert.Contains(ContentPresentation.NamesOnlyIndicator, ContentPresentation.StatusParts(ContentMode.Documents, false, -1, 10, null, true));
        Assert.DoesNotContain(ContentPresentation.NamesOnlyIndicator, ContentPresentation.StatusParts(ContentMode.Documents, false, -1, 10, null, true, explicitContent: true));
        // What tells them apart: only content functions raise the highlighter's mode above names only.
        Assert.Equal(ContentMode.NamesOnly, ContentHighlighter.Create("budget", new SearchOptions { MatchContent = true })!.NeedsMode);
        Assert.True(ContentHighlighter.Create("content:budget", new SearchOptions())!.NeedsMode > ContentMode.NamesOnly);
    }

    [Fact]
    public void Matches_the_preview_cant_highlight_in_place_get_a_banner()
    {
        static ContentSnippet Snippet(ContentKind kind, int page = 0) => new("ACME Corp spring sale", [(0, 4)], kind, page, 0, null, null);
        var (mail, mailBold) = ContentPresentation.Banner(Snippet(ContentKind.Mail));
        Assert.Equal("Message: ACME Corp spring sale", mail);
        Assert.Equal(("Message: ".Length, 4), Assert.Single(mailBold));
        var (layer, _) = ContentPresentation.Banner(Snippet(ContentKind.Design));
        Assert.Equal("Text: ACME Corp spring sale", layer);
        Assert.Equal("Page 2: ACME Corp spring sale", ContentPresentation.Banner(Snippet(ContentKind.Design, page: 2)).Text);
        // Text and Office are highlighted in place.
        Assert.Null(ContentPresentation.Banner(Snippet(ContentKind.Text)).Text);
        Assert.Null(ContentPresentation.Banner(Snippet(ContentKind.Office)).Text);
    }
}

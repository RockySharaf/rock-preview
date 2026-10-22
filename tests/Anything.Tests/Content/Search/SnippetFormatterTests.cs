using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Search;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Search;

public class SnippetFormatterTests
{
    static ContentHighlighter H(string query, bool plain = false)
        => ContentHighlighter.Create(query, new SearchOptions { MatchContent = plain })!;

    static ContentSnippet? F(string body, string ext, string query, ContentKind? kind = null, (DocField, string)[]? fields = null,
        PictureInfo? picture = null, int pages = 0, int maxChars = 300)
        => SnippetFormatter.Format(new StoredDoc(body, fields ?? [], picture), kind ?? ContentTypes.KindOf("x." + ext), ext, pages, picture,
            H(query, plain: !query.Contains(':')), maxChars);

    static string Bold(ContentSnippet s) => string.Join("|", s.Highlights.Select(h => s.Text.Substring(h.Start, h.Length)));

    [Fact]
    public void Pdf_pages_are_counted_from_page_breaks()
    {
        var s = F("first page text\fsecond page has the invoice here\fthird", "pdf", "content:invoice", pages: 3)!;
        Assert.Equal(2, s.Page);
        Assert.Equal(0, s.Line);
        Assert.Equal(3, s.Pages);
        Assert.Equal("second page has the invoice here", s.Text); // only its page, no ellipsis: nothing was cut
        Assert.Equal("invoice", Bold(s));
        Assert.Equal("p. 2 · ", ContentPresentation.SnippetPrefix(s));
        Assert.Equal(2, F("\finvoice on page two", "pdf", "content:invoice")!.Page); // a first page without text: the body starts with \f
    }

    [Fact]
    public void Slides_sheets_and_other_office_documents()
    {
        var slide = F("Title slide\fAgenda\fThe invoice slide", "pptx", "content:invoice")!;
        Assert.Equal(3, slide.Page);
        Assert.Equal("Slide 3 · ", ContentPresentation.SnippetPrefix(slide));

        var sheet = F("Summary\nTotal 5\fDetails\nRent invoice 800", "xlsx", "content:invoice")!;
        Assert.Equal((2, "Details"), (sheet.Page, sheet.Sheet));
        Assert.Equal("Rent invoice 800", sheet.Text); // the sheet's name is in the prefix, not the text
        Assert.Equal("Sheet \"Details\" · ", ContentPresentation.SnippetPrefix(sheet));
        var named = F("Invoices\nnothing", "ods", "content:invoice")!;
        Assert.Equal((1, "Invoices"), (named.Page, named.Sheet)); // a match in the name itself

        var doc = F("Dear client\nThe invoice is attached", "docx", "content:invoice")!;
        Assert.Equal((0, 0, (string?)null), (doc.Page, doc.Line, doc.Sheet));
        Assert.Equal("", ContentPresentation.SnippetPrefix(doc));
    }

    [Theory]
    [InlineData("txt", 3)]
    [InlineData("md", 3)]
    [InlineData("markdown", 3)]
    [InlineData("text", 3)]
    [InlineData("log", 3)]  // a user's extra text extension
    [InlineData("csv", 0)]  // shown as a table
    [InlineData("tsv", 0)]
    [InlineData("rtf", 0)]  // decoded: lines differ from the file's
    [InlineData("htm", 0)]  // tags stripped
    [InlineData("html", 0)]
    public void Lines_only_where_the_preview_shows_the_stored_lines(string ext, int line)
    {
        var s = F("one\ntwo\nthe invoice\nfour", ext, "content:invoice", kind: ContentKind.Text)!;
        Assert.Equal(line, s.Line);
        Assert.Equal(0, s.Page);
    }

    [Fact]
    public void Long_lines_are_cut_near_the_hit_at_word_boundaries()
    {
        string before = string.Join(' ', Enumerable.Range(0, 120).Select(i => "word" + i));
        string after = string.Join(' ', Enumerable.Range(0, 120).Select(i => "tail" + i));
        var s = F(before + " the invoice number " + after, "txt", "content:invoice")!;
        Assert.StartsWith("…", s.Text);
        Assert.EndsWith("…", s.Text);
        Assert.True(s.Text.Length <= 302, $"{s.Text.Length} characters");
        int at = s.Text.IndexOf("invoice", StringComparison.Ordinal);
        Assert.InRange(at, 1, SnippetFormatter.LeadChars + 2); // near the start, so the cell's end ellipsis can't hide it
        Assert.Equal("invoice", Bold(s));
        // Never a piece of a word at either cut.
        string inner = s.Text.Trim('…');
        Assert.Matches(@"^ ?(word\d+|the) ", inner);
        Assert.Matches(@" tail\d+$", inner);
    }

    [Fact]
    public void Text_without_spaces_keeps_its_context_on_both_sides()
    {
        // Japanese: any character boundary is a cut, so the snippet keeps about 300 characters around the hit.
        string japanese = string.Concat(Enumerable.Repeat("今日は良い天気ですね。", 80)) + "東京都に行きました。"
            + string.Concat(Enumerable.Repeat("明日も晴れるでしょう。", 80));
        var jp = F(japanese, "txt", "content:東京都")!;
        Assert.True(jp.Text.Length >= 200, $"{jp.Text.Length} characters: {jp.Text}");
        int at = jp.Text.IndexOf("東京都", StringComparison.Ordinal);
        Assert.InRange(at, 10, SnippetFormatter.LeadChars + 2);
        Assert.True(jp.Text.Length - at - 3 > 150, "no context after the hit");
        Assert.Equal("東京都", Bold(jp));

        // A CSV row: the cuts move to the nearest cell edge, never to the hit itself.
        string row = string.Join(";", Enumerable.Range(0, 60).Select(i => "cell" + i)) + ";invoice;"
            + string.Join(";", Enumerable.Range(60, 60).Select(i => "cell" + i));
        var csv = F(row, "csv", "content:invoice")!;
        Assert.True(csv.Text.Length >= 200, $"{csv.Text.Length} characters: {csv.Text}");
        Assert.Contains("cell59;invoice;cell60", csv.Text);
        Assert.Matches(@"^…;?(cell\d+;)+invoice(;cell\d+)+;?…$", csv.Text); // whole cells only
        Assert.Equal("invoice", Bold(csv));
    }

    [Fact]
    public void Cuts_never_split_a_character()
    {
        // A letter and its combining mark, and a surrogate pair, stay whole at both cuts.
        string marked = string.Concat(Enumerable.Repeat("é", 200)) + " invoice " + string.Concat(Enumerable.Repeat("\U00020BB7", 200));
        var s = F(marked, "txt", "content:invoice", maxChars: 60)!;
        string inner = s.Text.Trim('…');
        Assert.False(inner.Length > 0 && char.IsLowSurrogate(inner[0]), "starts with half a character");
        Assert.False(inner.Length > 0 && char.IsHighSurrogate(inner[^1]), "ends with half a character");
        Assert.False(inner.Length > 0 && inner[0] == '́', "starts with a mark");
        Assert.Equal("invoice", Bold(s));
    }

    [Fact]
    public void Whitespace_collapses_and_short_texts_are_whole()
    {
        var s = F("a\n\n   b invoice\t\tc", "txt", "content:invoice")!;
        Assert.Equal("a b invoice c", s.Text);
        Assert.Equal([(4, 7)], s.Highlights);
        Assert.Equal(3, s.Line);
    }

    [Fact]
    public void A_hit_near_the_end_shows_more_before_it()
    {
        string body = string.Join(' ', Enumerable.Range(0, 100).Select(i => "word" + i)) + " invoice";
        var s = F(body, "txt", "content:invoice", maxChars: 100)!;
        Assert.StartsWith("…", s.Text);
        Assert.EndsWith("invoice", s.Text);
        Assert.InRange(s.Text.Length, 80, 102);
    }

    [Fact]
    public void Field_matches_give_field_snippets()
    {
        var s = F("", "jpg", "sunset", fields: [(DocField.Keywords, "beach; sunset")])!;
        Assert.Equal(DocField.Keywords, s.Field);
        Assert.Equal("beach; sunset", s.Text);
        Assert.Equal("sunset", Bold(s));
        Assert.Equal("Keywords · ", ContentPresentation.SnippetPrefix(s));

        // A field function looks only in its field, even when the body has the word.
        var title = F("an annual report inside", "docx", "title:annual", fields: [(DocField.Title, "Annual figures")])!;
        Assert.Equal((DocField.Title, "Annual figures"), (title.Field!.Value, title.Text));
        Assert.Equal("Annual", Bold(title));

        // The body comes first for words of the body.
        var body = F("an annual report inside", "docx", "content:annual", fields: [(DocField.Title, "Annual figures")])!;
        Assert.Null(body.Field);
        Assert.Equal("an annual report inside", body.Text);
        var camera = F("", "jpg", "camera:eos", fields: [(DocField.CameraMake, "Canon"), (DocField.CameraModel, "EOS R5")])!;
        Assert.Equal(DocField.CameraModel, camera.Field);
    }

    [Fact]
    public void Pictures_matched_by_numbers_show_their_details()
    {
        var info = new PictureInfo(2480, 3508, 300, 300, 8, ColorMode.Cmyk, 1, 0, 0, 0, false, 0, 0, 0, 0, 0, 0);
        var s = F("", "psd", "dpi:>=300 cmyk:", kind: ContentKind.Design, picture: info)!;
        Assert.Equal("2480 × 3508 · 300 dpi · CMYK", s.Text);
        Assert.Empty(s.Highlights);
        Assert.Equal(ContentKind.Design, s.Kind);
        var rotated = info with { Orientation = 6, BitDepth = 16 };
        Assert.Equal("3508 × 2480 · 300 dpi · CMYK · 16-bit", F("", "tif", "bitdepth:16", picture: rotated)!.Text);
    }

    [Fact]
    public void Nothing_when_nothing_matches_inside()
    {
        Assert.Null(F("nothing here", "txt", "content:invoice"));
        Assert.Null(F("nothing here", "txt", "content:")); // files with words: no word to show
        Assert.Null(F("", "txt", "contentstatus:ok"));
        Assert.Null(F("an invoice", "txt", "!content:invoice"));
    }

    [Fact]
    public void Codes_phrases_and_bigrams_are_found_like_the_search_finds_them()
    {
        Assert.Equal("INV 2024", Bold(F("paid INV 2024 today", "txt", "content:INV-2024")!));
        Assert.Equal("annual\nreport".Replace('\n', ' '), Bold(F("the annual\nreport", "txt", "content:\"annual report\"")!));
        Assert.Equal("東京都", Bold(F("東京都に行きました", "txt", "content:東京都")!));
    }
}

using Anything.Core.Content;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content;

public class ExtractSinkTests
{
    static readonly string Ls = ((char)0x2028).ToString(), Ps = ((char)0x2029).ToString(), Nel = ((char)0x85).ToString();

    static ExtractSink Sink(int maxText = 1 << 20, int maxPages = 2000) => new(new ExtractLimits(MaxTextChars: maxText, MaxPages: maxPages));

    static string Body(ExtractSink sink) => sink.ToDoc(ContentKind.Text, DocStatus.Ok, 1, 0, 0).Body;

    [Fact]
    public void Text_is_cut_at_the_limit()
    {
        var sink = Sink(maxText: 10);
        sink.Text("hello world!!");
        Assert.True(sink.Full);
        Assert.Equal(10, sink.TextLength);
        sink.Text("more");
        sink.LineBreak();
        Assert.Equal("hello worl", Body(sink));
    }

    [Fact]
    public void Exactly_at_the_limit_is_not_full()
    {
        var sink = Sink(maxText: 5);
        sink.Text("hello");
        Assert.False(sink.Full);
        Assert.Equal("hello", Body(sink));
    }

    [Fact]
    public void The_page_limit_fills_the_sink()
    {
        var sink = Sink(maxPages: 2);
        sink.Text("a");
        sink.PageBreak();
        sink.Text("b");
        Assert.False(sink.Full);
        sink.PageBreak();
        Assert.True(sink.Full);
        sink.Text("c");
        Assert.Equal("a\fb", Body(sink));
    }

    [Fact]
    public void The_cut_never_splits_a_surrogate_pair()
    {
        var sink = Sink(maxText: 5);
        sink.Text("abcd😀");
        Assert.True(sink.Full);
        Assert.Equal("abcd", Body(sink));
    }

    [Fact]
    public void The_cut_never_separates_a_letter_from_its_marks()
    {
        var sink = Sink(maxText: 5);
        sink.Text("abcdé̂x");
        Assert.True(sink.Full);
        Assert.Equal("abcd", Body(sink));

        var thai = Sink(maxText: 3);
        thai.Text("กม้");
        Assert.Equal("กม้", Body(thai));
        var thaiCut = Sink(maxText: 2);
        thaiCut.Text("กม้");
        Assert.Equal("ก", Body(thaiCut));

        // A mark with no letter before it (after a line end) never takes the line end with it.
        var orphan = Sink(maxText: 3);
        orphan.Text("ab\n" + (char)0x0301);
        Assert.Equal("ab\n", Body(orphan));
        Assert.Equal("ab ", ExtractSink.SafeCut("ab " + (char)0x0301, 3));
        Assert.Equal("ab", ExtractSink.SafeCut("abc" + (char)0x0301, 3));
    }

    [Fact]
    public void Lone_surrogates_become_replacement_characters()
    {
        var sink = Sink();
        sink.Text("a\uD800b\uDC00c");
        sink.Text("\uD83D");
        sink.Text("\uDE00!");
        sink.Text("\uD83D");
        sink.LineBreak();
        Assert.Equal("a�b�c😀!�\n", Body(sink));

        var end = Sink();
        end.Text("x\uD83D");
        Assert.Equal("x�", Body(end));
    }

    [Fact]
    public void Line_ends_of_every_kind_become_newlines()
    {
        var sink = Sink();
        sink.Text("a\r\nb\rc\vd" + Nel + "e" + Ls + "f" + Ps + "g\n\nh");
        Assert.Equal("a\nb\nc\nd\ne\nf\ng\n\nh", Body(sink));

        var split = Sink();
        split.Text("a\r");
        split.Text("\nb");
        Assert.Equal("a\nb", Body(split));
    }

    [Fact]
    public void Only_page_breaks_write_form_feeds()
    {
        var sink = Sink();
        sink.Text("page\fone\u0001two\u001Bthree\tfour\u007Ffive\u0090six");
        Assert.Equal("page\none two three\tfour five six", Body(sink));
        Assert.DoesNotContain('\f', Body(sink));
        sink.PageBreak();
        Assert.Equal(1, Body(sink).Count(c => c == '\f'));
    }

    [Fact]
    public void Every_sheet_starts_with_its_name_and_only_later_sheets_with_a_page_break()
    {
        var sink = Sink();
        sink.StartSheet("Sheet1");
        sink.Text("x");
        sink.StartSheet("Q1 2024");
        sink.Text("y");
        sink.StartSheet("multi\nline\tname");
        sink.StartSheet("");
        sink.Text("z");
        Assert.Equal("Sheet1\nx\fQ1 2024\ny\fmulti line name\n\f\nz", Body(sink));
    }

    [Fact]
    public void Sheets_count_against_the_page_limit()
    {
        var sink = Sink(maxPages: 2);
        sink.StartSheet("a");
        sink.StartSheet("b");
        Assert.False(sink.Full);
        sink.StartSheet("c");
        Assert.True(sink.Full);
        Assert.Equal("a\n\fb\n", Body(sink));
    }

    [Fact]
    public void A_first_page_without_text_still_counts_as_a_page()
    {
        var sink = Sink();
        sink.PageBreak();
        sink.Text("page two");
        Assert.Equal("\fpage two", Body(sink));
    }

    [Fact]
    public void Line_and_page_ends_alone_are_no_words()
    {
        var sink = Sink();
        sink.PageBreak();
        sink.Text(" \t\n");
        sink.PageBreak();
        Assert.False(sink.HasWords);
        Assert.False(sink.HasContent);
        sink.Text("—");
        Assert.False(sink.HasWords);
        sink.Text("x");
        Assert.True(sink.HasWords);
        Assert.True(sink.HasContent);

        var fieldOnly = Sink();
        fieldOnly.PageBreak();
        fieldOnly.Field(DocField.Title, "Title");
        Assert.False(fieldOnly.HasWords);
        Assert.True(fieldOnly.HasContent);

        // A supplementary letter whose two halves land in different chunks of the body (the first chunk holds 16 chars).
        var split = Sink();
        split.Text(new string('\n', 15) + "𠀀");
        Assert.True(split.HasWords);
        var lone = Sink();
        lone.Text(new string('\n', 15) + "\uD840");
        Assert.False(lone.HasWords);
    }

    [Fact]
    public void ToDoc_keeps_what_each_status_keeps()
    {
        var sink = Sink();
        sink.Text("words");
        sink.Field(DocField.Title, "Title");
        sink.Picture.Width = 10;
        sink.Pages = 2;
        var noText = sink.ToDoc(ContentKind.Pdf, DocStatus.NoText, 1, 5, 6);
        Assert.Equal(("", 1, true, 2), (noText.Body, noText.Fields.Count, noText.Picture.HasValue, noText.Pages));
        var problem = sink.ToDoc(ContentKind.Pdf, DocStatus.Damaged, 1, 5, 6);
        Assert.Equal(("", 0, false, 0, 5L, 6), (problem.Body, problem.Fields.Count, problem.Picture.HasValue, problem.Pages, problem.BytesRead, problem.ElapsedMs));
        var truncated = sink.ToDoc(ContentKind.Pdf, DocStatus.Truncated, 1, 5, 6);
        Assert.Equal(("words", 1, true, 2), (truncated.Body, truncated.Fields.Count, truncated.Picture.HasValue, truncated.Pages));
    }

    [Fact]
    public void The_body_is_nfc()
    {
        var sink = Sink();
        sink.Text("café Å");
        Assert.Equal("café Å", Body(sink));
        Assert.Equal(5, Body(sink).IndexOf('Å'));
        Assert.True(Body(sink).IsNormalized());
    }

    [Fact]
    public void Fields_keep_the_first_value_and_join_keywords()
    {
        var sink = Sink();
        sink.Field(DocField.Title, "  First  ");
        sink.Field(DocField.Title, "Second");
        sink.Field(DocField.Author, "");
        sink.Field(DocField.Author, "Ann\r\nSmith");
        sink.Field(DocField.Keywords, "sunset");
        sink.Field(DocField.Keywords, "beach");
        sink.Field(DocField.Keywords, "SUNSET");
        sink.Field(DocField.Keywords, " ");
        var doc = sink.ToDoc(ContentKind.Picture, DocStatus.Ok, 1, 0, 0);
        Assert.Equal([(DocField.Title, "First"), (DocField.Author, "Ann  Smith"), (DocField.Keywords, "sunset; beach")], doc.Fields);
        Assert.True(sink.HasContent);
        Assert.Equal(3, sink.FieldCount);
    }

    [Fact]
    public void Fields_are_capped()
    {
        var sink = Sink();
        sink.Field(DocField.Comment, new string('c', 10_000));
        for (int i = 0; i < 10_000; i++) sink.Field(DocField.Keywords, "keyword" + i);
        var doc = sink.ToDoc(ContentKind.Picture, DocStatus.Ok, 1, 0, 0);
        Assert.Equal(ExtractSink.MaxFieldChars, doc.Fields.Single(f => f.Field == DocField.Comment).Value.Length);
        Assert.True(doc.Fields.Single(f => f.Field == DocField.Keywords).Value.Length <= ExtractSink.MaxKeywordChars);
    }

    [Fact]
    public void Many_keywords_cost_memory_in_proportion_to_what_is_kept()
    {
        // An IPTC block or an XMP bag with 50,000 keywords: they are joined once, and those past the cap are dropped unseen.
        var sink = Sink();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50_000; i++) sink.Field(DocField.Keywords, "keyword" + i);
        var doc = sink.ToDoc(ContentKind.Picture, DocStatus.Ok, 1, 0, 0);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        string keywords = doc.Fields.Single(f => f.Field == DocField.Keywords).Value;
        Assert.StartsWith("keyword0; keyword1; keyword2", keywords);
        Assert.InRange(keywords.Length, ExtractSink.MaxKeywordChars - 64, ExtractSink.MaxKeywordChars);
        Assert.True(allocated < 16L << 20, $"allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Picture_details_keep_the_first_value()
    {
        var sink = Sink();
        Assert.False(sink.HasPicture);
        var p = sink.Picture;
        Assert.False(sink.HasPicture);
        p.Width = 0;
        p.Width = 3000;
        p.Width = 10;
        p.Height = 2000;
        p.Orientation = 9;
        p.Orientation = 6;
        p.ColorMode = ColorMode.Cmyk;
        p.ColorMode = ColorMode.Rgb;
        p.FNumber = float.NaN;
        p.FNumber = 2.8f;
        p.SetGps(0, 0);
        p.SetGps(double.NaN, 3);
        p.SetGps(95, 3);
        Assert.False(p.HasGps);
        p.SetGps(45.5, -73.25);
        p.SetGps(1, 1);
        Assert.True(sink.HasPicture);
        var info = sink.ToDoc(ContentKind.Picture, DocStatus.Ok, 1, 0, 0).Picture!.Value;
        Assert.Equal((3000, 2000), (info.Width, info.Height));
        Assert.Equal((2000, 3000), info.Shown);
        Assert.Equal(ColorMode.Cmyk, info.ColorMode);
        Assert.Equal(2.8f, info.FNumber);
        Assert.Equal((45_500_000, -73_250_000), (info.LatitudeMicro, info.LongitudeMicro));
    }

    [Fact]
    public void ToDoc_carries_the_reader_facts()
    {
        var sink = Sink();
        sink.Text("x");
        sink.Pages = 3;
        var doc = sink.ToDoc(ContentKind.Pdf, DocStatus.Truncated, 7, 1234, 56);
        Assert.Equal((ContentKind.Pdf, DocStatus.Truncated, (byte)7, 1234L, 56, 3), (doc.Kind, doc.Status, doc.ExtractorVersion, doc.BytesRead, doc.ElapsedMs, doc.Pages));
        Assert.Null(doc.Picture);
    }
}

using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content.Readers;

public class TextReadersTests
{
    static TextReadersTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    public void Plain_text_keeps_line_numbers_for_every_kind_of_line_end()
    {
        var doc = ReaderRun.Bytes(Encoding.UTF8.GetBytes("one\r\ntwo\rthree\nfour\ffive"), "txt");
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal("one\ntwo\nthree\nfour\nfive", doc.Body); // a form feed in a text file is a line end, never a page
        Assert.Equal(1, doc.ExtractorVersion);
    }

    [Fact]
    public void Encodings_are_detected_utf16_bom_and_bomless_and_ansi_late_in_the_file()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Grüße aus Köln")).ToArray();
        Assert.Equal("Grüße aus Köln", ReaderRun.Bytes(utf16, "txt").Body);
        var bomless = Encoding.BigEndianUnicode.GetBytes("plain big endian words here");
        Assert.Equal("plain big endian words here", ReaderRun.Bytes(bomless, "txt").Body);
        // An Excel "CSV (Comma delimited)" in the ANSI code page: ASCII for 70 KB, then Müller.
        var ansi = TextDecoding.Ansi();
        var csv = new StringBuilder();
        while (csv.Length < 70_000) csv.Append("id,name,city\r\n");
        csv.Append("42,Müller,Zürich\r\n");
        var doc = ReaderRun.Bytes(ansi.GetBytes(csv.ToString()), "csv");
        if (ansi.CodePage == 1252) Assert.EndsWith("42,Müller,Zürich\n", doc.Body);
        Assert.DoesNotContain("�", doc.Body);
    }

    [Fact]
    public void Binary_is_no_text_and_files_past_4_MB_are_truncated()
    {
        Assert.Equal(DocStatus.NoText, ReaderRun.Bytes([0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0, 4, 0, 0, 0], "txt").Status);
        var big = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("word ", (5 << 20) / 5)));
        var doc = ReaderRun.Bytes(big, "txt", new ExtractLimits(MaxTextChars: 8 << 20));
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.True(doc.Body.Length <= PlainTextReader.MaxBytes);
    }

    [Fact]
    public void Html_strips_tags_skips_scripts_decodes_entities_and_keeps_fields()
    {
        var html = "<!DOCTYPE html><html><head><title>Page &amp; Title</title><meta name=\"keywords\" content=\"alpha, beta\">" +
            "<meta name=\"description\" content=\"A summary\"><style>.x{color:red}</style><script>var hidden = 'script words';</script></head>" +
            "<body><h1>Heading</h1><p>caf&eacute; &#233;t&#xE9; &#xD800; &#150; &unknown; x&lt;y</p><!-- comment words -->" +
            "<table><tr><td>cell one</td><td>cell two</td></tr></table><pre>keep   spaces</pre></body></html>";
        var doc = ReaderRun.Bytes(Encoding.UTF8.GetBytes(html), "html");
        var lines = doc.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Heading", lines[0]);
        Assert.Equal("café été � – &unknown; x<y", lines[1]);
        Assert.Equal("cell one\tcell two", lines[2]);
        Assert.Equal("keep   spaces", lines[3]);
        Assert.DoesNotContain("script words", doc.Body);
        Assert.DoesNotContain("comment", doc.Body);
        Assert.DoesNotContain("color", doc.Body);
        Assert.Equal("Page & Title", doc.Field(DocField.Title));
        Assert.Equal("alpha; beta", doc.Field(DocField.Keywords));
        Assert.Equal("A summary", doc.Field(DocField.Comment));
    }

    [Theory]
    [InlineData("description")]
    [InlineData("keywords")]
    [InlineData("generator")]
    public void Html_meta_content_of_millions_of_ampersands_decodes_in_linear_time(string name)
    {
        // Each '&' looked for its ';' through the rest of the value: 2 MB of them took over a minute, past the watchdog.
        var html = $"<html><head><meta name=\"{name}\" content=\"{new string('&', 3_000_000)}\"></head><body>hello world</body></html>";
        var started = Environment.TickCount64;
        var doc = ReaderRun.Bytes(Encoding.ASCII.GetBytes(html), "html");
        long ms = Environment.TickCount64 - started;
        Assert.Equal("hello world", doc.Body.Trim());
        Assert.True(ms < 3_000, $"{ms} ms");
        Assert.Equal(DecodedEntitiesStillWork, HtmlText.Decode("a &amp; b &#233; &unknown; c&lt;"));
    }

    const string DecodedEntitiesStillWork = "a & b é &unknown; c<";

    [Theory]
    [InlineData("html")]
    [InlineData("rtf")]
    public void A_keyword_list_of_megabytes_keeps_the_first_keywords_in_bounded_memory(string extension)
    {
        string list = "first, second; " + string.Concat(Enumerable.Repeat(",a", 2_000_000));
        var bytes = extension == "html"
            ? Encoding.ASCII.GetBytes($"<html><head><meta name=\"keywords\" content=\"{list}\"></head><body>body words</body></html>")
            : Encoding.ASCII.GetBytes("{\\rtf1{\\info{\\keywords " + list + "}}body words}");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = ReaderRun.Bytes(bytes, extension);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.StartsWith("first; second; a", doc.Field(DocField.Keywords));
        Assert.True(allocated < 64 << 20, $"allocated {allocated >> 20} MB");
    }

    [Fact]
    public void The_sink_walks_a_keyword_list_without_splitting_it_whole()
    {
        var sink = new ExtractSink(ExtractLimits.Local);
        sink.Keywords(" alpha ,beta;; gamma ,, ");
        Assert.Equal("alpha; beta; gamma", sink.Value(DocField.Keywords));
        var full = new ExtractSink(ExtractLimits.Local);
        full.Keywords(string.Join(',', Enumerable.Range(0, 100_000).Select(i => "k" + i)));
        Assert.True(full.KeywordsFull);
        Assert.True(full.Value(DocField.Keywords)!.Length <= ExtractSink.MaxKeywordChars);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void A_large_text_file_reads_only_what_fills_the_text_limit(string lineEnd)
    {
        // A 6 MB CSV: 1 M characters are kept, so about 1 MB is read, not the 4 MB window (a share's throttle counts every byte).
        var csv = new StringBuilder();
        for (int i = 0; csv.Length < 6 << 20; i++) csv.Append("2024-01-01,Invoice ").Append(i).Append(",Müller GmbH,42.00").Append(lineEnd);
        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        var (doc, source) = ReaderRun.Counted(bytes, "csv");
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.Equal(ExtractLimits.Local.MaxTextChars, doc.Body.Length);
        Assert.StartsWith("2024-01-01,Invoice 0,Müller GmbH,42.00\n", doc.Body);
        Assert.DoesNotContain("\r", doc.Body);
        long read = source.Reads.Sum(r => (long)r.Length);
        Assert.True(read < 3 * ExtractLimits.Local.MaxTextChars / 2, $"read {read >> 10} KB");
        // The same text whole (no limit hit) is read whole.
        string part = csv.ToString(0, 200_000);
        Assert.Equal(part.Replace("\r\n", "\n").Replace('\r', '\n'), ReaderRun.Bytes(Encoding.UTF8.GetBytes(part), "csv").Body);
    }

    [Fact]
    public void Html_meta_charset_decides_without_a_bom()
    {
        var body = "<html><head><meta charset=\"windows-1251\"></head><body>Привет мир</body></html>";
        var bytes = Encoding.GetEncoding(1251).GetBytes(body);
        Assert.Equal("Привет мир", ReaderRun.Bytes(bytes, "htm").Body.Trim());
    }

    [Fact]
    public void Rtf_decodes_font_code_pages_unicode_and_info()
    {
        var rtf = "{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fnil\\fcharset0 Calibri;}{\\f1\\fnil\\fcharset204 Calibri Cyr;}{\\f2\\fnil\\fcharset128 MS Gothic;}}" +
            "{\\colortbl;\\red255\\green0\\blue0;}{\\stylesheet{\\s0 Normal;}}" +
            "{\\info{\\title Report title}{\\author Ann}{\\upr{\\keywords ansi kw}{\\*\\ud{\\keywords unicode kw}}}{\\doccomm A comment}{\\nofpages4}}" +
            "\\pard\\f1\\'cf\\'f0\\'e8\\'e2\\'e5\\'f2\\f0  hello\\par " +
            "\\f2\\'93\\'fa\\'96\\'7b\\'8c\\'ea\\'83J\\f0\\par " +
            "\\uc1\\u20320?\\u22909?\\u-10179?\\u-8704?\\par " +
            "{\\field{\\*\\fldinst HYPERLINK \"http://hidden.example\"}{\\fldrslt link text}}{\\field{\\fldinst PAGE}{\\fldrslt 3}}\\par " +
            "{\\pict\\wmetafile8\\bin6 {}}{}x}\\par " +
            "{\\*\\generator Riched20;}tab\\tab end\\page second page}";
        var doc = ReaderRun.Bytes(Encoding.ASCII.GetBytes(rtf), "rtf");
        Assert.Equal(DocStatus.Ok, doc.Status);
        var pages = doc.Body.Split('\f');
        Assert.Equal(2, pages.Length);
        var lines = pages[0].Split('\n');
        Assert.Equal("Привет hello", lines[0]);
        Assert.Equal("日本語カ", lines[1]);
        Assert.Equal("你好😀", lines[2]);
        Assert.Equal("link text3", lines[3]);
        Assert.Equal("", lines[4]);
        Assert.Equal("tab\tend", lines[5]);
        Assert.Equal("second page", pages[1]);
        Assert.DoesNotContain("hidden", doc.Body);
        Assert.DoesNotContain("Riched", doc.Body);
        Assert.Equal("Report title", doc.Field(DocField.Title));
        Assert.Equal("Ann", doc.Field(DocField.Author));
        Assert.Equal("unicode kw", doc.Field(DocField.Keywords));
        Assert.Equal("A comment", doc.Field(DocField.Comment));
        Assert.Equal(4, doc.Pages);
    }

    [Fact]
    public void Rtf_hundred_thousand_nested_groups_neither_overflow_nor_hang()
    {
        var rtf = "{\\rtf1 " + new string('{', 100_000) + "deep words" + new string('}', 100_000) + " after}";
        var doc = ReaderRun.Bytes(Encoding.ASCII.GetBytes(rtf), "rtf");
        Assert.Contains("deep words", doc.Body);
        Assert.Contains("after", doc.Body);
        Assert.Equal(DocStatus.Damaged, ReaderRun.Bytes(Encoding.ASCII.GetBytes("plain text, not rtf"), "rtf").Status);
    }

    [Fact]
    public void Rtf_bin_lengths_past_the_end_are_safe()
    {
        var doc = ReaderRun.Bytes(Encoding.ASCII.GetBytes("{\\rtf1 before {\\pict\\bin999999999 xx}}"), "rtf");
        Assert.Equal("before", doc.Body.Trim());
    }
}

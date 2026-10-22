using System.Text;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content;

public class TextDecodingTests
{
    static byte[] Bytes(Encoding e, string s, bool bom) => bom ? [.. e.GetPreamble(), .. e.GetBytes(s)] : e.GetBytes(s);

    [Fact]
    public void Byte_order_marks_decide_and_are_dropped()
    {
        foreach (var (enc, name) in new (Encoding, string)[]
                 { (new UTF8Encoding(true), "utf-8"), (new UnicodeEncoding(false, true), "utf-16"), (new UnicodeEncoding(true, true), "utf-16BE"),
                   (new UTF32Encoding(false, true), "utf-32"), (new UTF32Encoding(true, true), "utf-32BE") })
        {
            var bytes = Bytes(enc, "Grüße 東京", bom: true);
            var detected = TextDecoding.Detect(bytes);
            Assert.NotNull(detected);
            Assert.Equal(name, detected!.WebName);
            Assert.Equal("Grüße 東京", TextDecoding.Decode(bytes, detected));
        }
    }

    [Fact]
    public void Bomless_utf16_is_recognised()
    {
        string text = "Hello world, this is plain text written by an old tool.";
        Assert.Equal("utf-16", TextDecoding.Detect(Encoding.Unicode.GetBytes(text))!.WebName);
        Assert.Equal("utf-16BE", TextDecoding.Detect(Encoding.BigEndianUnicode.GetBytes(text))!.WebName);
    }

    [Fact]
    public void Empty_is_utf8_and_nuls_mean_binary_anywhere_in_the_window()
    {
        Assert.Equal("utf-8", TextDecoding.Detect([])!.WebName);
        Assert.Null(TextDecoding.Detect([(byte)'a', 0, (byte)'b', (byte)'c', (byte)'d', (byte)'e', (byte)'f', (byte)'g', (byte)'h', (byte)'i', (byte)'j', (byte)'k', (byte)'l', (byte)'m', (byte)'n', (byte)'o', (byte)'p', (byte)'q']));
        var late = Encoding.ASCII.GetBytes(new string('x', 100_000));
        late[90_000] = 0;
        Assert.Null(TextDecoding.Detect(late));
    }

    [Fact]
    public void Many_control_bytes_mean_binary()
    {
        var bytes = Enumerable.Range(0, 1000).Select(i => i % 10 == 0 ? (byte)1 : (byte)'a').ToArray();
        Assert.Null(TextDecoding.Detect(bytes));
        var ok = Encoding.ASCII.GetBytes("line one\tcol\fpage\u001B[0m\r\n");
        Assert.NotNull(TextDecoding.Detect(ok));
    }

    [Fact]
    public void Utf8_must_be_valid_over_the_whole_window()
    {
        // Excel's "CSV (Comma delimited)": an all-ASCII head, then one ANSI byte far beyond 64 KB.
        var head = Encoding.ASCII.GetBytes(new string('a', 70_000) + "\nM");
        byte[] ansi = [.. head, 0xFC, (byte)'l', (byte)'l', (byte)'e', (byte)'r'];
        var detected = TextDecoding.Detect(ansi);
        Assert.NotNull(detected);
        Assert.NotEqual("utf-8", detected!.WebName);
        Assert.Equal(TextDecoding.Ansi().WebName, detected.WebName);
        // The whole buffer through this PC's code page (on a double-byte page 0xFC is a lead byte and takes the next byte).
        Assert.Equal(TextDecoding.Ansi().GetString(ansi), TextDecoding.Decode(ansi, detected));

        byte[] utf8 = [.. head, .. Encoding.UTF8.GetBytes("üller")];
        Assert.Equal("utf-8", TextDecoding.Detect(utf8)!.WebName);
    }

    [Fact]
    public void A_utf8_character_cut_at_the_end_of_the_window_is_still_utf8()
    {
        var bytes = Encoding.UTF8.GetBytes("Grüße aus 東京");
        var cut = bytes[..^1]; // half of 京
        var detected = TextDecoding.Detect(cut);
        Assert.Equal("utf-8", detected!.WebName);
        Assert.Equal("Grüße aus 東", TextDecoding.Decode(cut, detected));
    }

    [Fact]
    public void The_ansi_fallback_is_this_pcs_code_page()
    {
        var ansi = TextDecoding.Ansi();
        Assert.NotEqual(65001, ansi.CodePage);
        Assert.True(ansi.IsSingleByte || ansi.CodePage is 932 or 936 or 949 or 950);
    }

    [Theory]
    [InlineData("utf8", 65001), InlineData("UTF-8", 65001), InlineData("\"iso-8859-1\"", 28591), InlineData("latin1", 28591)]
    [InlineData("windows-1251", 1251), InlineData("cp1251", 1251), InlineData("ks_c_5601-1987", 949), InlineData("Shift_JIS", 932)]
    [InlineData("x-sjis", 932), InlineData("cp850", 850), InlineData("gb2312", 936)]
    public void Charsets_with_aliases(string name, int codePage) => Assert.Equal(codePage, TextDecoding.ForCharset(name)!.CodePage);

    [Theory]
    [InlineData("x-unknown-charset"), InlineData(""), InlineData(null), InlineData("   ")]
    public void Unknown_charsets_are_null(string? name) => Assert.Null(TextDecoding.ForCharset(name));

    [Fact]
    public void Html_meta_charset()
    {
        Assert.Equal(1251, TextDecoding.HtmlCharset("<html><head><meta charset=\"windows-1251\"><title>x</title>"u8)!.CodePage);
        Assert.Equal(932, TextDecoding.HtmlCharset("<META http-equiv='Content-Type' content='text/html; charset=Shift_JIS'>"u8)!.CodePage);
        Assert.Equal(65001, TextDecoding.HtmlCharset("<meta charset=utf-16>"u8)!.CodePage);
        Assert.Null(TextDecoding.HtmlCharset("<html><head><title>no charset</title>"u8));
        Assert.Null(TextDecoding.HtmlCharset(Encoding.ASCII.GetBytes(new string(' ', 2000) + "<meta charset=\"windows-1251\">")));
    }
}

using System.IO.Compression;
using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using static Anything.Tests.Content.Pdf.PdfTestSupport;
using W = Anything.Tests.Content.Pdf.PdfFixtureWriter;

namespace Anything.Tests.Content.Pdf;

public class PdfFilterTests
{
    static readonly ExtractBudget Budget = new(ExtractLimits.Local, CancellationToken.None);

    static PdfDict Parms(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        var lx = new PdfLexer(bytes, bytes.Length);
        lx.Next();
        return (PdfDict)PdfParser.Parse(lx, false, null, out _);
    }

    [Fact]
    public void Lzw_decodes_the_example_of_the_pdf_reference()
    {
        // ISO 32000-1, 7.4.4.2: "-----A---B" encodes as 80 0B 60 50 22 0C 0C 85 01.
        var decoded = PdfFilters.Lzw(Convert.FromHexString("800B6050220C0C8501"), 1 << 20, null);
        Assert.Equal("-----A---B", Encoding.ASCII.GetString(decoded.Span));
    }

    [Fact]
    public void Lzw_round_trips_long_input_through_every_code_width()
    {
        var rng = new Random(4);
        var data = new byte[200_000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)"abcdefgh"[rng.Next(rng.Next(1, 9))];
        var decoded = PdfFilters.Lzw(W.Encode("LZW", data), 1 << 20, null);
        Assert.Equal(data, decoded.Span.ToArray());
    }

    [Fact]
    public void Ascii85_matches_known_text_and_z_and_short_groups()
    {
        Assert.Equal("Man is distinguished", Encoding.ASCII.GetString(PdfFilters.Ascii85(Encoding.ASCII.GetBytes("<~9jqo^BlbD-BleB1DJ+*+F(f,q~>"), 1000).Span));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 1 }, PdfFilters.Ascii85(Encoding.ASCII.GetBytes("z!<~>"), 1000).Span.ToArray());
        Assert.Equal("Hello", Encoding.ASCII.GetString(PdfFilters.Ascii85(Encoding.ASCII.GetBytes("87cURDZ~>"), 1000).Span));
    }

    [Fact]
    public void AsciiHex_and_RunLength()
    {
        Assert.Equal(new byte[] { 0x48, 0x65, 0x70 }, PdfFilters.AsciiHex(Encoding.ASCII.GetBytes("48 65\n7>ignored"), 1000).Span.ToArray());
        // 2 = copy 3 bytes; 254 = repeat the next byte 3 times; 128 = end.
        Assert.Equal("abcxxx", Encoding.ASCII.GetString(PdfFilters.RunLength([2, (byte)'a', (byte)'b', (byte)'c', 254, (byte)'x', 128, 0, 0], 1000).Span));
    }

    [Fact]
    public void Flate_ignores_a_bad_checksum_and_keeps_output_before_an_error()
    {
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps. ", 200)));
        var z = W.Zlib(text);
        z[^1] ^= 0xFF; // Adler-32 wrong
        var whole = PdfFilters.Flate(z, 0, z.Length, 1 << 20);
        Assert.Equal(text, whole.Span.ToArray());
        Assert.False(whole.Truncated);
        var noChecksum = PdfFilters.Flate(z, 0, z.Length - 4, 1 << 20); // no Adler-32 at all
        Assert.Equal(text, noChecksum.Span.ToArray());
        Assert.False(noChecksum.Truncated);

        // Cut halfway: what came out before the cut stays, and the stream says it ended early.
        var cut = W.Zlib(text)[..(z.Length / 2)];
        var partial = PdfFilters.Flate(cut, 0, cut.Length, 1 << 20);
        Assert.True(partial.Length > 0);
        Assert.Equal(text.AsSpan(0, partial.Length).ToArray(), partial.Span.ToArray());
        Assert.True(partial.Truncated);

        // Raw deflate without the zlib header, and with bytes after its end (a /Length that took in the end of line).
        var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) d.Write(text);
        byte[] raw = [.. ms.ToArray(), (byte)'\r', (byte)'\n'];
        var rawWhole = PdfFilters.Flate(raw, 0, raw.Length, 1 << 20);
        Assert.Equal(text, rawWhole.Span.ToArray());
        Assert.False(rawWhole.Truncated);
    }

    [Fact]
    public void Stream_buffers_come_from_the_pool()
    {
        // A page of about 1 MB of content: once the pool holds buffers of that size, reading the stream again
        // allocates almost nothing (no new raw, inflate or predictor buffers), and the bytes are still right.
        var lines = Enumerable.Range(0, 30_000).Select(i => $"Line {i} of a page of words").ToArray();
        string content = W.Lines(lines);
        var w = W.SimpleDocument("placeholder");
        int stream = w.AddStream("<< >>", Ascii(content), "FlatePng");
        var bytes = w.Write(crypt: W.CryptKind.Aes128);
        var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(bytes, budget);
        using var file = PdfFile.Open(source, budget);
        var data = Assert.IsType<PdfStream>(file.Resolve(new PdfRef(stream, 0)));
        Assert.True(content.Length > 900_000);
        // Tests on other threads share the pool and may take a buffer this one gave back, so the least of a few
        // rounds is measured; without the pool every round allocates megabytes (raw, decrypted, inflated, predicted).
        long least = long.MaxValue;
        for (int round = 0; round < 6; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var decoded = file.ReadStream(data, file.StreamCap)!.Value;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(decoded.Pooled);
            Assert.False(decoded.Truncated);
            Assert.Equal(content, Encoding.Latin1.GetString(decoded.Span).TrimEnd('\0')); // the predictor's last row is padded
            decoded.Release();
            if (round > 0) least = Math.Min(least, allocated);
        }
        Assert.True(least < 64 << 10, $"allocated {least >> 10} KB");
    }

    [Fact]
    public void Output_is_capped_and_marked_truncated()
    {
        var z = W.Zlib(new byte[100_000]);
        var capped = PdfFilters.Flate(z, 0, z.Length, 4096);
        Assert.Equal(4096, capped.Length);
        Assert.True(capped.Truncated);
        Assert.Equal(1000, PdfFilters.RunLength([129, 7, 129, 7, 129, 7, 129, 7, 129, 7, 129, 7, 129, 7, 129, 7, 128], 1000).Length);
    }

    [Fact]
    public void Png_predictors_of_every_row_type_and_tiff_predictor()
    {
        // Four columns, one row of each filter type (None, Sub, Up, Average, Paeth) applied to known rows.
        byte[][] rows = [[10, 20, 30, 40], [11, 22, 33, 44], [12, 24, 36, 48], [13, 26, 39, 52], [14, 28, 42, 56]];
        var encoded = new List<byte>();
        var prior = new byte[4];
        for (int type = 0; type < 5; type++)
        {
            encoded.Add((byte)type);
            var row = rows[type];
            for (int i = 0; i < 4; i++)
            {
                int left = i > 0 ? row[i - 1] : 0, up = prior[i], upLeft = i > 0 ? prior[i - 1] : 0;
                int predicted = type switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                };
                encoded.Add(unchecked((byte)(row[i] - predicted)));
            }
            prior = row;
        }
        var decoded = PdfFilters.Predict(new PdfBytes([.. encoded], encoded.Count, false), Parms("<< /Predictor 15 /Columns 4 >>"), 1000);
        Assert.Equal(rows.SelectMany(r => r).ToArray(), decoded.Span.ToArray());

        byte[] tiff = [10, 5, 5, 5, 20, 1, 1, 1];
        var t = PdfFilters.Predict(new PdfBytes(tiff, tiff.Length, false), Parms("<< /Predictor 2 /Columns 4 >>"), 1000);
        Assert.Equal(new byte[] { 10, 15, 20, 25, 20, 21, 22, 23 }, t.Span.ToArray());
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    [Fact]
    public void Image_codecs_and_unknown_filters_are_refused()
    {
        var raw = new PdfBytes([1, 2, 3], 3, false);
        Assert.Null(PdfFilters.Decode(raw, new PdfName("DCTDecode"), null, 1000, Budget));
        Assert.Null(PdfFilters.Decode(raw, new PdfName("JBIG2Decode"), null, 1000, Budget));
        Assert.Null(PdfFilters.Decode(raw, new PdfName("Made-up"), null, 1000, Budget));
    }

    [Theory]
    [InlineData("Flate")]
    [InlineData("FlatePng")]
    [InlineData("LZW")]
    [InlineData("A85")]
    [InlineData("AHx")]
    [InlineData("RL")]
    public void Content_streams_through_each_filter(string filter)
    {
        var w = W.SimpleDocument("placeholder");
        int content = w.AddStream("<< >>", Ascii(W.Lines("Filtered words here", "and a second line")), filter);
        w.Set(4, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {content} 0 R >>");
        Assert.Equal("Filtered words here\nand a second line", Read(w.Write()).Body);
    }

    [Fact]
    public void Content_streams_through_a_filter_chain()
    {
        var w = W.SimpleDocument("placeholder");
        int content = w.AddStream("<< >>", Ascii(W.Lines("Chained filters")), "A85", "Flate");
        w.Set(4, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {content} 0 R >>");
        Assert.Equal("Chained filters", Read(w.Write()).Body);
    }

    [Fact]
    public void A_content_stream_past_the_stream_cap_keeps_its_beginning()
    {
        var lines = Enumerable.Range(0, 3000).Select(i => $"Line {i} of a long page").ToArray();
        var doc = Read(W.SimpleDocument(W.Lines(lines)).Write(), limits: new ExtractLimits(MaxStreamBytes: 4096));
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.StartsWith("Line 0 of a long page\nLine 1", doc.Body);
        Assert.DoesNotContain("Line 2999", doc.Body);
    }
}

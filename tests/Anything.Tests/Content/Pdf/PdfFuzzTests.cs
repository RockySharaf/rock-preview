using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using static Anything.Tests.Content.Pdf.PdfTestSupport;
using W = Anything.Tests.Content.Pdf.PdfFixtureWriter;

namespace Anything.Tests.Content.Pdf;

/// <summary>
/// 1,000 mutations of each fixture, mostly of its structured fields (numbers: lengths, counts, offsets, object numbers,
/// xref entries; names; spans), on unfiltered variants so the mutations reach the reader rather than the inflater. Every
/// case runs under Task.Run(...).Wait(2 × the time budget) and must return a document: no exception, no hang, no
/// allocation storm. A failure names the fixture, the seed and the input in hex.
/// </summary>
public class PdfFuzzTests
{
    const int Cases = 1000;
    static readonly ExtractLimits Limits = new(MaxMilliseconds: 1500, MaxTextChars: 1 << 16);

    public static TheoryData<string> Fixtures => new() { "table", "objstm", "rc4", "aes256", "cid", "forms", "update", "oracle" };

    static byte[] Fixture(string name)
    {
        switch (name)
        {
            case "table":
                {
                    var w = W.SimpleDocument(false, W.Lines("Alpha 1 invoice", "beta"), null, "BT /F1 12 Tf 72 700 Td [(Kern) -300 (ed)] TJ 0 -14 Td (next) ' ET");
                    w.Info = w.Add("<< /Title (Fuzz title) /Keywords (a, b) >>");
                    return w.Write();
                }
            case "objstm":
                {
                    var w = W.SimpleDocument(false, W.Lines("Packed objects"), W.Lines("Second"));
                    w.CompressStructure = false;
                    w.ObjectsPerStream = 3;
                    return w.Write(W.XrefKind.Hybrid, objectStreams: true);
                }
            case "rc4":
                {
                    var w = W.SimpleDocument(false, W.Lines("Encrypted words"));
                    w.Info = w.Add("<< /Title " + W.Str("Secret") + " >>");
                    return w.Write(crypt: W.CryptKind.Rc4_128);
                }
            case "aes256":
                {
                    var w = W.SimpleDocument(false, W.Lines("Strong encryption"));
                    w.CompressStructure = false;
                    return w.Write(W.XrefKind.Stream, objectStreams: true, W.CryptKind.Aes256);
                }
            case "cid":
                {
                    var w = new W();
                    int catalog = w.Reserve(), pages = w.Reserve();
                    string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange "
                        + "2 beginbfchar <0001> <0041> <0002> <00660069> endbfchar 2 beginbfrange <0010> <0020> <0061> <0030> <0031> [<4E2D> <6587>] endbfrange endcmap end end";
                    int tu = w.AddStream("<< >>", Ascii(cmap));
                    int cid = w.Add("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /DW 900 /W [1 [500 600] 16 32 250] >>");
                    int font = w.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {tu} 0 R >>");
                    int rk = w.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /Y /Encoding /90ms-RKSJ-H /DescendantFonts [{cid} 0 R] >>");
                    int content = w.AddStream("<< >>", Ascii("BT /F1 12 Tf 72 700 Td <00010002001000110030> Tj /F2 12 Tf 0 -20 Td <938C8B9E32303234> Tj ET"));
                    int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Rotate 90 /CropBox [10 10 600 780] /Resources << /Font << /F1 {font} 0 R /F2 {rk} 0 R >> >> /Contents {content} 0 R >>");
                    w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
                    w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
                    w.Root = catalog;
                    return w.Write();
                }
            case "forms":
                {
                    var w = new W();
                    int catalog = w.Reserve(), pages = w.Reserve(), form = w.Reserve();
                    int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding << /Differences [65 /Euro /f_i] >> >>");
                    w.SetStream(form, $"<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Matrix [1 0 0 1 5 5] /Resources << /Font << /T {font} 0 R >> /XObject << /S {form} 0 R >> >> >>", Ascii("BT /T 9 Tf (In form AB) Tj ET /S Do"));
                    int ap = w.AddStream($"<< /Type /XObject /Subtype /Form /BBox [0 0 50 10] /Resources << /Font << /T {font} 0 R >> >> >>", Ascii("BT /T 8 Tf 1 2 Td (Field) Tj ET"));
                    int widget = w.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /V (Value) /Rect [10 10 60 20] /AP << /N {ap} 0 R >> >>");
                    int content = w.AddStream("<< >>", Ascii("q 1 0 0 1 10 10 cm /Fm Do Q BI /W 2 /H 2 /BPC 8 /CS /G ID \u0001\u0002EI\u0003 EI BT /T 12 Tf 72 700 Td (After image) Tj ET"));
                    int page = w.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /T {font} 0 R >> /XObject << /Fm {form} 0 R >> >> /Contents {content} 0 R /Annots [{widget} 0 R] >>");
                    w.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
                    w.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /AcroForm << /NeedAppearances false >> >>");
                    w.Root = catalog;
                    return w.Write();
                }
            case "update":
                {
                    var original = W.SimpleDocument(false, W.Lines("Old text")).Write();
                    int oldStart = int.Parse(Encoding.ASCII.GetString(original).Split("startxref\n")[^1].Split('\n')[0]);
                    string content = W.Lines("New text");
                    string obj = $"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n";
                    long xref = original.Length + obj.Length;
                    string tail = obj + $"xref\n5 1\n{original.Length:D10} 00000 n\r\ntrailer\n<< /Size 6 /Root 1 0 R /Prev {oldStart} >>\nstartxref\n{xref}\n%%EOF\n";
                    return [.. original, .. Ascii(tail)];
                }
            default:
                return PdfOracles.ObjectStreams;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Mutations_never_let_an_exception_out(string name)
    {
        var original = Fixture(name);
        // The unmutated fixture reads.
        Assert.Contains(Run(original, out _).Status, new[] { DocStatus.Ok, DocStatus.Truncated });
        var numbers = NumberSpans(original);
        for (int c = 0; c < Cases; c++)
        {
            int seed = name.Aggregate(17, (h, ch) => unchecked(h * 31 + ch)) * 1009 + c; // reproducible across runs
            var input = Mutate(original, numbers, new Random(seed));
            ExtractedDoc? doc = null;
            long allocated = 0;
            Exception? error = null;
            var task = Task.Run(() =>
            {
                try { doc = Run(input, out allocated); }
                catch (Exception e) { error = e; }
            });
            if (!await Finishes(task, 2 * Limits.MaxMilliseconds))
                Assert.Fail($"{name}: hung past {2 * Limits.MaxMilliseconds} ms; {Describe(seed, input)}");
            if (error != null) Assert.Fail($"{name}: {error.GetType().Name} escaped: {error.Message}; {Describe(seed, input)}");
            Assert.True(Enum.IsDefined(doc!.Status), $"{name}: status {doc.Status}; {Describe(seed, input)}");
            Assert.True(doc.Body.Length <= Limits.MaxTextChars, $"{name}: body {doc.Body.Length}; {Describe(seed, input)}");
            Assert.True(allocated < 128 << 20, $"{name}: allocated {allocated >> 20} MB; {Describe(seed, input)}");
            // Slow once can be a GC pause while the whole suite runs in parallel; a real loop is slow every time.
            if (doc.ElapsedMs > 750 && Run(input, out _).ElapsedMs > 750) slow.Add($"{doc.ElapsedMs} ms {doc.Status} seed {seed}");
        }
        // A few kilobytes are never worth more than a moment: anything slow is a loop that should have been bounded.
        Assert.True(slow.Count == 0, $"{name}: {slow.Count} slow cases: {string.Join("; ", slow.Take(5))}");
    }

    readonly List<string> slow = [];

    [Fact]
    public async Task Page_boxes_survive_mutations_too()
    {
        var original = Fixture("cid");
        var numbers = NumberSpans(original);
        for (int c = 0; c < 300; c++)
        {
            int seed = 7919 * c + 13;
            var input = Mutate(original, numbers, new Random(seed));
            Exception? error = null;
            var task = Task.Run(() =>
            {
                try
                {
                    var budget = new ExtractBudget(Limits, CancellationToken.None);
                    using var source = new MemoryByteSource(input, budget);
                    PdfPageText.FindBoxes(source, 0, t => t.Length > 2 ? [(0, 2)] : [], budget);
                }
                catch (ContentLimitException) { } // a document-level limit is the caller's to handle; nothing else may escape
                catch (Exception e) { error = e; }
            });
            if (!await Finishes(task, 2 * Limits.MaxMilliseconds)) Assert.Fail($"boxes hung; {Describe(seed, input)}");
            if (error != null) Assert.Fail($"boxes: {error.GetType().Name}: {error.Message}; {Describe(seed, input)}");
        }
    }

    static ExtractedDoc Run(byte[] input, out long allocated)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var budget = new ExtractBudget(Limits, CancellationToken.None);
        using var source = new MemoryByteSource(input, budget);
        var doc = ContentReaders.Extract(source, "pdf", ContentKind.Pdf, budget);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return doc;
    }

    /// <summary>Where the numbers are (object numbers, lengths, counts, offsets, xref entries, operands).</summary>
    static List<(int Start, int Length)> NumberSpans(byte[] file)
    {
        var spans = new List<(int, int)>();
        for (int i = 0; i < file.Length;)
        {
            if (file[i] is >= (byte)'0' and <= (byte)'9' && (i == 0 || !char.IsLetterOrDigit((char)file[i - 1])))
            {
                int j = i;
                while (j < file.Length && file[j] is >= (byte)'0' and <= (byte)'9') j++;
                spans.Add((i, j - i));
                i = j;
            }
            else i++;
        }
        return spans;
    }

    static readonly string[] Replacements = ["0", "1", "-1", "2", "65535", "2147483647", "2147483648", "99999999999", "4294967295", "-2147483648", "1e9", "007"];
    static readonly string[] Names = ["/Length", "/Kids", "/Count", "/Type", "/Pages", "/Page", "/Font", "/Contents", "/Filter", "/W", "/Index", "/Size", "/Prev", "/N", "/First", "/Encrypt", "/ToUnicode", "/Widths", "/Subtype"];

    static byte[] Mutate(byte[] original, List<(int Start, int Length)> numbers, Random rng)
    {
        var bytes = new List<byte>(original);
        int edits = 1 + rng.Next(4);
        for (int e = 0; e < edits && bytes.Count > 0; e++)
        {
            int kind = rng.Next(10);
            if (kind <= 4 && numbers.Count > 0)
            {
                // A number: a special value, a neighbour, or a random one (same length when it fits, so offsets stay put).
                var (start, length) = numbers[rng.Next(numbers.Count)];
                if (start + length > bytes.Count) continue;
                string old = Encoding.ASCII.GetString(bytes.GetRange(start, length).ToArray());
                long.TryParse(old, out long value);
                string next = rng.Next(3) switch
                {
                    0 => Replacements[rng.Next(Replacements.Length)],
                    1 => (value + rng.Next(-3, 4)).ToString(),
                    _ => rng.NextInt64(0, (long)Math.Pow(10, Math.Min(length, 12))).ToString().PadLeft(length, '0'),
                };
                bytes.RemoveRange(start, length);
                bytes.InsertRange(start, Encoding.ASCII.GetBytes(next));
            }
            else if (kind == 5)
            {
                // A name swapped for another.
                int at = bytes.IndexOf((byte)'/', rng.Next(bytes.Count));
                if (at < 0) continue;
                int end = at + 1;
                while (end < bytes.Count && char.IsLetterOrDigit((char)bytes[end])) end++;
                bytes.RemoveRange(at, end - at);
                bytes.InsertRange(at, Encoding.ASCII.GetBytes(Names[rng.Next(Names.Length)]));
            }
            else if (kind == 6)
            {
                int at = rng.Next(bytes.Count);
                bytes.RemoveRange(at, Math.Min(bytes.Count - at, 1 + rng.Next(64)));
            }
            else if (kind == 7)
            {
                int at = rng.Next(bytes.Count), length = Math.Min(bytes.Count - at, 1 + rng.Next(256));
                var copy = bytes.GetRange(at, length);
                bytes.InsertRange(rng.Next(bytes.Count), copy);
            }
            else if (kind == 8)
            {
                int at = rng.Next(bytes.Count);
                bytes.RemoveRange(at, bytes.Count - at); // truncated
            }
            else
            {
                for (int k = 0; k < 1 + rng.Next(8); k++) bytes[rng.Next(bytes.Count)] = (byte)rng.Next(256);
            }
        }
        return [.. bytes];
    }
}

using System.Buffers.Binary;
using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Store;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Store;

public class SegmentSizeTests(ITestOutputHelper output)
{
    /// <summary>
    /// The brief's sample of this PC, kind by kind (8,372 files, averages over all files of a kind): files, files with words,
    /// distinct words of each file that has words, and whether it has picture details.
    /// </summary>
    static readonly (ContentKind Kind, int Files, int WithWords, int Words, bool Picture)[] Brief =
    [
        (ContentKind.Pdf, 3000, 338, 462, false),      // 52 distinct words a PDF on average, 11% have text
        (ContentKind.Office, 358, 355, 512, false),    // 508 on average
        (ContentKind.Text, 2000, 1992, 415, false),    // 413
        (ContentKind.Design, 14, 3, 1143, true),       // 245
        (ContentKind.Picture, 3000, 110, 9, true),     // about 1 (keywords and titles)
    ];

    static readonly string[] Folders = ["Clients", "Invoices", "Projects", "Print", "Scans", "Archive", "2023", "2024", "Jobs", "Proofs", "Letters", "Photos"];

    /// <summary>
    /// Words by frequency rank, shaped like real text: frequent words short, rare ones long (about 9 letters), and one rare
    /// word in eight a number (dates, amounts, codes).
    /// </summary>
    static string[] RealisticVocabulary(int size)
    {
        var syllables = Synth.Vocabulary(4000, seed: 3).Where(w => w.Length <= 3).Distinct().ToArray();
        var rnd = new Random(4);
        var seen = new HashSet<string>();
        var words = new string[size];
        for (int r = 0; r < size; r++)
        {
            string w;
            do
            {
                if (r > 5000 && rnd.Next(8) == 0) w = rnd.Next(100, 10_000_000).ToString();
                else
                {
                    int n = r < 60 ? 1 : r < 3000 ? 2 : r < 60_000 ? 3 : 3 + rnd.Next(3);
                    var sb = new StringBuilder();
                    for (int i = 0; i < n; i++) sb.Append(syllables[rnd.Next(syllables.Length)]);
                    w = sb.ToString();
                }
            } while (w.Length < 2 || !seen.Add(w));
            words[r] = w;
        }
        return words;
    }

    [Fact]
    public void Index_bytes_per_document_follow_the_spec_model_within_15_percent()
    {
        var rnd = new Random(2026);
        var words = RealisticVocabulary(900_000);
        var zipf = new Synth.Zipf(words.Length, 1.13);
        var docs = new List<SynthDoc>();
        ulong key = 1;
        foreach (var (kind, files, withWords, distinct, picture) in Brief)
            for (int i = 0; i < files; i++)
            {
                var chosen = new HashSet<int>();
                if (i < withWords) while (chosen.Count < distinct) chosen.Add(zipf.Next(rnd));
                string body = string.Join(' ', chosen.Select(c => words[c]));
                bool fieldsOnly = kind == ContentKind.Picture;
                var doc = new StoredDoc(fieldsOnly ? "" : body, fieldsOnly && body.Length > 0 ? [(DocField.Keywords, body)] : [],
                    picture ? Synth.Picture(rnd) : null);
                // Paths like the brief's sample: about 90 characters, varied names with dates and numbers.
                string path = $"Users\\ROG\\{Folders[rnd.Next(4)]}\\{Folders[rnd.Next(Folders.Length)]}\\{words[rnd.Next(30_000)]} {words[rnd.Next(30_000)]} " +
                    $"{2015 + rnd.Next(11)}-{rnd.Next(1, 13):D2}\\{words[rnd.Next(200_000)]} {words[rnd.Next(200_000)]} {rnd.Next(1000)}_{words[rnd.Next(900_000)]}" +
                    $".{kind.ToString().ToLowerInvariant()}";
                var status = chosen.Count > 0 ? DocStatus.Ok : DocStatus.NoText;
                docs.Add(new SynthDoc(Synth.Row(key++ * 2_654_435_761UL, kind, status), path, doc));
            }
        docs.Sort((a, b) => a.Row.Key.CompareTo(b.Row.Key));

        var bytes = Synth.Segment(docs);
        using var seg = SegmentReader.Open(bytes, 1)!;
        long text = Section(bytes, SegmentFormat.Section.Text);
        long actual = bytes.Length - text;

        // §6: 1.37 B a (word, file) pair of postings + 1.8 B a pair of dictionary + 85 B a file (row and path) + 44 B of META.
        long pairs = docs.Sum(d => (long)Synth.Terms(d.Doc).Count(t => t[0] != '\u0001')); // the model counts words, not field-scoped copies
        double model = docs.Count * 85.0 + pairs * (1.37 + 1.8) + docs.Count(d => d.Doc.Picture != null) * 44.0;
        output.WriteLine($"{docs.Count:N0} files, {pairs:N0} pairs, {seg.TermCount:N0} distinct terms (brief: 1,169,777 pairs, 204,267 words)");
        output.WriteLine($"index without stored text: {actual:N0} B, model {model:N0} B ({actual / model - 1:P1}); stored text {text:N0} B");
        foreach (var s in new[] { SegmentFormat.Section.Postings, SegmentFormat.Section.Dict, SegmentFormat.Section.DictIndex, SegmentFormat.Section.Docs,
            SegmentFormat.Section.Paths, SegmentFormat.Section.PathIndex, SegmentFormat.Section.Meta })
            output.WriteLine($"  {s,-10} {Section(bytes, s),12:N0} B");
        output.WriteLine($"  postings {(Section(bytes, SegmentFormat.Section.Postings) + 0.0) / pairs:F2} B a pair, dictionary {(Section(bytes, SegmentFormat.Section.Dict) + 0.0) / seg.TermCount:F1} B a term, " +
            $"path {(Section(bytes, SegmentFormat.Section.Paths) + 0.0) / docs.Count:F1} B a file");

        Assert.InRange(seg.TermCount, 170_000, 240_000); // the replay looks like the brief's vocabulary
        Assert.InRange(actual / model, 0.85, 1.15);
    }

    static long Section(byte[] b, SegmentFormat.Section id)
    {
        int tocLength = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(b.Length - 16));
        int toc = b.Length - 16 - tocLength;
        for (int e = toc; e < toc + tocLength; e += SegmentFormat.TocEntrySize)
            if (b[e] == (byte)id) return SegmentFormat.TocEntry.Read(b.AsSpan(e)).Length;
        return 0;
    }
}

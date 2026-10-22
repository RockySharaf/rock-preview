using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Store;

public class StoreRoundTripTests
{
    static readonly List<SynthDoc> Docs = Synth.Docs(10_000, seed: 42);
    static readonly SortedDictionary<string, List<int>> Expected = Synth.Postings(Docs);
    static readonly byte[] Bytes = Synth.Segment(Docs, id: 7, volumes: [new VolumeEntry(0, "vol:{ABC}", "C:\\", DateTime.MinValue)]);

    static SegmentReader Open() => SegmentReader.Open(Bytes, 7) ?? throw new InvalidOperationException("didn't open");

    [Fact]
    public void Rows_keys_and_find_round_trip()
    {
        using var seg = Open();
        Assert.Equal(7u, seg.Id);
        Assert.Equal(Docs.Count, seg.DocCount);
        Assert.Equal(ContentTokenizer.Version, seg.TokenizerVersion);
        Assert.InRange(seg.CreatedUtc, DateTime.UtcNow.AddMinutes(-10).ToFileTimeUtc(), DateTime.UtcNow.AddMinutes(1).ToFileTimeUtc());
        for (int d = 0; d < Docs.Count; d++)
        {
            var expected = Docs[d].Row;
            var row = seg.Row(d);
            var flags = expected.Flags & ~(DocFlags.HasText | DocFlags.HasPicture);
            if (ContentTokenizer.HasWords(Docs[d].Doc.Body)) flags |= DocFlags.HasText;
            if (Docs[d].Doc.Picture != null) flags |= DocFlags.HasPicture;
            Assert.Equal(expected with { Flags = flags }, row);
            Assert.Equal(d, seg.Find(expected.Key));
        }
        Assert.Equal(-1, seg.Find(0));
        Assert.Equal(-1, seg.Find(ulong.MaxValue));
        Assert.Equal(-1, seg.Find(Docs[5].Row.Key + 1 == Docs[6].Row.Key ? 0 : Docs[5].Row.Key + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => seg.Row(Docs.Count));
    }

    [Fact]
    public void Stored_text_fields_paths_and_pictures_round_trip()
    {
        using var seg = Open();
        int pictures = 0;
        for (int d = 0; d < Docs.Count; d++)
        {
            Synth.SameDoc(Docs[d].Doc, seg.ReadStored(d));
            Assert.Equal(Docs[d].Path, seg.ReadPath(d));
            if (Docs[d].Doc.Picture is { } p)
            {
                Assert.True(seg.TryGetPicture(d, out var info));
                Assert.Equal(Synth.Q(p), info);
                Assert.Equal((d, Synth.Q(p)), seg.Picture(pictures++));
            }
            else Assert.False(seg.TryGetPicture(d, out _));
            var raw = seg.ReadStoredRaw(d);
            var stored = StoredText.Encode(Docs[d].Doc with { Picture = null }, out int rawLength);
            Assert.Equal(stored, raw.ToArray());
            Assert.Equal(rawLength == 0, raw.IsEmpty);
        }
        Assert.Equal(pictures, seg.PictureCount);
        Assert.False(seg.IsDamaged);
        Assert.Single(seg.Volumes);
    }

    [Fact]
    public void Every_term_has_exactly_its_documents()
    {
        using var seg = Open();
        Assert.Equal(Expected.Count, seg.TermCount);
        int inline = 0, skips = 0, plain = 0;
        foreach (var (term, docs) in Expected)
        {
            Assert.True(seg.TryGetTerm(Synth.Utf8(term), out var p), term);
            Assert.Equal(docs.Count, p.DocFreq);
            Assert.Equal(docs, Synth.DocsOf(seg, p));
            if (p.DocFreq <= 2) inline++;
            else if (p.DocFreq > 256) skips++;
            else plain++;
        }
        Assert.True(inline > 100 && skips > 10 && plain > 100, $"inline {inline}, skips {skips}, plain {plain}");

        // The dictionary walk gives the same terms in byte order.
        var all = Synth.ReadAll(seg);
        Assert.Equal(Expected.Keys, all.Keys);
        foreach (var (term, docs) in Expected) Assert.Equal(docs, all[term]);

        Assert.False(seg.TryGetTerm("zzzzzzzz"u8, out _));
        Assert.False(seg.TryGetTerm(""u8, out _));
        Assert.False(seg.TryGetTerm("\u0001"u8, out _));
        Assert.False(seg.IsDamaged);
    }

    [Fact]
    public void Prefix_ranges_visit_exactly_the_terms_that_start_with_the_prefix()
    {
        using var seg = Open();
        foreach (var prefix in new[] { "a", "ba", "st", "tra", "inv", "2024", "東", "caf", "\u0001A", "\u0001D", "zz", "chou" })
        {
            var from = Synth.Utf8(prefix);
            var to = (byte[])from.Clone();
            to = [.. to, 0xFF];
            var seen = new List<string>();
            seg.Terms(from, to, (ReadOnlySpan<byte> term, in TermPostings p) =>
            {
                seen.Add(Encoding.UTF8.GetString(term));
                return true;
            }, CancellationToken.None);
            var expected = Expected.Keys.Where(k => Synth.Utf8(k).AsSpan().StartsWith(from)).ToList();
            Assert.Equal(expected, seen);
        }

        // Stopping early, and an empty range.
        int visits = 0;
        seg.Terms(default, default, (ReadOnlySpan<byte> _, in TermPostings _) => ++visits < 5, CancellationToken.None);
        Assert.Equal(5, visits);
        visits = 0;
        seg.Terms("m"u8, "m"u8, (ReadOnlySpan<byte> _, in TermPostings _) => { visits++; return true; }, CancellationToken.None);
        Assert.Equal(0, visits);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => seg.Terms(default, default, (ReadOnlySpan<byte> _, in TermPostings _) => true, cts.Token));
    }

    [Fact]
    public void Field_terms_are_scoped_and_plain()
    {
        using var seg = Open();
        for (int d = 0; d < 200; d++)
            foreach (var (field, value) in Docs[d].Doc.Fields)
            {
                var token = ContentTokenizer.Fold(value.Split(' ', ';')[0]);
                if (token.Length < 2) continue;
                Assert.True(seg.TryGetTerm(Synth.Utf8(ContentTokenizer.FieldPrefix(field) + token), out var scoped));
                Assert.Contains(d, Synth.DocsOf(seg, scoped));
                Assert.True(seg.TryGetTerm(Synth.Utf8(token), out var plain));
                Assert.Contains(d, Synth.DocsOf(seg, plain));
            }
    }

    [Fact]
    public void Codes_and_cjk_terms_are_indexed()
    {
        using var seg = Open();
        foreach (var term in new[] { "inv", "2024", "東京", "京都", "café", "cafe", "résumé" })
        {
            var expected = Expected.GetValueOrDefault(ContentTokenizer.Fold(term));
            if (expected == null) continue;
            Assert.True(seg.TryGetTerm(Synth.Utf8(ContentTokenizer.Fold(term)), out var p), term);
            Assert.Equal(expected, Synth.DocsOf(seg, p));
        }
        var compound = Expected.Keys.First(k => k.StartsWith("inv2024", StringComparison.Ordinal));
        Assert.True(seg.TryGetTerm(Synth.Utf8(compound), out _));
    }

    [Fact]
    public void Long_postings_skip_while_advancing()
    {
        using var seg = Open();
        var (term, docs) = Expected.First(e => e.Value.Count > 1000);
        Assert.True(seg.TryGetTerm(Synth.Utf8(term), out var p));
        var cursor = seg.Cursor(p)!;
        Assert.Equal(docs.Count, cursor.Count);
        var all = new List<int>();
        var c2 = seg.Cursor(p)!;
        while (c2.MoveNext()) all.Add(c2.Doc);
        Assert.Equal(docs, all);

        // Advance to targets far ahead jumps through skip entries and lands on the first document at or after the target.
        var rnd = new Random(3);
        int last = -1;
        foreach (int target in Enumerable.Range(0, 50).Select(_ => rnd.Next(Docs.Count)).Order())
        {
            if (target <= last) continue;
            int expected = docs.FirstOrDefault(d => d >= target, -1);
            bool found = cursor.Advance(target);
            Assert.Equal(expected >= 0, found);
            if (found) Assert.Equal(expected, cursor.Doc);
            last = target;
        }
        Assert.True(cursor.SkipsTaken > 0);
    }

    [Fact]
    public void Statistics_count_per_kind()
    {
        using var seg = Open();
        foreach (var kind in Enum.GetValues<ContentKind>())
        {
            var mine = Docs.Where(d => d.Row.Kind == kind).ToList();
            var s = seg.StatsOf(kind);
            Assert.Equal(mine.Count, s.Docs);
            Assert.Equal(mine.Count(d => ContentTokenizer.HasWords(d.Doc.Body)), s.WithText);
            Assert.Equal(mine.Sum(d => (long)StoredText.BlobLength(d.Doc)), s.RawTextBytes);
            Assert.Equal(mine.Sum(d => (long)StoredText.Encode(d.Doc, out _).Length), s.StoredBytes);
            Assert.Equal(mine.Sum(d => (long)Synth.Terms(d.Doc).Count), s.Pairs);
            Assert.Equal(mine.Sum(d => d.BytesRead), s.BytesRead);
            Assert.Equal(mine.Sum(d => (long)d.Ms), s.MsTotal);
            Assert.Equal(mine.Count(d => d.Row.Status == DocStatus.Damaged), s.Problems);
        }
    }

    [Fact]
    public void A_file_segment_reads_the_same_as_the_memory_one()
    {
        using var temp = new TempFolder();
        var path = temp.File(SegmentFormat.FileName(7));
        File.WriteAllBytes(path, Bytes);
        using (var seg = SegmentReader.Open(path, 7)!)
        {
            Assert.NotNull(seg);
            for (int d = 0; d < Docs.Count; d += 97)
            {
                Synth.SameDoc(Docs[d].Doc, seg.ReadStored(d));
                Assert.Equal(Docs[d].Path, seg.ReadPath(d));
            }
            var term = Expected.Keys.ElementAt(Expected.Count / 2);
            Assert.True(seg.TryGetTerm(Synth.Utf8(term), out var p));
            Assert.Equal(Expected[term], Synth.DocsOf(seg, p));
            Assert.True(seg.Verify(CancellationToken.None));
            // Mapped with Read|Delete sharing: the file can be deleted while the segment is open.
            File.Delete(path);
            Assert.Equal(Docs[1].Path, seg.ReadPath(1));
        }
    }

    [Fact]
    public void Spilled_runs_give_the_same_postings_and_dictionary()
    {
        using var temp = new TempFolder();
        var docs = Docs.Take(3000).ToList();
        using var w = new SegmentWriter(ContentTokenizer.Version, temp.File("spill"), runBudget: 1 << 16);
        foreach (var d in docs) w.Add(d.Row, d.Path, d.Doc, default, d.BytesRead, d.Ms);
        Assert.True(w.RunCount >= 5, $"only {w.RunCount} runs");
        Assert.True(w.RunCount < docs.Count / 5, $"{w.RunCount} runs: spilling far too often"); // not a run per document
        var ms = new MemoryStream();
        w.Write(ms, 1);
        var spilled = SegmentReader.Open(ms.ToArray(), 1)!;
        var whole = SegmentReader.Open(Synth.Segment(docs), 1)!;
        Assert.Equal(Synth.ReadAll(whole), Synth.ReadAll(spilled));
        Assert.Equal(Synth.Postings(docs), Synth.ReadAll(spilled));
        w.Dispose();
        Assert.Empty(Directory.EnumerateFiles(temp.File("spill"))); // spill files go with the writer
        spilled.Dispose();
        whole.Dispose();
    }

    [Fact]
    public void Writer_rules()
    {
        using var w = new SegmentWriter();
        var doc = new StoredDoc("hello world", [], null);
        w.Add(Synth.Row(10), "a", doc);
        Assert.Throws<ArgumentException>(() => w.Add(Synth.Row(10), "b", doc)); // one per key
        Assert.Throws<ArgumentException>(() => w.Add(Synth.Row(5), "b", doc));  // ascending
        w.Add(Synth.Row(11), "b", new StoredDoc("", [], null));
        Assert.Equal(2, w.DocCount);
        Assert.Equal(StoredText.BlobLength(doc), w.RawBytes);
        Assert.True(w.MeasureStoredBytes() > 0);
        var info = w.Write(new MemoryStream(), 3);
        Assert.Equal([0, 1], info.LocalOfAdded);
        Assert.Equal(2, info.DocCount);
        Assert.Throws<InvalidOperationException>(() => w.Write(new MemoryStream(), 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentWriter(tokenizerVersion: 99));
    }

    [Fact]
    public void An_empty_document_and_a_document_with_only_fields_round_trip()
    {
        var docs = new List<SynthDoc>
        {
            new(Synth.Row(1, ContentKind.Picture, DocStatus.NoText), "p.jpg", new StoredDoc("", [(DocField.Keywords, "sunset; beach")], Synth.Picture(new Random(1)))),
            new(Synth.Row(2, ContentKind.Pdf, DocStatus.PasswordProtected), "x.pdf", new StoredDoc("", [], null)),
            new(Synth.Row(3), "y.docx", new StoredDoc("one two three", [], null)),
        };
        using var seg = SegmentReader.Open(Synth.Segment(docs), 1)!;
        for (int d = 0; d < docs.Count; d++) Synth.SameDoc(docs[d].Doc, seg.ReadStored(d));
        Assert.True(seg.ReadStoredRaw(1).IsEmpty);
        Assert.Equal(DocFlags.HasPicture, seg.Row(0).Flags);
        Assert.Equal(DocFlags.None, seg.Row(1).Flags);
        Assert.Equal(DocFlags.HasText, seg.Row(2).Flags);
        Assert.True(seg.TryGetTerm(Synth.Utf8(ContentTokenizer.FieldPrefix(DocField.Keywords) + "beach"), out var p));
        Assert.Equal([0], Synth.DocsOf(seg, p));
        Assert.Equal(1, seg.StatsOf(ContentKind.Pdf).Problems);
    }

    [Fact]
    public void Terms_of_a_document_past_the_caps_come_from_what_is_stored()
    {
        // A field over 256 KB is stored cut: the word at its end isn't stored, so it mustn't be a term either (a merge,
        // which tokenizes stored text, would lose it otherwise).
        var huge = string.Concat(Enumerable.Repeat("filler ", 50_000)) + "zzzunique";
        var doc = new StoredDoc("body words", [(DocField.Comment, huge)], null);
        Assert.False(StoredText.FitsWhole(doc));
        Assert.True(StoredText.FitsWhole(new StoredDoc("small", [(DocField.Title, "t")], null)));
        using var seg = SegmentReader.Open(Synth.Segment([new SynthDoc(Synth.Row(1), "a.txt", doc)]), 1)!;
        var stored = seg.ReadStored(0)!;
        Assert.DoesNotContain("zzzunique", stored.Fields[0].Value);
        Assert.False(seg.TryGetTerm("zzzunique"u8, out _));
        Assert.True(seg.TryGetTerm("filler"u8, out _));
        Assert.Equal(Synth.Postings([new SynthDoc(Synth.Row(1), "a.txt", stored)]), Synth.ReadAll(seg));
    }

    [Fact]
    public void An_empty_segment_round_trips()
    {
        using var seg = SegmentReader.Open(Synth.Segment([]), 2)!;
        Assert.Equal(0, seg.DocCount);
        Assert.Equal(-1, seg.Find(1));
        Assert.False(seg.TryGetTerm("a"u8, out _));
        int visits = 0;
        seg.Terms(default, default, (ReadOnlySpan<byte> _, in TermPostings _) => { visits++; return true; }, CancellationToken.None);
        Assert.Equal(0, visits);
    }
}

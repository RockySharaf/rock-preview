using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Store;

/// <summary>A fact that runs only when RP_STORE_PERF=1 (long measurements on this PC); otherwise reported as skipped.</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RP_STORE_PERF") != "1") Skip = "Measurement: set RP_STORE_PERF=1 to run it.";
    }
}

/// <summary>A fresh folder under %TEMP% that is deleted afterwards (best effort: mapped files may linger until released).</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rp-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string[] Names() => [.. Directory.EnumerateFileSystemEntries(Path).Select(System.IO.Path.GetFileName).Order()!];

    public void Dispose()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>One synthetic document.</summary>
internal sealed record SynthDoc(DocRow Row, string Path, StoredDoc Doc, long BytesRead = 0, int Ms = 0);

/// <summary>Deterministic synthetic documents: Zipf-distributed words, codes, CJK lines, fields and picture details.</summary>
internal static class Synth
{
    static readonly string[] Onsets = ["b", "c", "d", "f", "g", "h", "j", "k", "l", "m", "n", "p", "r", "s", "t", "v", "w", "z", "br", "st", "tr", "ch"];
    static readonly string[] Vowels = ["a", "e", "i", "o", "u", "ai", "ou"];

    /// <summary>A vocabulary of distinct lower-case words (deterministic).</summary>
    public static string[] Vocabulary(int size, int seed = 7)
    {
        var rnd = new Random(seed);
        var set = new HashSet<string>();
        var list = new List<string>(size);
        while (list.Count < size)
        {
            int syllables = 1 + rnd.Next(4);
            var sb = new StringBuilder();
            for (int i = 0; i < syllables; i++) sb.Append(Onsets[rnd.Next(Onsets.Length)]).Append(Vowels[rnd.Next(Vowels.Length)]);
            if (rnd.Next(4) == 0) sb.Append("n");
            var w = sb.ToString();
            if (w.Length >= 2 && set.Add(w)) list.Add(w);
        }
        return [.. list];
    }

    /// <summary>Samples ranks with probability ∝ 1/(rank+1)^s.</summary>
    public sealed class Zipf
    {
        readonly double[] _cumulative;

        public Zipf(int size, double s = 1.0)
        {
            _cumulative = new double[size];
            double sum = 0;
            for (int i = 0; i < size; i++) _cumulative[i] = sum += 1.0 / Math.Pow(i + 1, s);
            for (int i = 0; i < size; i++) _cumulative[i] /= sum;
        }

        public int Next(Random rnd)
        {
            int i = Array.BinarySearch(_cumulative, rnd.NextDouble());
            return Math.Min(i < 0 ? ~i : i, _cumulative.Length - 1);
        }
    }

    public static DocRow Row(ulong key, ContentKind kind = ContentKind.Office, DocStatus status = DocStatus.Ok, long size = 1000,
        long modified = 133_500_000_000_000_000L, ushort volume = 0, ushort pages = 1, byte version = 1, DocFlags flags = DocFlags.None)
        => new(key, modified, size, volume, pages, kind, status, version, flags);

    /// <summary>
    /// count documents with distinct keys, sorted by key: bodies of Zipf words (with some codes and CJK), Title/Keywords on
    /// some, picture details on about one in ten, a few problem statuses.
    /// </summary>
    public static List<SynthDoc> Docs(int count, int seed = 1, int vocabulary = 3000, int minWords = 20, int maxWords = 60, ushort volume = 0)
    {
        var rnd = new Random(seed);
        var words = Vocabulary(vocabulary, seed + 100);
        var zipf = new Zipf(words.Length);
        var keys = new HashSet<ulong>();
        var docs = new List<SynthDoc>(count);
        for (int i = 0; i < count; i++)
        {
            ulong key;
            do key = (ulong)rnd.NextInt64() ^ ((ulong)rnd.Next() << 40); while (!keys.Add(key));
            int n = rnd.Next(minWords, maxWords + 1);
            var sb = new StringBuilder();
            for (int w = 0; w < n; w++)
            {
                if (w > 0) sb.Append(w % 12 == 0 ? '\n' : ' ');
                sb.Append(words[zipf.Next(rnd)]);
            }
            if (rnd.Next(10) == 0) sb.Append($" INV-2024-{rnd.Next(10000):D4}");
            if (rnd.Next(25) == 0) sb.Append("\n東京都の会議 Café résumé");
            if (rnd.Next(30) == 0) sb.Append('\f').Append(words[zipf.Next(rnd)]).Append(" page two");
            var fields = new List<(DocField, string)>();
            if (rnd.Next(3) == 0) fields.Add((DocField.Title, $"{words[zipf.Next(rnd)]} {words[zipf.Next(rnd)]} report"));
            if (rnd.Next(5) == 0) fields.Add((DocField.Keywords, $"{words[zipf.Next(rnd)]}; {words[zipf.Next(rnd)]}"));
            PictureInfo? picture = rnd.Next(10) == 0 ? Picture(rnd) : null;
            var kind = picture != null ? ContentKind.Picture : (ContentKind)(1 + rnd.Next(5));
            var status = rnd.Next(40) == 0 ? DocStatus.Damaged : rnd.Next(30) == 0 ? DocStatus.Truncated : DocStatus.Ok;
            string body = status == DocStatus.Damaged ? "" : sb.ToString();
            if (status == DocStatus.Damaged) fields.Clear();
            var path = $"Users\\Me\\Documents\\Folder{rnd.Next(200)}\\{words[rnd.Next(words.Length)]}_{i}.{Ext(kind)}";
            docs.Add(new SynthDoc(Row(key, kind, status, size: rnd.Next(1, 10_000_000), modified: 133_000_000_000_000_000L + rnd.Next(),
                volume: volume, pages: (ushort)rnd.Next(1, 50), version: (byte)rnd.Next(1, 3), flags: status == DocStatus.Truncated ? DocFlags.Truncated : DocFlags.None),
                path, new StoredDoc(body, fields, picture), rnd.Next(100_000), rnd.Next(500)));
        }
        docs.Sort((a, b) => a.Row.Key.CompareTo(b.Row.Key));
        return docs;
    }

    static string Ext(ContentKind kind) => kind switch
    {
        ContentKind.Pdf => "pdf", ContentKind.Office => "docx", ContentKind.Text => "txt", ContentKind.Mail => "eml", ContentKind.Design => "psd", _ => "jpg",
    };

    public static PictureInfo Picture(Random rnd) => new(
        Width: rnd.Next(100, 8000), Height: rnd.Next(100, 8000), DpiX: rnd.Next(3) switch { 0 => 72, 1 => 150, _ => 300 }, DpiY: 300,
        BitDepth: 8, ColorMode: rnd.Next(2) == 0 ? ColorMode.Rgb : ColorMode.Cmyk, Orientation: (byte)rnd.Next(1, 9), ExifColorSpace: 1,
        Rating: (byte)rnd.Next(6), DateTakenUtc: 133_000_000_000_000_000L + rnd.Next(), HasGps: rnd.Next(2) == 0,
        LatitudeMicro: rnd.Next(-90_000_000, 90_000_000), LongitudeMicro: rnd.Next(-180_000_000, 180_000_000), Iso: rnd.Next(50, 6400),
        FNumber: 2.8f, ExposureSeconds: 0.004f, FocalLengthMm: 35.5f);

    /// <summary>The terms the store makes of a document (folded tokens of the body and fields, field-scoped ones too), as UTF-8 strings.</summary>
    public static HashSet<string> Terms(StoredDoc doc)
    {
        var sink = new SetSink();
        ContentTokenizer.Tokenize(doc.Body, sink);
        foreach (var (field, value) in doc.Fields) ContentTokenizer.TokenizeField(field, value, sink);
        return sink.Terms;
    }

    sealed class SetSink : ITokenSink
    {
        public HashSet<string> Terms { get; } = new(StringComparer.Ordinal);
        public void Token(ReadOnlySpan<char> folded, int start, int length, TokenKind kind) => Terms.Add(folded.ToString());
    }

    /// <summary>Expected postings: term → ascending local documents (docs in key order).</summary>
    public static SortedDictionary<string, List<int>> Postings(IReadOnlyList<SynthDoc> docs)
    {
        var map = new SortedDictionary<string, List<int>>(Utf8Order.Instance);
        for (int d = 0; d < docs.Count; d++)
            foreach (var t in Terms(docs[d].Doc))
            {
                if (!map.TryGetValue(t, out var list)) map[t] = list = [];
                list.Add(d);
            }
        return map;
    }

    /// <summary>Strings in UTF-8 byte order (the store's term order).</summary>
    public sealed class Utf8Order : IComparer<string>
    {
        public static readonly Utf8Order Instance = new();
        public int Compare(string? x, string? y) => Encoding.UTF8.GetBytes(x!).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y!));
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>Writes docs (sorted by key) into one segment in memory.</summary>
    public static byte[] Segment(IReadOnlyList<SynthDoc> docs, uint id = 1, string? spill = null, int runBudget = SegmentWriter.DefaultRunBudget,
        IReadOnlyList<VolumeEntry>? volumes = null)
    {
        using var w = new SegmentWriter(ContentTokenizer.Version, spill, runBudget) { Volumes = volumes ?? [] };
        foreach (var d in docs) w.Add(d.Row, d.Path, d.Doc, default, d.BytesRead, d.Ms);
        var ms = new MemoryStream();
        w.Write(ms, id);
        return ms.ToArray();
    }

    /// <summary>Commits docs through one batch.</summary>
    public static StoreChange Commit(IContentStore store, IEnumerable<SynthDoc> docs, IEnumerable<DocRef>? deletes = null)
    {
        using var batch = store.BeginBatch();
        foreach (var d in docs) batch.Add(d.Row, d.Path, d.Doc, d.BytesRead, d.Ms);
        foreach (var d in deletes ?? []) batch.Delete(d);
        return batch.Commit();
    }

    /// <summary>Every term of a segment with its documents, by walking the dictionary.</summary>
    public static SortedDictionary<string, List<int>> ReadAll(IContentSegment seg)
    {
        var map = new SortedDictionary<string, List<int>>(Utf8Order.Instance);
        var builder = new List<(string Term, TermPostings P)>();
        seg.Terms(default, default, (ReadOnlySpan<byte> term, in TermPostings p) =>
        {
            builder.Add((Encoding.UTF8.GetString(term), p));
            return true;
        }, CancellationToken.None);
        foreach (var (term, p) in builder) map[term] = DocsOf(seg, p);
        return map;
    }

    /// <summary>The documents of one term's postings in a segment.</summary>
    public static List<int> DocsOf(IContentSegment seg, in TermPostings p)
    {
        var b = new DocSetBuilder([seg.DocCount]);
        seg.ReadPostings(p, 0, b, CancellationToken.None);
        var list = new List<int>();
        b.Build().ForEach((_, d) => list.Add(d), CancellationToken.None);
        return list;
    }

    /// <summary>The live documents of a view keyed by document key.</summary>
    public static Dictionary<ulong, (DocRef Ref, DocRow Row)> Live(IStoreView view) => view.RowsByKey().ToDictionary(r => r.Row.Key, r => (r.Ref, r.Row));

    /// <summary>The picture details a reader gets back for p (the 44-byte record is lossy).</summary>
    public static PictureInfo Q(PictureInfo p) => SegmentFormat.Quantize(p);

    public static void SameDoc(StoredDoc expected, StoredDoc? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Body, actual!.Body);
        Assert.Equal(expected.Fields.Where(f => f.Value.Length > 0), actual.Fields);
        Assert.Equal(expected.Picture is { } p ? Q(p) : null, actual.Picture);
    }
}

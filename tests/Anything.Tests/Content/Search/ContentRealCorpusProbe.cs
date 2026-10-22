using System.Diagnostics;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Anything.Core.Content.Store;
using Anything.Core.Search;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Search;

/// <summary>
/// Manual probe on this PC's own text files: set RP_CONTENT_PROBE to a ';' list of folders (they are only read) and run
/// this test. It indexes up to 6,000 text files into fake segments with the real tokenizer and times searches. Output is
/// counts and timings only, never words of the files. Without the variable it does nothing.
/// </summary>
[Collection(ContentSearchSerial.Name)]
public class ContentRealCorpusProbe(ITestOutputHelper output)
{
    static readonly HashSet<string> TextExtensions = new(["txt", "md", "markdown", "csv", "tsv", "text"], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Searches_over_this_PCs_text_files()
    {
        string? folders = Environment.GetEnvironmentVariable("RP_CONTENT_PROBE");
        if (string.IsNullOrWhiteSpace(folders)) { output.WriteLine("RP_CONTENT_PROBE not set: nothing to measure."); return; }

        var index = new FileIndex();
        var files = new List<(string Path, string Body)>();
        long bytes = 0;
        var read = Stopwatch.StartNew();
        foreach (var folder in folders.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var path in Walk(folder))
            {
                if (files.Count >= 6000 || bytes > 300L << 20) break;
                string ext = Path.GetExtension(path).TrimStart('.');
                if (!TextExtensions.Contains(ext) || ContentTypes.IsDeniedFile(Path.GetFileName(path))) continue;
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var head = new byte[(int)Math.Min(fs.Length, 4L << 20)];
                    int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                    var encoding = TextDecoding.Detect(head.AsSpan(0, n));
                    if (encoding == null) continue;
                    string body = TextDecoding.Decode(head.AsSpan(0, n), encoding).Replace("\r\n", "\n").Replace('\r', '\n');
                    if (body.Length > 1 << 20) body = body[..(1 << 20)];
                    files.Add((path, body));
                    bytes += n;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        output.WriteLine($"read {files.Count:N0} text files, {bytes / 1048576.0:F1} MB in {read.Elapsed.TotalSeconds:F1} s");
        if (files.Count == 0) return;

        var ids = new int[files.Count];
        index.BulkAdd(add =>
        {
            int root = add.Add("P:", FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
            for (int i = 0; i < files.Count; i++) ids[i] = add.Add($"file{i}{Path.GetExtension(files[i].Path)}", root, FileAttributes.Archive, 1, 0, 0);
        });
        var build = Stopwatch.StartNew();
        var segments = new List<FakeSegment>();
        var map = DocIdMap.Empty(index.Generation).ToBuilder();
        const int PerSegment = 1000;
        for (int s = 0; s * PerSegment < files.Count; s++)
        {
            var docs = new List<FakeDoc>();
            for (int i = s * PerSegment; i < Math.Min(files.Count, (s + 1) * PerSegment); i++)
                docs.Add(new FakeDoc($"file{i}", new DocRow((ulong)i, 0, 1, 0, 0, ContentKind.Text, DocStatus.Ok, 1,
                    ContentTokenizer.HasWords(files[i].Body) ? DocFlags.HasText : 0), new StoredDoc(files[i].Body, [], null)));
            uint id = (uint)(s + 1);
            segments.Add(new FakeSegment(id, docs));
            map.AddSegment(id, docs.Count);
            for (int d = 0; d < docs.Count; d++) map.Set(id, d, ids[s * PerSegment + d]);
        }
        output.WriteLine($"tokenized into {segments.Count} segments in {build.Elapsed.TotalSeconds:F1} s");

        // Words to ask for, chosen from the files themselves by how many files hold them (never printed).
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, body) in files)
        {
            var sink = new WordSink();
            ContentTokenizer.Tokenize(body, sink);
            foreach (var w in sink.Words) df[w] = df.GetValueOrDefault(w) + 1;
        }
        var byFrequency = df.Where(p => p.Key.Length >= 4 && p.Key.All(char.IsLetter)).OrderByDescending(p => p.Value).Select(p => p.Key).ToList();
        if (byFrequency.Count < 50) { output.WriteLine("too few words"); return; }
        var rnd = new Random(42);
        var common = byFrequency.Take(20).ToList();
        var middle = byFrequency.Skip(byFrequency.Count / 10).Take(200).ToList();
        var rare = byFrequency.Skip(byFrequency.Count * 9 / 10).ToList();

        var source = new FakeContentSource();
        source.Publish(new ContentView(new FakeStoreView(segments, 1), map.Build(index.Generation), ContentMode.Documents));
        index.Content = source;
        var engine = new SearchEngine();
        var plain = new SearchOptions { MatchContent = true };

        void Measure(string label, IEnumerable<string> queries, SearchOptions options)
        {
            var cold = new List<double>();
            var warm = new List<double>();
            int results = 0, pending = 0;
            foreach (var q in queries)
            {
                var sw = Stopwatch.StartNew();
                var r = engine.Search(index, q, options, SortKey.Name, false, default);
                cold.Add(sw.Elapsed.TotalMilliseconds);
                if (r.ContentPending) pending++;
                SpinWait.SpinUntil(() => !engine.Search(index, q, options, SortKey.Name, false, default).ContentPending, 5000);
                sw.Restart();
                var again = engine.Search(index, q, options, SortKey.Name, false, default);
                warm.Add(sw.Elapsed.TotalMilliseconds);
                results += again.Ids.Length;
                Assert.Null(again.Error);
            }
            cold.Sort();
            warm.Sort();
            double P(List<double> t, double p) => t[Math.Min(t.Count - 1, (int)Math.Ceiling(t.Count * p) - 1)];
            output.WriteLine($"{label,-34} n={cold.Count,3}  cold p50 {P(cold, 0.5),7:F1} p95 {P(cold, 0.95),7:F1} ms  warm p50 {P(warm, 0.5),6:F2} p95 {P(warm, 0.95),6:F2} ms  " +
                $"{results / Math.Max(1, cold.Count),6:N0} results avg, {pending} pending past the budget");
        }

        Measure("plain common word", common, plain);
        Measure("plain 3-letter prefixes", common.Concat(middle.Take(40)).Select(w => w[..3]).Distinct(), plain);
        Measure("plain middle word", middle.Take(60), plain);
        Measure("plain rare word", rare.OrderBy(_ => rnd.Next()).Take(60), plain);
        Measure("content: middle word", middle.Skip(60).Take(60).Select(w => "content:" + w), new SearchOptions());
        Measure("content: phrase of two words", middle.Skip(120).Take(30).Select(w => $"content:\"{w} {common[rnd.Next(common.Count)]}\""), new SearchOptions());
        Measure("case:content: common word", common.Take(10).Select(w => "case:content:" + char.ToUpperInvariant(w[0]) + w[1..]), new SearchOptions());
        Measure("content: glob", middle.Skip(150).Take(20).Select(w => "content:" + w[..2] + "*" + w[^2..]), new SearchOptions());
        int decompressions = segments.Sum(s => s.Reads);
        output.WriteLine($"stored texts read for confirmations: {decompressions:N0}");
    }

    /// <summary>Files below a folder; folders that can't be listed (locked drives behind junctions, denied) are skipped.</summary>
    static IEnumerable<string> Walk(string root)
    {
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = [.. new DirectoryInfo(dir).GetDirectories().Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0).Select(d => d.FullName)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs) stack.Push(d);
        }
    }

    sealed class WordSink : ITokenSink
    {
        public readonly HashSet<string> Words = new(StringComparer.Ordinal);
        public void Token(ReadOnlySpan<char> folded, int start, int length, TokenKind kind)
        {
            if (kind == TokenKind.Word) Words.Add(folded.ToString());
        }
    }
}

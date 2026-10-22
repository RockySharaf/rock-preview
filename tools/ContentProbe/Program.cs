using System.Diagnostics;
using System.IO.Enumeration;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Microsoft.Win32.SafeHandles;

namespace ContentProbe;

/// <summary>
/// Manual measurements for search inside files. Every file measured is only opened for reading.
///   sample  --roots DIR... [--count N] [--seed S] --out LIST        random sample of PDFs under the roots
///   pdf     --list LIST --out DIR [--encrypt N] [--no-py]           our PDF reader against PyMuPDF (text, recall, bytes, ms)
///   folder  DIR... [--out DIR]                                      ContentReaders over every candidate file
///   text    FILE [--page N] [--query WORDS]                         our text of one file (and page boxes)
///   fuzz    --list LIST [--cases N] [--seed S]                      in-memory mutations of real files, budgets checked
///   esample, build, search, calib, jit, cpu, samples                  the whole pipeline on real files (Measure.cs)
/// </summary>
static partial class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("modes: sample, pdf, folder, text, fuzz, diff, boxes, esample, build, search"); return 2; }
        var o = Options.Parse(args[1..]);
        return args[0] switch
        {
            "sample" => Sample(o),
            "pdf" => PdfCompare(o),
            "folder" => Folder(o),
            "text" => Text(o),
            "fuzz" => Fuzz(o),
            "diff" => Diff(o),
            "boxes" => BoxesCompare(o),
            "esample" => StratifiedSample(o),
            "build" => Build(o),
            "search" => SearchLatency(o),
            "calib" => CalibrateRepeat(o),
            "jit" => JitDecay(o),
            "cpu" => ReaderCpu(o),
            "samples" => Samples(o),
            _ => 2,
        };
    }

    /// <summary>Our page boxes against PyMuPDF's search_for rectangles on upright pages (intersection over union).</summary>
    static int BoxesCompare(Options o)
    {
        var files = File.ReadAllLines(o.Get("list")!).Where(l => l.Length > 0).ToList();
        string folder = o.Get("out") ?? Path.Combine(Path.GetTempPath(), "ContentProbe");
        Directory.CreateDirectory(folder);
        string list = Path.Combine(folder, "boxes-in.txt"), output = Path.Combine(folder, "boxes-out.jsonl");
        File.WriteAllLines(list, files, new UTF8Encoding(false));
        RunPython("boxes", list, output);
        var ious = new List<double>();
        int words = 0, pyRects = 0, wordsWithout = 0, documents = 0;
        var poor = new List<string>();
        foreach (var line in File.ReadLines(output))
        {
            using var j = JsonDocument.Parse(line);
            var r = j.RootElement;
            if (!r.TryGetProperty("page", out var pageEl) || r.GetProperty("words").GetArrayLength() == 0) continue;
            string path = r.GetProperty("path").GetString()!;
            int page = pageEl.GetInt32();
            double w = r.GetProperty("width").GetDouble(), h = r.GetProperty("height").GetDouble();
            documents++;
            foreach (var item in r.GetProperty("words").EnumerateArray())
            {
                string word = item.GetProperty("word").GetString()!;
                var theirs = item.GetProperty("rects").EnumerateArray().Select(a => (a[0].GetDouble() / w, a[1].GetDouble() / h, a[2].GetDouble() / w, a[3].GetDouble() / h)).ToList();
                if (theirs.Count == 0) continue;
                words++;
                IReadOnlyList<PageBox> ours;
                try
                {
                    var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
                    var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
                    using var source = new ProbeSource(handle, budget);
                    ours = PdfPageText.FindBoxes(source, page, text =>
                    {
                        var hits = new List<(int, int)>();
                        for (int i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.OrdinalIgnoreCase))
                            hits.Add((i, word.Length));
                        return hits;
                    }, budget);
                }
                catch (Exception) { ours = []; }
                if (ours.Count == 0) wordsWithout++;
                foreach (var t in theirs)
                {
                    pyRects++;
                    double best = 0;
                    foreach (var b in ours) best = Math.Max(best, Iou(t, (b.X, b.Y, b.X + b.Width, b.Y + b.Height)));
                    ious.Add(best);
                    if (best < 0.5 && poor.Count < 20) poor.Add($"{best:F2} '{word}' p{page + 1} {path}");
                }
            }
        }
        ious.Sort();
        Console.WriteLine($"{documents} documents, {words} words, {pyRects} PyMuPDF rectangles; words we found no box for: {wordsWithout}");
        if (ious.Count > 0)
            Console.WriteLine($"IoU with our best box: median {Pct(ious, 0.5):F2}, p10 {Pct(ious, 0.1):F2}; >= 0.5 for {ious.Count(x => x >= 0.5)} ({(double)ious.Count(x => x >= 0.5) / ious.Count:P1})");
        foreach (var p in poor) Console.WriteLine("  " + p);
        return 0;
    }

    static double Iou((double X0, double Y0, double X1, double Y1) a, (double X0, double Y0, double X1, double Y1) b)
    {
        double ix = Math.Max(0, Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0)), iy = Math.Max(0, Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0));
        double inter = ix * iy, union = (a.X1 - a.X0) * (a.Y1 - a.Y0) + (b.X1 - b.X0) * (b.Y1 - b.Y0) - inter;
        return union > 0 ? inter / union : 0;
    }

    /// <summary>Tokens PyMuPDF finds in one file that we don't, and the other way round.</summary>
    static int Diff(Options o)
    {
        string path = o.Positional[0];
        string folder = Path.Combine(Path.GetTempPath(), "ContentProbe-diff-" + Environment.ProcessId);
        Directory.CreateDirectory(folder);
        var ours = ReadOurs(path);
        var py = RunPy([path], folder)[path];
        var a = TokensOf(ours.Body);
        var b = TokensOf(py.Text);
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"ours {a.Count} tokens ({ours.Status}), PyMuPDF {b.Count}");
        Console.WriteLine("missing: " + string.Join(' ', b.Where(t => !a.Contains(t)).Take(200)));
        Console.WriteLine("extra: " + string.Join(' ', a.Where(t => !b.Contains(t)).Take(200)));
        try { Directory.Delete(folder, true); } catch (IOException) { }
        return 0;
    }

    sealed class Options
    {
        public List<string> Positional = [];
        public Dictionary<string, List<string>> Named = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string k) => Named.TryGetValue(k, out var v) && v.Count > 0 ? v[0] : null;
        public int Int(string k, int d) => int.TryParse(Get(k), out int v) ? v : d;
        public bool Has(string k) => Named.ContainsKey(k);
        public List<string> All(string k) => Named.TryGetValue(k, out var v) ? v : [];

        public static Options Parse(string[] args)
        {
            var o = new Options();
            string? key = null;
            foreach (var a in args)
            {
                if (a.StartsWith("--", StringComparison.Ordinal)) { key = a[2..]; o.Named[key] = []; continue; }
                if (key != null) o.Named[key].Add(a);
                else o.Positional.Add(a);
            }
            return o;
        }
    }

    // ── Sampling ───────────────────────────────────────────────────────────────────────────────────────────────

    static int Sample(Options o)
    {
        var roots = o.All("roots");
        int count = o.Int("count", 600), seed = o.Int("seed", 7);
        var found = new List<string>();
        var sw = Stopwatch.StartNew();
        foreach (var root in roots)
        {
            var e = new FileSystemEnumerable<string>(root, (ref FileSystemEntry entry) => entry.ToFullPath(), new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x400000 | (FileAttributes)0x40000,
                MatchType = MatchType.Simple,
            })
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && entry.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && entry.Length > 0,
            };
            try { found.AddRange(e); } catch (Exception ex) { Console.Error.WriteLine($"{root}: {ex.GetType().Name}"); }
        }
        var rng = new Random(seed);
        var picked = found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(_ => rng.Next()).Take(count).ToList();
        File.WriteAllLines(o.Get("out")!, picked, new UTF8Encoding(false));
        Console.WriteLine($"{found.Count} PDFs found in {sw.Elapsed.TotalSeconds:F1} s; {picked.Count} sampled");
        return 0;
    }

    // ── Reading ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A file through RandomAccess, counting bytes read and the 64 KB blocks touched (what an aligned-block source would read).</summary>
    sealed class ProbeSource(SafeFileHandle handle, ExtractBudget budget) : IByteSource
    {
        readonly HashSet<long> _blocks = [];
        public long Length { get; } = RandomAccess.GetLength(handle);
        public long BytesRead { get; private set; }
        public long BlockBytes => _blocks.Count * (64L << 10);
        public int Reads { get; private set; }

        public int ReadAt(long offset, Span<byte> buffer)
        {
            budget.Check();
            if (offset >= Length || buffer.IsEmpty) return 0;
            int want = (int)Math.Min(buffer.Length, Length - offset);
            int n;
            try { n = RandomAccess.Read(handle, buffer[..want], offset); }
            catch (IOException e) { throw new ContentIoException(e.HResult & 0xFFFF, "read failed"); }
            budget.Read(n);
            BytesRead += n;
            Reads++;
            for (long b = offset >> 16; b <= (offset + Math.Max(1, n) - 1) >> 16; b++) _blocks.Add(b);
            return n;
        }

        public void Dispose() => handle.Dispose();
    }

    sealed record Ours(string Path, long Size, DocStatus Status, string Body, int Pages, long BytesRead, long BlockBytes, int Reads, double Ms, string? Error, int Fields);

    static Ours ReadOurs(string path, ExtractLimits? limits = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
            var budget = new ExtractBudget(limits ?? ExtractLimits.Local, CancellationToken.None);
            using var source = new ProbeSource(handle, budget);
            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var kind = ContentTypes.KindOf(Path.GetFileName(path));
            var doc = ContentReaders.Extract(source, ext, kind == ContentKind.None ? ContentKind.Pdf : kind, budget);
            return new Ours(path, source.Length, doc.Status, doc.Body, doc.Pages, source.BytesRead, source.BlockBytes, source.Reads, sw.Elapsed.TotalMilliseconds, null, doc.Fields.Count);
        }
        catch (Exception e)
        {
            return new Ours(path, 0, DocStatus.Damaged, "", 0, 0, 0, 0, sw.Elapsed.TotalMilliseconds, e.GetType().Name, 0);
        }
    }

    // ── Tokens ─────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class TokenSet : ITokenSink
    {
        public readonly HashSet<string> Tokens = new(StringComparer.Ordinal);
        public void Token(ReadOnlySpan<char> folded, int start, int length, TokenKind kind)
        {
            if (kind is TokenKind.Word or TokenKind.Part or TokenKind.Bigram) Tokens.Add(folded.ToString());
        }
    }

    static HashSet<string> TokensOf(string text)
    {
        var set = new TokenSet();
        ContentTokenizer.Tokenize(Normalise(text), set);
        return set.Tokens;
    }

    /// <summary>Both sides alike: NFC, ligatures as letters, private-use characters dropped.</summary>
    static string Normalise(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case 'ﬀ': sb.Append("ff"); break;
                case 'ﬁ': sb.Append("fi"); break;
                case 'ﬂ': sb.Append("fl"); break;
                case 'ﬃ': sb.Append("ffi"); break;
                case 'ﬄ': sb.Append("ffl"); break;
                case 'ﬅ' or 'ﬆ': sb.Append("st"); break;
                case >= '' and <= '': sb.Append(' '); break;
                case '�': sb.Append(' '); break;
                default: sb.Append(c); break;
            }
        }
        try { return sb.ToString().Normalize(NormalizationForm.FormC); } catch (ArgumentException) { return sb.ToString(); }
    }

    // ── PDF against PyMuPDF ────────────────────────────────────────────────────────────────────────────────────

    sealed record Py(string Path, string Text, int Pages, double Ms, string? Error, bool Encrypted);

    static Dictionary<string, Py> RunPy(IReadOnlyList<string> files, string folder)
    {
        string list = Path.Combine(folder, "py-in.txt"), output = Path.Combine(folder, "py-out.jsonl");
        File.WriteAllLines(list, files, new UTF8Encoding(false));
        RunPython("text", list, output);
        var result = new Dictionary<string, Py>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(output))
        {
            using var j = JsonDocument.Parse(line);
            var r = j.RootElement;
            string p = r.GetProperty("path").GetString()!;
            result[p] = new Py(p, r.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "", r.TryGetProperty("pages", out var pg) ? pg.GetInt32() : 0,
                r.GetProperty("ms").GetDouble(), r.TryGetProperty("error", out var er) ? er.GetString() : null, r.TryGetProperty("encrypted", out _));
        }
        return result;
    }

    static void RunPython(params string[] args)
    {
        string script = Path.Combine(AppContext.BaseDirectory, "pymupdf_probe.py");
        var psi = new ProcessStartInfo("python") { UseShellExecute = false, RedirectStandardError = true };
        psi.ArgumentList.Add("-P");
        psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("python failed: " + err[..Math.Min(err.Length, 2000)]);
    }

    static int PdfCompare(Options o)
    {
        var files = File.ReadAllLines(o.Get("list")!).Where(l => l.Length > 0).ToList();
        string folder = o.Get("out") ?? Path.Combine(Path.GetTempPath(), "ContentProbe");
        Directory.CreateDirectory(folder);
        Console.WriteLine($"{files.Count} files; output in {folder}");

        // Warm-up on a few files so the first timings aren't JIT.
        foreach (var f in files.Take(3)) ReadOurs(f);
        var ours = new List<Ours>();
        var sw = Stopwatch.StartNew();
        foreach (var f in files) ours.Add(ReadOurs(f));
        Console.WriteLine($"ours: {sw.Elapsed.TotalSeconds:F1} s");

        Dictionary<string, Py> py = [];
        if (!o.Has("no-py"))
        {
            sw.Restart();
            py = RunPy(files, folder);
            Console.WriteLine($"PyMuPDF: {sw.Elapsed.TotalSeconds:F1} s");
        }

        var rows = new List<string> { "path\tsize\tstatus\tpages\tpyPages\tourTokens\tpyTokens\trecall\tprecision\tbytesRead\tblockBytes\treads\tms\tpyMs\terror" };
        var recalls = new List<double>();
        int withTextOurs = 0, withTextPy = 0, both = 0, pyOnly = 0, oursOnly = 0, compared = 0;
        var worst = new List<(double Recall, string Path, int PyTokens)>();
        foreach (var r in ours)
        {
            var ourTokens = TokensOf(r.Body);
            py.TryGetValue(r.Path, out var p);
            var pyTokens = p != null ? TokensOf(p.Text) : [];
            bool ot = ourTokens.Count > 0, pt = pyTokens.Count > 0;
            if (ot) withTextOurs++;
            if (pt) withTextPy++;
            if (ot && pt) both++;
            if (pt && !ot) pyOnly++;
            if (ot && !pt) oursOnly++;
            double recall = double.NaN, precision = double.NaN;
            if (pt)
            {
                int hit = pyTokens.Count(ourTokens.Contains);
                recall = (double)hit / pyTokens.Count;
                precision = ot ? (double)ourTokens.Count(pyTokens.Contains) / ourTokens.Count : double.NaN;
                recalls.Add(recall);
                compared++;
                worst.Add((recall, r.Path, pyTokens.Count));
            }
            rows.Add(string.Join('\t', r.Path, r.Size, r.Status, r.Pages, p?.Pages ?? 0, ourTokens.Count, pyTokens.Count,
                F(recall), F(precision), r.BytesRead, r.BlockBytes, r.Reads, F(r.Ms), F(p?.Ms ?? double.NaN), r.Error ?? p?.Error ?? ""));
        }
        File.WriteAllLines(Path.Combine(folder, "pdf-results.tsv"), rows, new UTF8Encoding(false));

        var report = new StringBuilder();
        report.AppendLine($"Documents: {ours.Count}");
        report.AppendLine("Statuses (ours): " + string.Join(", ", ours.GroupBy(r => r.Status).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        report.AppendLine($"With text: ours {withTextOurs}, PyMuPDF {withTextPy}, both {both}, PyMuPDF only {pyOnly}, ours only {oursOnly}");
        if (recalls.Count > 0)
        {
            recalls.Sort();
            report.AppendLine($"Token recall over {compared} documents with PyMuPDF text: mean {recalls.Average():P1}, median {Pct(recalls, 0.5):P1}, p10 {Pct(recalls, 0.1):P1}; >= 95% in {recalls.Count(x => x >= 0.95)} ({(double)recalls.Count(x => x >= 0.95) / recalls.Count:P1})");
            double pooledHit = 0, pooledAll = 0;
            foreach (var r in ours)
            {
                if (!py.TryGetValue(r.Path, out var p)) continue;
                var pt = TokensOf(p.Text);
                if (pt.Count == 0) continue;
                var ot = TokensOf(r.Body);
                pooledHit += pt.Count(ot.Contains);
                pooledAll += pt.Count;
            }
            report.AppendLine($"Pooled token recall: {pooledHit / Math.Max(1, pooledAll):P2}");
        }
        var ratios = ours.Where(r => r.Size > 0).Select(r => (double)r.BytesRead / r.Size).OrderBy(x => x).ToList();
        var blockRatios = ours.Where(r => r.Size > 0).Select(r => Math.Min(1, (double)r.BlockBytes / r.Size)).OrderBy(x => x).ToList();
        var ms = ours.Select(r => r.Ms).OrderBy(x => x).ToList();
        if (ratios.Count > 0)
        {
            report.AppendLine($"BytesRead/size: median {Pct(ratios, 0.5):P2}, p95 {Pct(ratios, 0.95):P1}; 64 KB blocks: median {Pct(blockRatios, 0.5):P2}, p95 {Pct(blockRatios, 0.95):P1}");
            var big = ours.Where(r => r.Size > 1 << 20).Select(r => (double)r.BytesRead / r.Size).OrderBy(x => x).ToList();
            if (big.Count > 0) report.AppendLine($"  files over 1 MB ({big.Count}): median {Pct(big, 0.5):P2}, p95 {Pct(big, 0.95):P1}");
            foreach (var root in ours.GroupBy(r => Path.GetPathRoot(r.Path) ?? ""))
            {
                var rr = root.Where(r => r.Size > 0).Select(r => (double)r.BytesRead / r.Size).OrderBy(x => x).ToList();
                var rb = root.Where(r => r.Size > 0).Select(r => Math.Min(1, (double)r.BlockBytes / r.Size)).OrderBy(x => x).ToList();
                if (rr.Count > 0) report.AppendLine($"  under {root.Key} ({rr.Count}): median {Pct(rr, 0.5):P2}, p95 {Pct(rr, 0.95):P1}; 64 KB blocks median {Pct(rb, 0.5):P2}");
            }
            var bytes = ours.Select(r => (double)r.BytesRead).OrderBy(x => x).ToList();
            report.AppendLine($"BytesRead per file: median {Pct(bytes, 0.5) / 1024:F0} KB, p95 {Pct(bytes, 0.95) / 1024:F0} KB, mean {bytes.Average() / 1024:F0} KB");
        }
        report.AppendLine($"ms (ours, local): mean {ms.Average():F1}, median {Pct(ms, 0.5):F1}, p95 {Pct(ms, 0.95):F1}, max {ms[^1]:F0}");
        if (py.Count > 0)
        {
            var pms = py.Values.Select(p => p.Ms).OrderBy(x => x).ToList();
            report.AppendLine($"ms (PyMuPDF, warm cache): mean {pms.Average():F1}, median {Pct(pms, 0.5):F1}, p95 {Pct(pms, 0.95):F1}");
        }
        report.AppendLine("Lowest recall:");
        foreach (var (rec, path, n) in worst.OrderBy(w => w.Recall).Take(25)) report.AppendLine($"  {rec:P0} of {n}: {path}");

        if (o.Has("encrypt")) EncryptedCopies(o, ours, folder, report);
        File.WriteAllText(Path.Combine(folder, "pdf-report.txt"), report.ToString(), new UTF8Encoding(false));
        Console.Write(report);
        return 0;
    }

    static void EncryptedCopies(Options o, List<Ours> ours, string folder, StringBuilder report)
    {
        int n = o.Int("encrypt", 40);
        var picked = ours.Where(r => r.Status is DocStatus.Ok or DocStatus.Truncated && r.Size < 20 << 20).Take(n).Select(r => r.Path).ToList();
        string copies = Path.Combine(Path.GetTempPath(), "ContentProbe-encrypted-" + Environment.ProcessId);
        Directory.CreateDirectory(copies);
        string list = Path.Combine(folder, "enc-in.txt"), output = Path.Combine(folder, "enc-out.jsonl");
        File.WriteAllLines(list, picked, new UTF8Encoding(false));
        RunPython("encrypt", list, copies, output);
        var counts = new Dictionary<string, (int Same, int Differ, int Locked)>();
        var differences = new List<string>();
        foreach (var line in File.ReadLines(output))
        {
            using var j = JsonDocument.Parse(line);
            var r = j.RootElement;
            if (r.TryGetProperty("error", out _)) continue;
            var c = r.GetProperty("copies");
            if (!c.TryGetProperty("plain", out var plainPath)) continue;
            var plain = ReadOurs(plainPath.GetString()!);
            foreach (var m in c.EnumerateObject())
            {
                if (m.Name == "plain") continue;
                var enc = ReadOurs(m.Value.GetString()!);
                counts.TryGetValue(m.Name, out var t);
                if (enc.Status == DocStatus.PasswordProtected) t.Locked++;
                else if (enc.Body == plain.Body && enc.Status == plain.Status) t.Same++;
                else { t.Differ++; differences.Add($"{m.Name}: {r.GetProperty("path").GetString()} ({plain.Status} {plain.Body.Length} chars vs {enc.Status} {enc.Body.Length})"); }
                counts[m.Name] = t;
            }
        }
        report.AppendLine($"Encrypted copies (PyMuPDF, empty user password) of {picked.Count} documents, compared with PyMuPDF's plain copy:");
        foreach (var (k, v) in counts) report.AppendLine($"  {k}: identical {v.Same}, different {v.Differ}, refused {v.Locked}");
        foreach (var d in differences.Take(20)) report.AppendLine("  " + d);
        try { Directory.Delete(copies, true); } catch (IOException) { }
    }

    // ── Other modes ────────────────────────────────────────────────────────────────────────────────────────────

    static int Folder(Options o)
    {
        var files = new List<string>();
        foreach (var root in o.Positional)
        {
            if (File.Exists(root)) { files.Add(root); continue; }
            foreach (var f in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                if (ContentTypes.KindOf(Path.GetFileName(f)) != ContentKind.None) files.Add(f);
        }
        var results = files.Select(f => ReadOurs(f)).ToList();
        foreach (var g in results.GroupBy(r => Path.GetExtension(r.Path).ToLowerInvariant()))
        {
            var ms = g.Select(r => r.Ms).OrderBy(x => x).ToList();
            Console.WriteLine($"{g.Key}: {g.Count()} files, {g.Count(r => r.Body.Length > 0)} with text; ms median {Pct(ms, 0.5):F1} p95 {Pct(ms, 0.95):F1}; " +
                string.Join(", ", g.GroupBy(r => r.Status).Select(s => $"{s.Key} {s.Count()}")));
        }
        return 0;
    }

    static int Text(Options o)
    {
        string path = o.Positional[0];
        var r = ReadOurs(path);
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"{r.Status} pages {r.Pages} fields {r.Fields} read {r.BytesRead} of {r.Size} in {r.Reads} reads, {r.Ms:F1} ms {r.Error}");
        Console.WriteLine(r.Body.Length > 4000 && !o.Has("all") ? r.Body[..4000] + "…" : r.Body);
        if (o.Get("query") is { } q)
        {
            int page = o.Int("page", 0);
            var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
            var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var source = new ProbeSource(handle, budget);
            var word = ContentTokenizer.Analyze(q, false);
            var boxes = PdfPageText.FindBoxes(source, page, text =>
            {
                var list = new List<(int, int)>();
                for (int from = 0; from < text.Length;)
                {
                    var (s, l) = ContentTokenizer.FindSequence(text, word, false, false, false, from);
                    if (s < 0) break;
                    list.Add((s, l));
                    from = s + Math.Max(1, l);
                }
                return list;
            }, budget);
            foreach (var b in boxes) Console.WriteLine($"box {b.X:F3} {b.Y:F3} {b.Width:F3} {b.Height:F3}");
        }
        return 0;
    }

    static int Fuzz(Options o)
    {
        var files = File.ReadAllLines(o.Get("list")!).Where(l => l.Length > 0 && File.Exists(l) && new FileInfo(l).Length < 4 << 20).Take(o.Int("files", 50)).ToList();
        int cases = o.Int("cases", 200), seed = o.Int("seed", 1);
        var limits = new ExtractLimits(MaxMilliseconds: 2000);
        string? copies = null;
        if (o.Has("expanded"))
        {
            // PyMuPDF writes uncompressed copies (streams decoded, no object streams) into a temporary folder, so the
            // mutations reach the reader's structure code rather than the inflater.
            copies = Path.Combine(Path.GetTempPath(), "ContentProbe-expanded-" + Environment.ProcessId);
            Directory.CreateDirectory(copies);
            string list = Path.Combine(copies, "in.txt"), output = Path.Combine(copies, "out.jsonl");
            File.WriteAllLines(list, files, new UTF8Encoding(false));
            RunPython("expand", list, copies, output);
            files = [.. File.ReadLines(output).Select(l => JsonDocument.Parse(l).RootElement).Where(r => r.TryGetProperty("copy", out _)).Select(r => r.GetProperty("copy").GetString()!)
                .Where(p => new FileInfo(p).Length < 8 << 20)];
        }
        int escaped = 0, hung = 0, slow = 0, total = 0;
        long maxAllocated = 0;
        var sw = Stopwatch.StartNew();
        foreach (var f in files)
        {
            byte[] original = File.ReadAllBytes(f);
            var numbers = new List<(int Start, int Length)>();
            for (int i = 0; i < original.Length;)
            {
                if (original[i] is >= (byte)'0' and <= (byte)'9' && (i == 0 || !char.IsLetterOrDigit((char)original[i - 1])))
                {
                    int j = i;
                    while (j < original.Length && original[j] is >= (byte)'0' and <= (byte)'9') j++;
                    numbers.Add((i, j - i));
                    i = j;
                }
                else i++;
            }
            var rng = new Random(seed ^ f.GetHashCode(StringComparison.Ordinal));
            for (int c = 0; c < cases; c++)
            {
                var bytes = new List<byte>(original);
                int edits = 1 + rng.Next(6);
                for (int e = 0; e < edits && bytes.Count > 1; e++)
                {
                    int at = rng.Next(bytes.Count);
                    switch (rng.Next(6))
                    {
                        case 0 or 1 or 2 when numbers.Count > 0:
                            {
                                // A structured field: a number (object number, length, count, offset, operand) changed.
                                var (start, length) = numbers[rng.Next(numbers.Count)];
                                if (start + length > bytes.Count) break;
                                string next = rng.Next(3) switch
                                {
                                    0 => new[] { "0", "1", "-1", "2147483647", "99999999999", "65535" }[rng.Next(6)],
                                    1 => (long.TryParse(Encoding.ASCII.GetString([.. bytes.GetRange(start, length)]), out long v) ? v + rng.Next(-2, 3) : 0).ToString(CultureInfo.InvariantCulture),
                                    _ => rng.NextInt64(0, (long)Math.Pow(10, Math.Min(length, 12))).ToString(CultureInfo.InvariantCulture).PadLeft(length, '0'),
                                };
                                bytes.RemoveRange(start, length);
                                bytes.InsertRange(start, Encoding.ASCII.GetBytes(next));
                                break;
                            }
                        case 3: bytes[at] = (byte)rng.Next(256); break;
                        case 4: bytes.RemoveRange(at, Math.Min(bytes.Count - at, 1 + rng.Next(64))); break;
                        default: bytes.RemoveRange(at, bytes.Count - at); break;
                    }
                }
                total++;
                var input = bytes.ToArray();
                long allocated = 0;
                ExtractedDoc? doc = null;
                var budget = new ExtractBudget(limits, CancellationToken.None);
                var task = Task.Run(() =>
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    using var source = new MemoryByteSource(input, budget);
                    doc = ContentReaders.Extract(source, "pdf", ContentKind.Pdf, budget);
                    allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                });
                try
                {
                    if (!task.Wait(2 * limits.MaxMilliseconds)) { hung++; Console.WriteLine($"HUNG {f} case {c}"); continue; }
                }
                catch (AggregateException e) { escaped++; Console.WriteLine($"ESCAPED {f} case {c}: {e.InnerException?.GetType().Name}"); continue; }
                maxAllocated = Math.Max(maxAllocated, allocated);
                if (doc!.ElapsedMs > 1000) slow++;
            }
        }
        Console.WriteLine($"{total} cases over {files.Count} files{(copies != null ? " (PyMuPDF-expanded copies)" : "")} in {sw.Elapsed.TotalSeconds:F0} s: "
            + $"{escaped} exceptions escaped, {hung} hung, {slow} over 1 s, most allocated by one case {maxAllocated >> 20} MB");
        if (copies != null) try { Directory.Delete(copies, true); } catch (IOException) { }
        return escaped + hung == 0 ? 0 : 1;
    }

    static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
    static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.###", CultureInfo.InvariantCulture);
}

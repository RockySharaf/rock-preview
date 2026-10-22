using System.Diagnostics;
using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content.Readers;

/// <summary>
/// A manual probe over real files (skipped unless RP_CONTENT_PROBE names folders, separated by ';'): samples candidates
/// per extension, reads them with the real FileContentSource (read-only, Date Accessed kept, cloud placeholders never
/// opened) and writes statuses, timings and bytes read per extension to RP_CONTENT_PROBE_OUT. No document text is written.
/// </summary>
public class RealFilesProbe
{
    [Fact]
    public void Probe_real_files()
    {
        var folders = Environment.GetEnvironmentVariable("RP_CONTENT_PROBE");
        if (string.IsNullOrWhiteSpace(folders)) return;
        int perExtension = int.TryParse(Environment.GetEnvironmentVariable("RP_CONTENT_PROBE_COUNT"), out int n) ? n : 200;
        // RP_CONTENT_PROBE_EXT limits the probe to some extensions and lists every file that isn't Ok.
        var only = Environment.GetEnvironmentVariable("RP_CONTENT_PROBE_EXT")?.Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool detail = only != null;
        string output = Environment.GetEnvironmentVariable("RP_CONTENT_PROBE_OUT") ?? Path.Combine(Path.GetTempPath(), "rp-content-probe.txt");
        var random = new Random(1);
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        foreach (var folder in folders.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*", options); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                if (file.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) || file.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(file);
                if (ContentTypes.CandidateKind(name, ContentKinds.All, ContentMode.DocumentsAndPictures, null) == ContentKind.None) continue;
                string ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
                if (only != null && !only.Contains(ext)) continue;
                if (!samples.TryGetValue(ext, out var list)) samples[ext] = list = [];
                int count = seen[ext] = seen.GetValueOrDefault(ext) + 1;
                if (list.Count < perExtension) list.Add(file);
                else if (random.Next(count) < perExtension) list[random.Next(perExtension)] = file; // reservoir sample
            }
        }
        var source = new FileContentSource();
        var report = new StringBuilder();
        report.AppendLine($"ext\tfiles\tsampled\tstatuses\tp50ms\tp95ms\tmaxms\tmedianReadPct\tmedianReadKB\twithText\tavgChars\tpics");
        foreach (var (ext, files) in samples.OrderBy(s => s.Key))
        {
            var times = new List<double>();
            var readRatio = new List<double>();
            var readKb = new List<double>();
            var statuses = new Dictionary<string, int>();
            int withText = 0, pictures = 0;
            long chars = 0;
            var slow = new List<string>();
            foreach (var file in files)
            {
                var kind = ContentTypes.KindOf(Path.GetFileName(file.AsSpan()));
                var request = new ExtractRequest(file, kind, -1, 0, false, false, 2048L << 20, ExtractLimits.Local);
                var sw = Stopwatch.StartNew();
                var read = source.Read(request, CancellationToken.None);
                double ms = sw.Elapsed.TotalMilliseconds;
                if (read.Retry || read.Offline) { string key = read.Offline ? "Offline" : "Retry"; statuses[key] = statuses.GetValueOrDefault(key) + 1; continue; }
                var doc = read.Doc;
                statuses[doc.Status.ToString()] = statuses.GetValueOrDefault(doc.Status.ToString()) + 1;
                if (doc.Status is DocStatus.CloudOnly or DocStatus.OptedOut or DocStatus.EfsEncrypted or DocStatus.AccessDenied) continue;
                times.Add(ms);
                if (read.Size > 0) readRatio.Add(100.0 * doc.BytesRead / read.Size);
                readKb.Add(doc.BytesRead / 1024.0);
                if (doc.Body.Length > 0) { withText++; chars += doc.Body.Length; }
                if (doc.Picture != null) pictures++;
                if (ms > 500 || doc.Status is DocStatus.Damaged or DocStatus.TimedOut || (detail && doc.Status != DocStatus.Ok))
                    slow.Add($"  {doc.Status} {ms:F0} ms {read.Size / 1024} KB {doc.Body.Length} chars {doc.Pages} pages {file}");
            }
            times.Sort(); readRatio.Sort(); readKb.Sort();
            double P(List<double> v, double q) => v.Count == 0 ? 0 : v[Math.Min(v.Count - 1, (int)(v.Count * q))];
            report.AppendLine($"{ext}\t{seen[ext]}\t{files.Count}\t{string.Join(",", statuses.OrderBy(s => s.Key).Select(s => $"{s.Key}:{s.Value}"))}\t" +
                $"{P(times, 0.5):F1}\t{P(times, 0.95):F1}\t{(times.Count > 0 ? times[^1] : 0):F0}\t{P(readRatio, 0.5):F2}\t{P(readKb, 0.5):F0}\t{withText}\t" +
                $"{(withText > 0 ? chars / withText : 0)}\t{pictures}");
            foreach (var s in slow.Take(detail ? 1000 : 20)) report.AppendLine(s);
        }
        File.WriteAllText(output, report.ToString());
    }
}

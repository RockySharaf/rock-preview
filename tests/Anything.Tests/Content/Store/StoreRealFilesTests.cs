using System.Buffers.Binary;
using System.Diagnostics;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Anything.Core.Content.Store;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Store;

/// <summary>Runs only when RP_STORE_REAL names a folder: measures the store on that folder's own text files.</summary>
public sealed class RealFilesFactAttribute : FactAttribute
{
    public RealFilesFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RP_STORE_REAL"))) Skip = "Measurement: set RP_STORE_REAL to a folder to run it.";
    }
}

/// <summary>
/// R3 on real files: up to 3,000 plain-text files (txt, md, csv, tsv) under RP_STORE_REAL, read only, decoded like the
/// text reader (first 1 MB of text), written into one segment in memory. Nothing is written to disk; only totals are printed.
/// </summary>
public class StoreRealFilesTests(ITestOutputHelper output)
{
    [RealFilesFact]
    public void Real_text_files_index_size_against_the_spec_model()
    {
        string root = Environment.GetEnvironmentVariable("RP_STORE_REAL")!;
        // As the app's scope rules: never cloud placeholders (reading would download them), EFS or "don't index" files,
        // nor anything under a denied folder (.git, node_modules, AppData …) or with a denied name.
        const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Encrypted | FileAttributes.NotContentIndexed
                | FileAttributes.Temporary | RecallOnOpen | RecallOnDataAccess,
        };
        var files = Directory.EnumerateFiles(root, "*", options)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".txt" or ".md" or ".csv" or ".tsv")
            .Where(f => !ContentTypes.IsDeniedFile(Path.GetFileName(f)))
            .Where(f => !Path.GetRelativePath(root, Path.GetDirectoryName(f)!).Split('\\').Any(c => ContentTypes.IsDeniedFolder(c)))
            .Take(3000).ToList();
        var docs = new List<SynthDoc>();
        var clock = Stopwatch.StartNew();
        long rawText = 0;
        int accessChanged = 0;
        foreach (var f in files)
        {
            string text;
            try
            {
                using var handle = NoAccessTimeHandle.Open(f);
                if (handle == null) continue; // can't keep Date Accessed as it is: leave the file alone
                // Through the handle, from the file's own record (a path query can return the directory's lazy copy).
                var accessed = File.GetLastAccessTimeUtc(handle);
                using var fs = new FileStream(handle, FileAccess.Read);
                var buffer = new byte[(int)Math.Min(fs.Length, 4 << 20)];
                fs.ReadExactly(buffer);
                if (File.GetLastAccessTimeUtc(handle) != accessed)
                {
                    accessChanged++;
                    output.WriteLine($"Date Accessed moved: {Path.GetRelativePath(root, f)}");
                }
                var encoding = TextDecoding.Detect(buffer);
                text = encoding == null ? "" : TextDecoding.Decode(buffer, encoding);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (text.Length == 0) continue;
            if (text.Length > 1 << 20) text = text[..(1 << 20)];
            text = text.Replace("\r\n", "\n");
            rawText += text.Length;
            docs.Add(new SynthDoc(Synth.Row(ContentKeys.Of("path:X:", f), ContentKind.Text), Path.GetRelativePath(root, f), new StoredDoc(text, [], null)));
        }
        docs = [.. docs.DistinctBy(d => d.Row.Key).OrderBy(d => d.Row.Key)];
        output.WriteLine($"{docs.Count:N0} text files read in {clock.Elapsed.TotalSeconds:F1} s ({rawText / 1e6:F1} M characters); Date Accessed changed on {accessChanged}");
        Assert.Equal(0, accessChanged);

        clock.Restart();
        var bytes = Synth.Segment(docs);
        output.WriteLine($"segment written in {clock.Elapsed.TotalSeconds:F1} s: {bytes.Length / 1e6:F2} MB, {bytes.Length / (double)docs.Count:F0} B a file");
        using var seg = SegmentReader.Open(bytes, 1)!;
        var stats = seg.StatsOf(ContentKind.Text);
        long stored = Section(bytes, SegmentFormat.Section.Text);
        double model = docs.Count * 85.0 + stats.Pairs * (1.37 + 1.8);
        output.WriteLine($"stored text {stored / (double)docs.Count:F0} B a file (brief, zlib-6 over its sample: 4.6 KB); raw {stats.RawTextBytes / (double)docs.Count:F0} B; " +
            $"Brotli ratio {stats.RawTextBytes / (double)Math.Max(1, stats.StoredBytes):F2}");
        output.WriteLine($"{stats.Pairs / (double)docs.Count:F0} distinct terms a file (brief: 413); {seg.TermCount:N0} terms in all");
        output.WriteLine($"index without stored text: {bytes.Length - stored:N0} B; §6 model {model:N0} B ({(bytes.Length - stored) / model - 1:P1})");
        foreach (var s in new[] { SegmentFormat.Section.Postings, SegmentFormat.Section.Dict, SegmentFormat.Section.Docs, SegmentFormat.Section.Paths })
            output.WriteLine($"  {s,-9} {Section(bytes, s) / (double)docs.Count,8:F1} B a file");
    }

    static long Section(byte[] b, SegmentFormat.Section id)
    {
        int tocLength = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(b.Length - 16));
        int toc = b.Length - 16 - tocLength;
        for (int e = toc; e < toc + tocLength; e += SegmentFormat.TocEntrySize)
            if (b[e] == (byte)id) return SegmentFormat.TocEntry.Read(b.AsSpan(e)).Length;
        return 0;
    }

    /// <summary>A read handle that never updates the file's Date Accessed (SetFileTime with -1), as the app's reader opens files.</summary>
    static class NoAccessTimeHandle
    {
        const uint FileReadData = 0x0001, FileReadAttributes = 0x0080, FileWriteAttributes = 0x0100, Synchronize = 0x00100000;
        const uint ShareAll = 0x7, OpenExisting = 3, SequentialScan = 0x08000000;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileTime(Microsoft.Win32.SafeHandles.SafeFileHandle file, IntPtr created, ref long accessed, IntPtr written);

        /// <summary>The handle, or null when the file can't be opened that way (then it isn't read at all).</summary>
        public static Microsoft.Win32.SafeHandles.SafeFileHandle? Open(string path)
        {
            var h = CreateFileW(@"\\?\" + Path.GetFullPath(path), FileReadData | FileReadAttributes | FileWriteAttributes | Synchronize, ShareAll,
                IntPtr.Zero, OpenExisting, SequentialScan, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return null; }
            long keep = -1; // 0xFFFFFFFF'FFFFFFFF: don't update Date Accessed through this handle
            if (!SetFileTime(h, IntPtr.Zero, ref keep, IntPtr.Zero)) { h.Dispose(); return null; }
            return h;
        }
    }
}

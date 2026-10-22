using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Readers;

/// <summary>
/// Manual: an independent oracle for U3 on real files (skipped unless RP_SHELL_ORACLE names folders, separated by ';').
/// Samples candidates per extension (RP_SHELL_ORACLE_COUNT, default 50), copies each through a Date Accessed-keeping
/// handle into a temp folder (the originals are only read, never opened by the shell or a filter), then compares:
/// <list type="bullet">
/// <item>documents (docx, xlsx, pptx, odt, rtf, htm, html, eml, txt): the token recall of our body against the text of
/// Windows' own IFilter for the extension (LoadIFilter), where one is installed;</item>
/// <item>pictures (jpg, jpeg, tif, tiff, png, heic, webp): our fields and details against the shell property store
/// (System.Title, Comment, Keywords, Photo.DateTaken, Photo.CameraManufacturer/Model, Image.HorizontalSize/VerticalSize,
/// Image.HorizontalResolution, GPS.LatitudeDecimal/LongitudeDecimal).</item>
/// </list>
/// Writes counts per extension and field to RP_SHELL_ORACLE_OUT (values only with RP_SHELL_ORACLE_VALUES=1) and asserts
/// U3's agreement criterion: a median recall of at least 95% per document extension, and per picture field at most 5% of
/// the files where Windows has a value disagreeing. Known differences by design: IFilters keep field codes (hyperlink
/// targets), which we skip, and split words at formatting runs ("R ocky") or glue adjacent HTML elements; a date taken
/// that differs by a whole time-zone offset is OffsetTimeOriginal, which we honour and Windows ignores (counted apart).
/// On this PC (2026-10-21, 40 files per extension): docx 97%, xlsx 100%, pptx 100%, rtf 100%, html 98%, txt 100% median
/// recall; picture comment, date taken, camera, size, DPI and GPS all agreeing (no sampled picture had a title; the WIC
/// probe covers titles).
/// </summary>
public class ShellOracleProbe(ITestOutputHelper output)
{
    static readonly string[] DocumentExtensions = ["docx", "xlsx", "pptx", "odt", "rtf", "htm", "html", "eml", "txt"];
    static readonly string[] PictureExtensions = ["jpg", "jpeg", "tif", "tiff", "png", "heic", "webp"];

    [Fact]
    public void Readers_agree_with_windows_ifilters_and_property_store()
    {
        var folders = Environment.GetEnvironmentVariable("RP_SHELL_ORACLE");
        if (string.IsNullOrWhiteSpace(folders)) return;
        int perExtension = int.TryParse(Environment.GetEnvironmentVariable("RP_SHELL_ORACLE_COUNT"), out int n) && n > 0 ? n : 50;
        bool values = Environment.GetEnvironmentVariable("RP_SHELL_ORACLE_VALUES") == "1";
        string reportPath = Environment.GetEnvironmentVariable("RP_SHELL_ORACLE_OUT") ?? Path.Combine(Path.GetTempPath(), "rp-shell-oracle.txt");
        var samples = Sample(folders, perExtension);
        var copies = Directory.CreateTempSubdirectory("rp-oracle-");
        var report = new StringBuilder();
        var failures = new List<string>();
        Exception? error = null;
        // The shell and the filters are COM: one STA thread for the whole probe.
        var thread = new Thread(() =>
        {
            try { Run(samples, copies.FullName, values, report, failures); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        try { copies.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        File.WriteAllText(reportPath, report.ToString());
        output.WriteLine(report.ToString());
        if (error != null) throw error;
        Assert.Empty(failures);
    }

    static Dictionary<string, List<string>> Sample(string folders, int perExtension)
    {
        var random = new Random(1);
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var wanted = DocumentExtensions.Concat(PictureExtensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        foreach (var folder in folders.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            IEnumerable<string> files;
            // Full paths with backslashes: the \\?\ form the file source opens doesn't take '/'.
            try { files = Directory.EnumerateFiles(Path.GetFullPath(folder), "*", options); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                if (file.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) || file.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileName(file);
                string ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
                if (!wanted.Contains(ext) || ContentTypes.IsDeniedFile(name)) continue;
                if (!samples.TryGetValue(ext, out var list)) samples[ext] = list = [];
                int count = seen[ext] = seen.GetValueOrDefault(ext) + 1;
                if (list.Count < perExtension) list.Add(file);
                else if (random.Next(count) < perExtension) list[random.Next(perExtension)] = file; // reservoir sample
            }
        }
        return samples;
    }

    /// <summary>The file's bytes through a handle that keeps Date Accessed (cloud placeholders and files over 64 MB skipped).</summary>
    static byte[]? ReadKeepingAccessTime(string path)
    {
        try
        {
            if (((uint)File.GetAttributes(path) & ReaderNative.CloudBits) != 0) { Skipped("cloud placeholder"); return null; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Skipped("no attributes"); return null; }
        var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
        var refused = FileByteSource.TryOpen(path, budget, out var source, out int error);
        if (refused != null || source == null) { Skipped($"open {refused} {error}"); return null; }
        using (source)
        {
            if (source.Length == 0 || source.Length > ExtractLimits.Local.MaxBytesRead) { Skipped("empty or over 64 MB"); return null; }
            var bytes = new byte[source.Length];
            int read = source.ReadFully(0, bytes);
            if (read != bytes.Length) { Skipped("short read"); return null; }
            return bytes;
        }
    }

    /// <summary>Why files weren't compared (unreadable, no filter, HRESULTs), reported at the end.</summary>
    static readonly Dictionary<string, int> s_skipped = [];

    static void Skipped(string why) => s_skipped[why] = s_skipped.GetValueOrDefault(why) + 1;

    static void Run(Dictionary<string, List<string>> samples, string copies, bool values, StringBuilder report, List<string> failures)
    {
        s_skipped.Clear();
        int copy = 0;
        report.AppendLine("Documents: token recall of our body against the Windows IFilter's text (distinct folded tokens).");
        report.AppendLine("ext\tsampled\tcompared\tnoFilter\tmedian\tp10\tatLeast95%");
        foreach (var ext in DocumentExtensions)
        {
            if (!samples.TryGetValue(ext, out var files)) continue;
            var recalls = new List<double>();
            int noFilter = 0;
            var low = new List<string>();
            foreach (var file in files)
            {
                var bytes = ReadKeepingAccessTime(file);
                if (bytes == null) { Skipped($"{ext}: unreadable or empty"); continue; }
                string target = Path.Combine(copies, $"{copy++}.{ext}");
                File.WriteAllBytes(target, bytes);
                string? theirs = IFilterText(target);
                if (theirs == null) { noFilter++; continue; }
                var expected = Tokens(theirs);
                if (expected.Count < 5) { Skipped($"{ext}: fewer than 5 words from the filter"); continue; } // too little text to measure
                var doc = ReaderRun.Bytes(bytes, ext);
                var ours = Tokens(doc.Body);
                foreach (var (_, v) in doc.Fields) ours.UnionWith(Tokens(v));
                double recall = expected.Count(ours.Contains) / (double)expected.Count;
                recalls.Add(recall);
                if (recall < 0.95)
                {
                    var missing = expected.Where(t => !ours.Contains(t)).Take(8);
                    low.Add($"  {recall:P0} {doc.Status} {expected.Count} tokens {file}" + (values ? " missing: " + string.Join(" ", missing) : ""));
                }
            }
            recalls.Sort();
            double median = recalls.Count == 0 ? 0 : recalls[recalls.Count / 2];
            double p10 = recalls.Count == 0 ? 0 : recalls[recalls.Count / 10];
            report.AppendLine($"{ext}\t{files.Count}\t{recalls.Count}\t{noFilter}\t{median:P1}\t{p10:P1}\t{recalls.Count(r => r >= 0.95)}");
            foreach (var l in low.Take(15)) report.AppendLine(l);
            if (recalls.Count >= 10 && median < 0.95) failures.Add($"{ext}: median recall {median:P1}");
        }

        report.AppendLine();
        report.AppendLine("Pictures: our field against the shell property store, where Windows has a value (extra = we have one and Windows none).");
        report.AppendLine("ext\tfield\twindows\tagree\tdisagree\textra");
        var keys = new Dictionary<string, PropertyKey?>();
        PropertyKey? Key(string name)
        {
            if (!keys.TryGetValue(name, out var key)) keys[name] = key = PSGetPropertyKeyFromName(name, out var k) >= 0 ? k : null;
            return key;
        }
        string[] fields = ["Title", "Comment", "Keywords", "DateTaken", "CameraMake", "CameraModel", "Size", "Dpi", "Gps"];
        foreach (var ext in PictureExtensions)
        {
            if (!samples.TryGetValue(ext, out var files)) continue;
            var tally = fields.ToDictionary(f => f, _ => (Windows: 0, Agree: 0, Disagree: 0, Extra: 0));
            int timeZone = 0;
            var disagreements = new List<string>();
            foreach (var file in files)
            {
                var bytes = ReadKeepingAccessTime(file);
                if (bytes == null) { Skipped($"{ext}: unreadable or empty"); continue; }
                string target = Path.Combine(copies, $"{copy++}.{ext}");
                File.WriteAllBytes(target, bytes);
                var guid = typeof(IPropertyStore).GUID;
                int opened = SHGetPropertyStoreFromParsingName(target, IntPtr.Zero, 0, ref guid, out var store);
                if (opened < 0 || store == null) { Skipped($"{ext}: property store 0x{opened:X8}"); continue; }
                try
                {
                    var doc = ReaderRun.Bytes(bytes, ext);
                    var p = doc.Picture;
                    void Compare(string field, string? windows, string? ours, Func<string, string, bool>? same = null)
                    {
                        var t = tally[field];
                        if (string.IsNullOrWhiteSpace(windows)) { if (!string.IsNullOrWhiteSpace(ours)) t.Extra++; tally[field] = t; return; }
                        t.Windows++;
                        bool agree = ours != null && (same ?? Same)(windows.Trim(), ours.Trim());
                        if (agree) t.Agree++;
                        else
                        {
                            t.Disagree++;
                            disagreements.Add($"  {field}: {file}" + (values ? $" windows=\"{Cut(windows)}\" ours=\"{Cut(ours)}\"" : ""));
                        }
                        tally[field] = t;
                    }
                    Compare("Title", Text(store, Key("System.Title")), doc.Field(DocField.Title));
                    Compare("Comment", Text(store, Key("System.Comment")), doc.Field(DocField.Comment));
                    Compare("Keywords", Text(store, Key("System.Keywords")), doc.Field(DocField.Keywords), (w, o) =>
                    {
                        var mine = o.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        return w.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).All(mine.Contains);
                    });
                    // Within 2 s (Windows adds SubSecTimeOriginal). A difference of a whole time-zone offset is the
                    // OffsetTimeOriginal we honour (spec §5) while Windows reads the local time as this PC's: counted apart.
                    long? taken = FileTime(store, Key("System.Photo.DateTaken"));
                    Compare("DateTaken", taken?.ToString(), p is { DateTakenUtc: > 0 } ? p.Value.DateTakenUtc.ToString() : null, (w, o) =>
                    {
                        long difference = Math.Abs(long.Parse(w) - long.Parse(o));
                        if (difference <= 20_000_000) return true;
                        const long quarterHour = 15L * 60 * 10_000_000;
                        long rest = difference % quarterHour;
                        if (difference > 14 * 4 * quarterHour || (rest > 20_000_000 && quarterHour - rest > 20_000_000)) return false;
                        timeZone++;
                        return true;
                    });
                    Compare("CameraMake", Text(store, Key("System.Photo.CameraManufacturer")), doc.Field(DocField.CameraMake));
                    Compare("CameraModel", Text(store, Key("System.Photo.CameraModel")), doc.Field(DocField.CameraModel));
                    double? width = Number(store, Key("System.Image.HorizontalSize")), height = Number(store, Key("System.Image.VerticalSize"));
                    Compare("Size", width is > 0 && height is > 0 ? $"{width}x{height}" : null, p is { Width: > 0 } ? $"{p.Value.Width}x{p.Value.Height}" : null,
                        (w, o) => w == o || w == string.Join('x', o.Split('x').Reverse()));
                    double? dpi = Number(store, Key("System.Image.HorizontalResolution"));
                    Compare("Dpi", dpi is > 0 ? Math.Round(dpi.Value).ToString(CultureInfo.InvariantCulture) : null, p is { DpiX: > 0 } ? p.Value.DpiX.ToString(CultureInfo.InvariantCulture) : dpi is > 0 ? "0" : null,
                        (w, o) => w == o || (o == "0" && w is "96" or "72")); // Windows shows its default for no resolution
                    double? latitude = Number(store, Key("System.GPS.LatitudeDecimal")), longitude = Number(store, Key("System.GPS.LongitudeDecimal"));
                    Compare("Gps", latitude != null && longitude != null ? string.Create(CultureInfo.InvariantCulture, $"{latitude:F5};{longitude:F5}") : null,
                        p is { HasGps: true } ? string.Create(CultureInfo.InvariantCulture, $"{p.Value.LatitudeMicro / 1e6:F5};{p.Value.LongitudeMicro / 1e6:F5}") : null, (w, o) =>
                        {
                            var a = w.Split(';').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                            var b = o.Split(';').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                            return Math.Abs(a[0] - b[0]) < 1e-4 && Math.Abs(a[1] - b[1]) < 1e-4;
                        });
                }
                finally { Marshal.ReleaseComObject(store); }
            }
            foreach (var field in fields)
            {
                var t = tally[field];
                if (t.Windows + t.Extra == 0) continue;
                report.AppendLine($"{ext}\t{field}\t{t.Windows}\t{t.Agree}\t{t.Disagree}\t{t.Extra}");
                if (t.Windows >= 10 && t.Disagree > t.Windows * 0.05) failures.Add($"{ext} {field}: {t.Disagree} of {t.Windows} disagree");
            }
            if (timeZone > 0) report.AppendLine($"  DateTaken: {timeZone} agree only after a whole time-zone offset (OffsetTimeOriginal, which Windows ignores)");
            foreach (var d in disagreements.Take(30)) report.AppendLine(d);
        }
        report.AppendLine();
        report.AppendLine("Not compared:");
        foreach (var (why, count) in s_skipped.OrderBy(k => k.Key)) report.AppendLine($"  {why}: {count}");
    }

    static bool Same(string windows, string ours) => string.Equals(Collapse(windows), Collapse(ours), StringComparison.OrdinalIgnoreCase);

    static string Collapse(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static string Cut(string? s) => s == null ? "" : s.Length > 60 ? s[..60] + "…" : s;

    /// <summary>Distinct folded tokens of a text, by the index's own rules (field-scoped ones left out).</summary>
    static HashSet<string> Tokens(string text)
    {
        var sink = new TokenSet();
        ContentTokenizer.Tokenize(text, sink);
        return sink.Tokens;
    }

    sealed class TokenSet : ITokenSink
    {
        public HashSet<string> Tokens { get; } = new(StringComparer.Ordinal);
        public void Token(ReadOnlySpan<char> folded, int start, int length, TokenKind kind)
        {
            if (kind is TokenKind.Word or TokenKind.Part or TokenKind.Bigram) Tokens.Add(folded.ToString());
        }
    }

    // ------------------------------------------------------------------ IFilter

    const int FILTER_E_END_OF_CHUNKS = unchecked((int)0x80041700), FILTER_E_NO_MORE_TEXT = unchecked((int)0x80041701);
    const int FILTER_E_EMBEDDING_UNAVAILABLE = unchecked((int)0x80041707), FILTER_E_LINK_UNAVAILABLE = unchecked((int)0x80041708);
    const int FILTER_S_LAST_TEXT = 0x00041709;
    const uint IFILTER_INIT_CANON_PARAGRAPHS = 1, IFILTER_INIT_HARD_LINE_BREAKS = 2, IFILTER_INIT_CANON_SPACES = 8, IFILTER_INIT_APPLY_INDEX_ATTRIBUTES = 16;
    const int CHUNK_TEXT = 1;
    const uint PRSPEC_PROPID = 1, PID_STG_CONTENTS = 19;
    /// <summary>PSGUID_STORAGE: the property set of a file's contents.</summary>
    static readonly Guid StorageProperties = new("B725F130-47EF-101A-A5F1-02608C9EEBAC");

    /// <summary>
    /// The contents text Windows' IFilter for the file gives (at most 4 M characters); null when no filter loads. Asked
    /// for the contents alone first; some filters (plain text, RTF) give nothing then, so they are asked again for every
    /// attribute they index, of which only the contents chunks are kept.
    /// </summary>
    static string? IFilterText(string path)
    {
        var contents = IFilterText(path, allAttributes: false);
        return contents != null && Tokens(contents).Count < 5 ? IFilterText(path, allAttributes: true) ?? contents : contents;
    }

    static string? IFilterText(string path, bool allAttributes)
    {
        int loaded = LoadIFilter(path, IntPtr.Zero, out var unknown);
        if (loaded < 0 || unknown == null) { Skipped($"{Path.GetExtension(path)}: LoadIFilter 0x{loaded:X8}"); return null; }
        var filter = (IFilter)unknown;
        var text = new StringBuilder();
        var buffer = Marshal.AllocHGlobal(8192 * 2);
        try
        {
            uint flags = IFILTER_INIT_CANON_PARAGRAPHS | IFILTER_INIT_HARD_LINE_BREAKS | IFILTER_INIT_CANON_SPACES | (allAttributes ? IFILTER_INIT_APPLY_INDEX_ATTRIBUTES : 0);
            int init = filter.Init(flags, 0, IntPtr.Zero, out _);
            if (init < 0) { Skipped($"{Path.GetExtension(path)}: IFilter.Init 0x{init:X8}"); return null; }
            for (int chunks = 0; chunks < 100_000 && text.Length < 4 << 20; chunks++)
            {
                int hr = filter.GetChunk(out var chunk);
                if (hr == FILTER_E_END_OF_CHUNKS) break;
                if (hr is FILTER_E_EMBEDDING_UNAVAILABLE or FILTER_E_LINK_UNAVAILABLE) continue;
                if (hr < 0) { Skipped($"{Path.GetExtension(path)}: GetChunk 0x{hr:X8}"); break; }
                if ((chunk.Flags & CHUNK_TEXT) == 0) continue;
                if (allAttributes && (chunk.AttributeSet != StorageProperties || chunk.PropertyKind != PRSPEC_PROPID || chunk.Property != PID_STG_CONTENTS)) continue;
                while (text.Length < 4 << 20)
                {
                    uint length = 8192;
                    hr = filter.GetText(ref length, buffer);
                    if (hr == FILTER_E_NO_MORE_TEXT || hr < 0) break;
                    text.Append(Marshal.PtrToStringUni(buffer, (int)Math.Min(length, 8192)));
                    if (hr == FILTER_S_LAST_TEXT) break;
                }
                text.Append('\n');
            }
            return text.ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.ReleaseComObject(filter);
        }
    }

    [DllImport("query.dll", CharSet = CharSet.Unicode)]
    static extern int LoadIFilter(string path, IntPtr outer, [MarshalAs(UnmanagedType.IUnknown)] out object? filter);

    [StructLayout(LayoutKind.Sequential)]
    struct StatChunk
    {
        public uint IdChunk;
        public int BreakType;
        public int Flags;
        public uint Locale;
        public Guid AttributeSet;
        public uint PropertyKind;
        public IntPtr Property;
        public uint IdChunkSource, StartSource, LengthSource;
    }

    [ComImport, Guid("89BCB740-6119-101A-BCB7-00DD010655AF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFilter
    {
        [PreserveSig] int Init(uint flags, uint attributeCount, IntPtr attributes, out uint filterFlags);
        [PreserveSig] int GetChunk(out StatChunk chunk);
        [PreserveSig] int GetText(ref uint length, IntPtr buffer);
        [PreserveSig] int GetValue(out IntPtr value);
        [PreserveSig] int BindRegion();
    }

    // ------------------------------------------------------------------ Property store

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public IntPtr Value1, Value2;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
    static extern int PSGetPropertyKeyFromName(string name, out PropertyKey key);

    [DllImport("propsys.dll")]
    static extern int PropVariantToStringAlloc(ref PropVariant value, out IntPtr text);

    [DllImport("propsys.dll")]
    static extern int PropVariantToDouble(ref PropVariant value, out double number);

    [DllImport("propsys.dll")]
    static extern int PropVariantToFileTime(ref PropVariant value, int flags, out long fileTime);

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant value);

    static T? WithValue<T>(IPropertyStore store, PropertyKey? key, Func<PropVariant, T?> convert) where T : class
    {
        if (key is not { } k || store.GetValue(ref k, out var value) < 0) return null;
        try { return value.Type == 0 ? null : convert(value); }
        finally { PropVariantClear(ref value); }
    }

    static string? Text(IPropertyStore store, PropertyKey? key) => WithValue(store, key, v =>
    {
        if (PropVariantToStringAlloc(ref v, out var text) < 0) return null;
        try { return Marshal.PtrToStringUni(text); }
        finally { Marshal.FreeCoTaskMem(text); }
    });

    static double? Number(IPropertyStore store, PropertyKey? key)
        => WithValue(store, key, v => PropVariantToDouble(ref v, out double d) >= 0 ? (object)d : null) is double n ? n : null;

    static long? FileTime(IPropertyStore store, PropertyKey? key)
        => WithValue(store, key, v => PropVariantToFileTime(ref v, 0, out long t) >= 0 && t > 0 ? (object)t : null) is long n ? n : null;
}

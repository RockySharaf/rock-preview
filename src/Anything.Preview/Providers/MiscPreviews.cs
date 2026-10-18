using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Anything.Preview.Interop;
using Anything.Preview.Util;

namespace Anything.Preview;

// ====================================================================== archives

internal static class ArchivePreview
{
    public static readonly HashSet<string> ZipLike = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".jar", ".war", ".ear", ".apk", ".aab", ".xapk", ".nupkg", ".snupkg", ".vsix", ".epub", ".xpi", ".crx",
        ".appx", ".msix", ".appxbundle", ".msixbundle", ".whl", ".kmz", ".cbz", ".sketch", ".xd", ".ipa", ".aar", ".mpk",
    };
    public static readonly HashSet<string> TarLike = new(StringComparer.OrdinalIgnoreCase) { ".tar", ".tgz", ".gz", ".tar.gz" };
    public static readonly HashSet<string> Other = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".rar", ".bz2", ".xz", ".zst", ".lz", ".lzma", ".cab", ".iso", ".img", ".vhd", ".vhdx", ".dmg", ".wim", ".arj",
    };
    private const int MaxEntries = 5000;

    public static bool IsZip(byte[] head) => head.Length >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] is 3 or 5 && head[3] is 4 or 6;

    public static PreviewResult? RenderZip(string path, CancellationToken ct)
    {
        using var fs = FileUtil.OpenShared(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var rows = new List<string[]>();
        long total = 0, packed = 0;
        int files = 0, dirs = 0, count = 0;
        foreach (var e in zip.Entries)
        {
            if ((++count & 1023) == 0) ct.ThrowIfCancellationRequested();
            bool isDir = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
            if (isDir) dirs++; else { files++; total += e.Length; packed += e.CompressedLength; }
            if (rows.Count < MaxEntries)
                rows.Add(new[]
                {
                    e.FullName, isDir ? "" : FileUtil.FormatShortSize(e.Length), isDir ? "" : FileUtil.FormatShortSize(e.CompressedLength),
                    e.LastWriteTime.Year > 1980 ? e.LastWriteTime.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "",
                });
        }
        var res = new PreviewResult(path, PreviewKind.Table, "Archive")
        {
            FormatName = "ZIP archive",
            Table = new TableData { Columns = new[] { "Name", "Size", "Packed", "Modified" }, Rows = rows, Truncated = count > MaxEntries },
        };
        res.AddMeta("Entries", $"{files:N0} files, {dirs:N0} folders");
        res.AddMeta("Uncompressed", FileUtil.FormatShortSize(total));
        if (total > 0) res.AddMeta("Ratio", $"{100.0 * packed / total:0}%");
        if (!string.IsNullOrEmpty(zip.Comment)) res.AddMeta("Comment", zip.Comment.Length > 200 ? zip.Comment[..200] + "…" : zip.Comment);
        return res;
    }

    public static PreviewResult? RenderTar(string path, CancellationToken ct)
    {
        using var fs = FileUtil.OpenShared(path, 1 << 16, sequential: true);
        var head = new byte[2];
        fs.ReadExactly(head);
        fs.Position = 0;
        Stream s = head[0] == 0x1F && head[1] == 0x8B ? new GZipStream(fs, CompressionMode.Decompress) : fs;
        using var reader = new TarReader(s, leaveOpen: false);
        var rows = new List<string[]>();
        long total = 0;
        int count = 0;
        var sw = Stopwatch.StartNew();
        TarEntry? e;
        bool truncated = false;
        try
        {
            while ((e = reader.GetNextEntry(copyData: false)) != null)
            {
                ct.ThrowIfCancellationRequested();
                count++;
                total += e.Length;
                rows.Add(new[]
                {
                    e.Name, e.EntryType is TarEntryType.Directory ? "" : FileUtil.FormatShortSize(e.Length),
                    e.ModificationTime.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                });
                if (rows.Count >= MaxEntries || sw.ElapsedMilliseconds > 3000) { truncated = true; break; }
            }
        }
        catch (InvalidDataException) when (rows.Count > 0) { truncated = true; }
        if (rows.Count == 0) return null;
        var res = new PreviewResult(path, PreviewKind.Table, "Archive")
        {
            FormatName = s is GZipStream ? "gzip-compressed tar archive" : "tar archive",
            Table = new TableData { Columns = new[] { "Name", "Size", "Modified" }, Rows = rows, Truncated = truncated },
        };
        res.AddMeta("Entries", count.ToString("N0", CultureInfo.InvariantCulture) + (truncated ? "+" : ""));
        res.AddMeta("Uncompressed", FileUtil.FormatShortSize(total) + (truncated ? "+" : ""));
        return res;
    }
}

// ====================================================================== fonts

internal static class FontPreview
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf", ".ttc", ".otc", ".woff", ".fon", ".fnt" };

    private const string Pangram = "The quick brown fox jumps over the lazy dog";
    private static readonly int[] Sizes = { 12, 16, 20, 28, 36, 48, 64 };

    public static async Task<PreviewResult?> RenderAsync(string path, Size target, CancellationToken ct)
    {
        byte[]? data = FileUtil.ReadAllCapped(path, 64L * 1024 * 1024);
        if (data == null) return null;
        string kind = "Font";
        if (Bytes.StartsWithAscii(data, "wOFF"))
        {
            data = WoffToSfnt(data);
            kind = "WOFF font";
            if (data == null) return null;
        }
        int width = Math.Clamp(target.Width, 400, 1600);
        var bmp = RenderGdi(data, width, out string? family, out string? styles);
        string provider = "Font (GDI+)";
        if (bmp == null)
        {
            // CFF-flavoured OpenType: GDI+ can't load it; WPF can (needs an STA thread).
            string p = path;
            var r = await StaPool.Shared.RunAsync(() => RenderWpf(p, width), ct, true, 15000).ConfigureAwait(false);
            if (r?.Bitmap == null) return null;
            bmp = r.Bitmap;
            family = r.Family;
            provider = "Font (WPF)";
        }
        var res = new PreviewResult(path, PreviewKind.Image, provider) { FormatName = kind == "Font" ? FontKind(data) : kind, Image = bmp };
        res.AddMeta("Family", family);
        res.AddMeta("Styles", styles);
        return res;
    }

    private static string FontKind(byte[] d) =>
        Bytes.StartsWithAscii(d, "OTTO") ? "OpenType font (CFF)" : Bytes.StartsWithAscii(d, "ttcf") ? "TrueType collection" : "TrueType font";

    private static Bitmap? RenderGdi(byte[] data, int width, out string? family, out string? styles)
    {
        family = null;
        styles = null;
        IntPtr mem = Marshal.AllocCoTaskMem(data.Length);
        try
        {
            Marshal.Copy(data, 0, mem, data.Length);
            using var pfc = new PrivateFontCollection();
            pfc.AddMemoryFont(mem, data.Length);
            if (pfc.Families.Length == 0) return null;
            var fam = pfc.Families[0];
            family = string.Join(", ", pfc.Families.Select(f => f.Name));
            var avail = new[] { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic }.Where(fam.IsStyleAvailable).ToList();
            if (avail.Count == 0) return null;
            styles = string.Join(", ", avail.Select(s => s.ToString()));
            var style = avail[0];
            return Compose(width, fam.Name, (g, text, px, y) =>
            {
                using var f = new Font(fam, px, style, GraphicsUnit.Pixel);
                g.DrawString(text, f, Brushes.Black, new RectangleF(16, y, width - 32, px * 3), StringFormat.GenericTypographic);
                return g.MeasureString(text, f, width - 32, StringFormat.GenericTypographic).Height;
            });
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(mem);
        }
    }

    private static Bitmap Compose(int width, string title, Func<Graphics, string, float, float, float> draw)
    {
        int height = 120 + Sizes.Sum(s => (int)(s * 1.6f) + 10) + 140;
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var tf = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel))
            g.DrawString(title, tf, Brushes.DimGray, 16, 12);
        float y = 52;
        y += draw(g, "ABCDEFGHIJKLMNOPQRSTUVWXYZ\nabcdefghijklmnopqrstuvwxyz\n0123456789 .,:;!?&@#$%*()[]{}", 22, y) + 18;
        using var small = new Font("Segoe UI", 11, GraphicsUnit.Pixel);
        using var sep = new Pen(Color.Gainsboro);
        foreach (int s in Sizes)
        {
            g.DrawLine(sep, 16, y, width - 16, y);
            g.DrawString(s + " px", small, Brushes.Gray, 16, y + 2);
            y += 16;
            y += Math.Min(draw(g, Pangram, s, y), s * 3) + 8;
        }
        int h = Math.Min(bmp.Height, (int)y + 16);
        if (h >= bmp.Height) return bmp;
        var crop = bmp.Clone(new Rectangle(0, 0, width, h), PixelFormat.Format32bppPArgb);
        bmp.Dispose();
        return crop;
    }

    private sealed record WpfFontResult(Bitmap? Bitmap, string? Family);

    private static WpfFontResult RenderWpf(string path, int width)
    {
        try
        {
            var gt = new System.Windows.Media.GlyphTypeface(new Uri(path));
            string fam = gt.FamilyNames.Values.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(path);
            var bmp = Compose(width, fam, (g, text, px, y) =>
            {
                // Render each line with WPF glyph runs into a bitmap, then blit.
                float total = 0;
                foreach (var line in text.Split('\n'))
                {
                    using var lineBmp = RenderGlyphLine(gt, line, px, width - 32);
                    if (lineBmp != null) g.DrawImageUnscaled(lineBmp, 16, (int)(y + total));
                    total += px * 1.35f;
                }
                return total;
            });
            return new WpfFontResult(bmp, fam);
        }
        catch
        {
            return new WpfFontResult(null, null);
        }
    }

    private static Bitmap? RenderGlyphLine(System.Windows.Media.GlyphTypeface gt, string text, float px, int maxWidth)
    {
        var glyphs = new List<ushort>();
        var adv = new List<double>();
        double x = 0;
        foreach (char ch in text)
        {
            if (!gt.CharacterToGlyphMap.TryGetValue(ch, out ushort gi)) continue;
            double a = gt.AdvanceWidths[gi] * px;
            if (x + a > maxWidth) break;
            glyphs.Add(gi);
            adv.Add(a);
            x += a;
        }
        if (glyphs.Count == 0) return null;
        int h = (int)Math.Ceiling(px * 1.35);
        var run = new System.Windows.Media.GlyphRun(gt, 0, false, px, 1.0f, glyphs, new System.Windows.Point(0, px * gt.Baseline), adv,
            null, null, null, null, null, null);
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawGlyphRun(System.Windows.Media.Brushes.Black, run);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(x)), h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);
        return BitmapUtil.ToGdi(rtb);
    }

    /// <summary>WOFF 1.0 → plain sfnt (zlib-compressed tables).</summary>
    internal static byte[]? WoffToSfnt(byte[] w)
    {
        try
        {
            uint flavor = Bytes.U32BE(w, 4);
            int numTables = Bytes.U16BE(w, 12);
            var tables = new List<(uint Tag, uint Checksum, byte[] Data)>();
            for (int i = 0; i < numTables; i++)
            {
                int o = 44 + i * 20;
                uint tag = Bytes.U32BE(w, o), off = Bytes.U32BE(w, o + 4), comp = Bytes.U32BE(w, o + 8), orig = Bytes.U32BE(w, o + 12), ck = Bytes.U32BE(w, o + 16);
                byte[] data;
                if (comp < orig)
                {
                    using var zs = new ZLibStream(new MemoryStream(w, (int)off, (int)comp), CompressionMode.Decompress);
                    data = new byte[orig];
                    zs.ReadExactly(data);
                }
                else data = w.AsSpan((int)off, (int)orig).ToArray();
                tables.Add((tag, ck, data));
            }
            int entrySelector = (int)Math.Floor(Math.Log2(Math.Max(1, numTables)));
            int searchRange = (1 << entrySelector) * 16;
            using var ms = new MemoryStream();
            void U32(uint v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            U32(flavor); U16(numTables); U16(searchRange); U16(entrySelector); U16(numTables * 16 - searchRange);
            uint offset = (uint)(12 + 16 * numTables);
            foreach (var t in tables)
            {
                U32(t.Tag); U32(t.Checksum); U32(offset); U32((uint)t.Data.Length);
                offset += (uint)((t.Data.Length + 3) & ~3);
            }
            foreach (var t in tables)
            {
                ms.Write(t.Data);
                for (int p = t.Data.Length; (p & 3) != 0; p++) ms.WriteByte(0);
            }
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }
}

// ====================================================================== folders

internal static class FolderPreview
{
    public static async Task<PreviewResult> RenderAsync(string path, Size target, CancellationToken ct)
    {
        var di = new DirectoryInfo(path);
        var res = new PreviewResult(path, PreviewKind.Info, "Folder") { FormatName = Shell.GetTypeName(path, true) };
        var thumbTask = Shell.GetThumbnailAsync(path, 256, ct);
        int files = 0, dirs = 0;
        long size = 0;
        bool partial = false;
        var rows = new List<(bool Dir, string[] Row)>();
        var sw = Stopwatch.StartNew();
        await Task.Run(() =>
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
            foreach (var fsi in di.EnumerateFileSystemInfos("*", opts))
            {
                if (((files + dirs) & 511) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (sw.ElapsedMilliseconds > 2000) { partial = true; break; }
                }
                bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
                long len = 0;
                if (isDir) dirs++;
                else { files++; try { len = ((FileInfo)fsi).Length; size += len; } catch { } }
                if (rows.Count < 1000)
                    rows.Add((isDir, new[]
                    {
                        fsi.Name, isDir ? "" : FileUtil.FormatShortSize(len),
                        fsi.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    }));
            }
        }, ct).ConfigureAwait(false);
        res.Image = await thumbTask.ConfigureAwait(false) ?? await Shell.GetIconAsync(path, 256, ct).ConfigureAwait(false);
        res.HasAlpha = true;
        res.AddMeta("Contains", $"{files:N0} files, {dirs:N0} folders" + (partial ? " (counting stopped)" : ""));
        res.AddMeta("Size of files", FileUtil.FormatSize(size) + " (not recursive)");
        try
        {
            res.AddMeta("Created", di.CreationTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
            res.AddMeta("Modified", di.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
            res.AddMeta("Attributes", di.Attributes.ToString());
        }
        catch { }
        res.Table = new TableData
        {
            Columns = new[] { "Name", "Size", "Modified" },
            Rows = rows.OrderByDescending(r => r.Dir).ThenBy(r => r.Row[0], StringComparer.CurrentCultureIgnoreCase).Select(r => r.Row).ToList(),
            Truncated = files + dirs > rows.Count,
        };
        return res;
    }
}

// ====================================================================== media

internal static class MediaPreview
{
    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".qt", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".m2v", ".ts", ".m2ts", ".mts",
        ".3gp", ".3g2", ".flv", ".asf", ".vob", ".ogv", ".divx", ".dv",
    };
    public static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".wma", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".oga", ".opus", ".aif", ".aiff", ".aifc", ".mid",
        ".midi", ".ac3", ".amr", ".ape", ".mka", ".wv", ".au", ".snd", ".caf",
    };

    public static async Task<PreviewResult> RenderAsync(string path, string ext, Size target, CancellationToken ct)
    {
        bool video = Video.Contains(ext);
        var props = await Shell.ReadPropertiesAsync(path, ct,
            ("Duration", Shell.PKEY_Media_Duration), ("Width", Shell.PKEY_Video_FrameWidth), ("Height", Shell.PKEY_Video_FrameHeight),
            ("FrameRate", Shell.PKEY_Video_FrameRate), ("Bitrate", Shell.PKEY_Audio_EncodingBitrate), ("VideoBitrate", Shell.PKEY_Video_TotalBitrate),
            ("SampleRate", Shell.PKEY_Audio_SampleRate), ("Channels", Shell.PKEY_Audio_ChannelCount), ("Title", Shell.PKEY_Title),
            ("Artist", Shell.PKEY_Music_Artist), ("Album", Shell.PKEY_Music_AlbumTitle), ("Year", Shell.PKEY_Media_Year),
            ("Genre", Shell.PKEY_Music_Genre)).ConfigureAwait(false) ?? new();
        var thumb = await Shell.GetThumbnailAsync(path, Math.Clamp(Math.Max(target.Width, target.Height), 256, 1024), ct).ConfigureAwait(false);
        TimeSpan? dur = props.TryGetValue("Duration", out var d) && d.Number is ulong ticks and > 0 ? TimeSpan.FromTicks((long)ticks) : null;
        Size? frame = props.TryGetValue("Width", out var w) && w.Number is > 0 && props.TryGetValue("Height", out var h) && h.Number is > 0
            ? new Size((int)w.Number!.Value, (int)h.Number!.Value) : null;
        var res = new PreviewResult(path, PreviewKind.Media, "Media")
        {
            FormatName = Shell.GetTypeName(path, false),
            Image = thumb,
            Media = new MediaInfo { IsVideo = video, Duration = dur, FrameSize = frame },
        };
        if (dur != null) res.AddMeta("Duration", FileUtil.FormatDuration(dur.Value));
        if (frame != null) res.AddMeta("Resolution", $"{frame.Value.Width} × {frame.Value.Height}");
        if (props.TryGetValue("FrameRate", out var fr) && fr.Number is > 0) res.AddMeta("Frame rate", $"{fr.Number.Value / 1000.0:0.##} fps");
        foreach (var (key, label) in new[] { ("Title", "Title"), ("Artist", "Artist"), ("Album", "Album"), ("Year", "Year"), ("Genre", "Genre"),
                                             ("Bitrate", "Audio bitrate"), ("VideoBitrate", "Total bitrate"), ("SampleRate", "Sample rate"), ("Channels", "Channels") })
            if (props.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v.Display)) res.AddMeta(label, v.Display);
        return res;
    }
}

// ====================================================================== info card

internal static class InfoCard
{
    public static async Task<PreviewResult> RenderAsync(string path, CancellationToken ct, string provider = "Info", byte[]? head = null)
    {
        var fi = new FileInfo(path);
        var res = new PreviewResult(path, PreviewKind.Info, provider) { FormatName = Shell.GetTypeName(path, false) };
        res.Image = await Shell.GetIconAsync(path, 256, ct).ConfigureAwait(false);
        res.HasAlpha = true;
        AddFileFacts(res, fi);
        string ext = fi.Extension.ToLowerInvariant();
        if (ext is ".exe" or ".dll" or ".sys" or ".ocx" or ".cpl" or ".scr" or ".msi" or ".mui")
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                res.AddMeta("Description", vi.FileDescription);
                res.AddMeta("Product", vi.ProductName);
                res.AddMeta("Company", vi.CompanyName);
                res.AddMeta("File version", vi.FileVersion);
                res.AddMeta("Copyright", vi.LegalCopyright);
            }
            catch { }
        }
        try
        {
            head ??= FileUtil.ReadHead(path, 4096);
            if (head.Length > 0) res.HexDump = FileUtil.HexDump(head, 4096);
        }
        catch (Exception ex)
        {
            res.AddMeta("Read error", ex.Message);
        }
        return res;
    }

    public static void AddFileFacts(PreviewResult res, FileInfo fi)
    {
        try
        {
            res.AddMeta("Size", FileUtil.FormatSize(fi.Length));
            res.AddMeta("Created", fi.CreationTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
            res.AddMeta("Modified", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
            res.AddMeta("Accessed", fi.LastAccessTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
            res.AddMeta("Attributes", fi.Attributes.ToString());
        }
        catch { }
    }
}

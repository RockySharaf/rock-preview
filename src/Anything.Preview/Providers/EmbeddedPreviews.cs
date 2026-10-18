using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Anything.Preview.Util;

namespace Anything.Preview;

/// <summary>Extractors for previews embedded inside design/CAD files (Adobe XMP, EPS, CorelDRAW, DWG).</summary>
internal static class EmbeddedPreviews
{
    // ------------------------------------------------------------------ XMP thumbnail (InDesign, Illustrator, …)

    private static readonly byte[] XmpGImg = Encoding.ASCII.GetBytes("xmpGImg:image");

    /// <summary>
    /// Finds the largest decodable xmpGImg:image (base64 JPEG) in the file. Scans the head and the tail (InDesign
    /// keeps its XMP packet near the end of big files) without reading whole multi-GB files.
    /// </summary>
    public static byte[]? FindXmpThumbnail(string path)
    {
        var fi = new FileInfo(path);
        const int chunk = 24 * 1024 * 1024;
        var ranges = new List<(long, int)>();
        if (fi.Length <= chunk * 2L) ranges.Add((0, (int)fi.Length));
        else { ranges.Add((0, chunk)); ranges.Add((fi.Length - chunk, chunk)); }
        byte[]? best = null;
        foreach (var (off, len) in ranges)
        {
            var data = FileUtil.ReadRange(path, off, len);
            int pos = 0;
            while ((pos = Bytes.IndexOf(data, XmpGImg, pos)) >= 0)
            {
                var img = DecodeXmpImageAt(data, pos + XmpGImg.Length);
                if (img != null && (best == null || img.Length > best.Length)) best = img;
                pos += XmpGImg.Length;
            }
            if (best != null) break;
        }
        return best;
    }

    private static byte[]? DecodeXmpImageAt(byte[] data, int p)
    {
        // Attribute form: xmpGImg:image="..."  Element form: <xmpGImg:image>...</xmpGImg:image>
        if (p >= data.Length) return null;
        int start;
        byte endChar;
        if (data[p] == (byte)'=' && p + 1 < data.Length && (data[p + 1] == '"' || data[p + 1] == '\''))
        {
            endChar = data[p + 1];
            start = p + 2;
        }
        else if (data[p] == (byte)'>')
        {
            endChar = (byte)'<';
            start = p + 1;
        }
        else return null;
        int end = Array.IndexOf(data, endChar, start);
        if (end < 0 || end - start < 100) return null;
        var sb = new StringBuilder(end - start);
        for (int i = start; i < end; i++)
        {
            char c = (char)data[i];
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=') sb.Append(c);
            else if (c == '&') // "&#xA;" entity used for line breaks
            {
                int semi = Array.IndexOf(data, (byte)';', i);
                if (semi > i && semi - i < 8) i = semi;
            }
        }
        while (sb.Length % 4 != 0) sb.Append('=');
        try
        {
            var bytes = Convert.FromBase64String(sb.ToString());
            return bytes.Length > 64 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ EPS (DOS binary header)

    /// <summary>DOS EPS header C5D0D3C6: embedded TIFF preview, else WMF preview.</summary>
    public static Bitmap? FromEpsBinaryHeader(string path, Size box, out string? kind)
    {
        kind = null;
        var head = FileUtil.ReadHead(path, 32);
        if (head.Length < 30 || head[0] != 0xC5 || head[1] != 0xD0 || head[2] != 0xD3 || head[3] != 0xC6) return null;
        uint wmfOff = Bytes.U32LE(head, 12), wmfLen = Bytes.U32LE(head, 16);
        uint tifOff = Bytes.U32LE(head, 20), tifLen = Bytes.U32LE(head, 24);
        long size = new FileInfo(path).Length;
        if (tifOff > 0 && tifLen > 0 && tifOff + (long)tifLen <= size && tifLen < 256 << 20)
        {
            var tif = FileUtil.ReadRange(path, tifOff, (int)tifLen);
            var bmp = BitmapUtil.Decode(tif, box);
            if (bmp != null) { kind = "EPS TIFF preview"; return bmp; }
        }
        if (wmfOff > 0 && wmfLen > 0 && wmfOff + (long)wmfLen <= size && wmfLen < 256 << 20)
        {
            var wmf = FileUtil.ReadRange(path, wmfOff, (int)wmfLen);
            var bmp = MetafileRenderer.RenderBytes(wmf, box, out _, out _);
            if (bmp != null) { kind = "EPS WMF preview"; return bmp; }
        }
        return null;
    }

    /// <summary>Offset of the PostScript section inside a DOS EPS (0 for plain EPS).</summary>
    public static (long Offset, long Length) EpsPostScriptRange(string path)
    {
        var head = FileUtil.ReadHead(path, 32);
        if (head.Length >= 12 && head[0] == 0xC5 && head[1] == 0xD0 && head[2] == 0xD3 && head[3] == 0xC6)
            return (Bytes.U32LE(head, 4), Bytes.U32LE(head, 8));
        return (0, new FileInfo(path).Length);
    }

    /// <summary>Last-resort scan for an embedded JPEG/PNG (largest one wins).</summary>
    public static byte[]? FindLargestEmbeddedImage(byte[] data)
    {
        byte[]? best = null;
        // JPEG: SOI FF D8 FF … EOI FF D9
        int pos = 0;
        int guard = 0;
        while ((pos = Bytes.IndexOf(data, new byte[] { 0xFF, 0xD8, 0xFF }, pos)) >= 0 && guard++ < 64)
        {
            int end = Bytes.IndexOf(data, new byte[] { 0xFF, 0xD9 }, pos + 3);
            // Nested EXIF thumbnails end early; extend to the last EOI before the next SOI.
            if (end > 0)
            {
                int next = Bytes.IndexOf(data, new byte[] { 0xFF, 0xD8, 0xFF }, pos + 3);
                while (next > 0 && next < end)
                {
                    int e2 = Bytes.IndexOf(data, new byte[] { 0xFF, 0xD9 }, end + 2);
                    if (e2 < 0) break;
                    end = e2;
                    next = Bytes.IndexOf(data, new byte[] { 0xFF, 0xD8, 0xFF }, next + 3);
                    if (next < 0 || next > end) break;
                }
                int len = end + 2 - pos;
                if (len > 1024 && (best == null || len > best.Length)) best = data.AsSpan(pos, len).ToArray();
                pos = end + 2;
            }
            else break;
        }
        pos = 0;
        guard = 0;
        var pngSig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var iend = Encoding.ASCII.GetBytes("IEND");
        while ((pos = Bytes.IndexOf(data, pngSig, pos)) >= 0 && guard++ < 64)
        {
            int end = Bytes.IndexOf(data, iend, pos + 8);
            if (end < 0) break;
            int len = end + 8 - pos;
            if (len > 512 && pos + len <= data.Length && (best == null || len > best.Length)) best = data.AsSpan(pos, len).ToArray();
            pos = end;
        }
        return best;
    }

    // ------------------------------------------------------------------ CorelDRAW

    /// <summary>CDR X4+ (ZIP: previews/thumbnail.png …) or legacy RIFF CDR (DISP chunk DIB).</summary>
    public static Bitmap? FromCorel(string path, Size box, out string? kind, out string? version)
    {
        kind = null;
        version = null;
        var head = FileUtil.ReadHead(path, 16);
        if (head.Length >= 4 && head[0] == 'P' && head[1] == 'K')
        {
            using var fs = FileUtil.OpenShared(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            version = "CorelDRAW X4 or later (ZIP container)";
            var candidates = zip.Entries
                .Where(e => e.Length > 0 && (e.FullName.StartsWith("previews/", StringComparison.OrdinalIgnoreCase) ||
                                             e.FullName.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)) &&
                            (e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                             e.Name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
                             e.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(e => e.FullName.StartsWith("previews/", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(e => e.Length)
                .ToList();
            foreach (var e in candidates)
            {
                using var es = e.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                var bmp = BitmapUtil.Decode(ms.ToArray(), box);
                if (bmp != null) { kind = e.FullName; return bmp; }
            }
            return null;
        }
        if (head.Length >= 12 && Bytes.StartsWithAscii(head, "RIFF"))
        {
            string form = Encoding.ASCII.GetString(head, 8, 4);
            if (form.StartsWith("CDR", StringComparison.Ordinal) || form.StartsWith("cdr", StringComparison.Ordinal))
                version = "CorelDRAW (RIFF, version code '" + form[3] + "')";
            else if (form.StartsWith("CMX", StringComparison.Ordinal)) version = "Corel Presentation Exchange (CMX)";
            var data = FileUtil.ReadHead(path, 8 * 1024 * 1024);
            // Walk top-level chunks; fall back to a raw search for "DISP".
            int p = 12;
            int disp = -1, dispLen = 0;
            while (p + 8 <= data.Length)
            {
                string id = Encoding.ASCII.GetString(data, p, 4);
                int len = (int)Bytes.U32LE(data, p + 4);
                if (len < 0) break;
                if (id == "DISP") { disp = p + 8; dispLen = len; break; }
                p += 8 + len + (len & 1);
            }
            if (disp < 0)
            {
                int i = Bytes.IndexOf(data, Encoding.ASCII.GetBytes("DISP"));
                if (i >= 0 && i + 8 < data.Length) { disp = i + 8; dispLen = (int)Bytes.U32LE(data, i + 4); }
            }
            if (disp >= 0)
            {
                dispLen = Math.Min(dispLen, data.Length - disp);
                // libcdr: 4 bytes of something, then a headerless DIB. Be tolerant and probe both.
                foreach (int skip in new[] { 4, 0, 8 })
                {
                    if (dispLen - skip < 40) continue;
                    var dib = data.AsSpan(disp + skip, dispLen - skip);
                    if (Bytes.U32LE(dib, 0) != 40) continue;
                    var bmpBytes = BitmapUtil.DibToBmp(dib);
                    if (bmpBytes == null) continue;
                    var bmp = BitmapUtil.Decode(bmpBytes, box);
                    if (bmp != null) { kind = "DISP chunk"; return bmp; }
                }
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ Blender

    /// <summary>The "TEST" block thumbnail Blender saves into .blend files (legacy and Blender 5 headers; gzip ok).</summary>
    public static Bitmap? FromBlend(string path, Size box)
    {
        var raw = FileUtil.ReadHead(path, 8 * 1024 * 1024);
        if (raw.Length > 2 && raw[0] == 0x1F && raw[1] == 0x8B)
        {
            using var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
            raw = FileUtil.ReadUpTo(gz, 8 * 1024 * 1024);
        }
        if (!Bytes.StartsWithAscii(raw, "BLENDER")) return null;
        int p;
        bool large;
        int ptr;
        if (raw[7] is (byte)'_' or (byte)'-') { ptr = raw[7] == '_' ? 4 : 8; large = false; p = 12; if (raw[8] != 'v') return null; }
        else
        {
            // Blender 5: "BLENDER" + header size (2 digits) + "-" + format version (2 digits) + endian + version
            if (!int.TryParse(Encoding.ASCII.GetString(raw, 7, 2), out int hs)) return null;
            ptr = 8; large = true; p = hs;
        }
        while (p + 24 < raw.Length)
        {
            string code = Encoding.ASCII.GetString(raw, p, 4);
            long len;
            int hdr;
            if (large) { len = (long)BitConverter.ToUInt64(raw, p + 16); hdr = 32; }
            else { len = Bytes.U32LE(raw, p + 4); hdr = 4 + 4 + ptr + 4 + 4; }
            if (code == "TEST")
            {
                int d = p + hdr;
                int w = (int)Bytes.U32LE(raw, d), h = (int)Bytes.U32LE(raw, d + 4);
                if (w <= 0 || h <= 0 || w > 2048 || h > 2048 || d + 8 + (long)w * h * 4 > raw.Length) return null;
                var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int o = d + 8 + ((h - 1 - y) * w + x) * 4;
                        bmp.SetPixel(x, y, Color.FromArgb(raw[o + 3], raw[o], raw[o + 1], raw[o + 2]));
                    }
                return BitmapUtil.FitOrSelf(bmp, box);
            }
            if (code is "ENDB" or "DNA1" || len < 0) break;
            p += hdr + (int)len;
        }
        return null;
    }

    // ------------------------------------------------------------------ AutoCAD DWG

    private static readonly byte[] DwgImageSentinel =
        { 0x1F, 0x25, 0x6D, 0x07, 0xD4, 0x36, 0x28, 0x28, 0x9D, 0x57, 0xCA, 0x3F, 0x9D, 0x44, 0x10, 0x2B };

    public static string? DwgVersionName(string code) => code switch
    {
        "AC1012" => "AutoCAD R13", "AC1014" => "AutoCAD R14", "AC1015" => "AutoCAD 2000", "AC1018" => "AutoCAD 2004",
        "AC1021" => "AutoCAD 2007", "AC1024" => "AutoCAD 2010", "AC1027" => "AutoCAD 2013", "AC1032" => "AutoCAD 2018",
        "AC1009" => "AutoCAD R11/R12", "AC1006" => "AutoCAD R10", _ => null,
    };

    /// <summary>
    /// The thumbnail AutoCAD stores in the file: header pointer at 0x0D → image-data sentinel, then a directory of
    /// (code, start, size) entries: 2 = BMP (headerless DIB), 3 = WMF, 6 = PNG.
    /// </summary>
    public static Bitmap? FromDwg(string path, Size box, out string? kind, out string? version)
    {
        kind = null;
        var head = FileUtil.ReadHead(path, 0x20);
        version = head.Length >= 6 ? Encoding.ASCII.GetString(head, 0, 6) : null;
        if (head.Length < 0x15 || version == null || !version.StartsWith("AC", StringComparison.Ordinal)) return null;
        long fileLen = new FileInfo(path).Length;
        var tried = new List<long>();
        long seeker = Bytes.U32LE(head, 0x0D);
        if (seeker > 0 && seeker < fileLen) tried.Add(seeker);
        foreach (var pos in tried)
        {
            var bmp = ReadDwgImageAt(path, pos, fileLen, box, out kind);
            if (bmp != null) return bmp;
        }
        // Sentinel search over the first 16 MB.
        var data = FileUtil.ReadHead(path, 16 * 1024 * 1024);
        int at = 0;
        while ((at = Bytes.IndexOf(data, DwgImageSentinel, at)) >= 0)
        {
            var bmp = ReadDwgImageAt(path, at, fileLen, box, out kind);
            if (bmp != null) return bmp;
            at += 16;
        }
        return null;
    }

    private static Bitmap? ReadDwgImageAt(string path, long pos, long fileLen, Size box, out string? kind)
    {
        kind = null;
        var hdr = FileUtil.ReadRange(path, pos, 16 + 4 + 1 + 9 * 8);
        if (hdr.Length < 21 || !hdr.AsSpan(0, 16).SequenceEqual(DwgImageSentinel)) return null;
        int count = hdr[20];
        if (count is <= 0 or > 8) return null;
        // Prefer PNG, then BMP, then WMF.
        var entries = new List<(int Code, long Start, int Size)>();
        for (int i = 0; i < count; i++)
        {
            int o = 21 + i * 9;
            if (o + 9 > hdr.Length) break;
            entries.Add((hdr[o], Bytes.U32LE(hdr, o + 1), (int)Bytes.U32LE(hdr, o + 5)));
        }
        foreach (var code in new[] { 6, 2, 3 })
        {
            foreach (var e in entries.Where(e => e.Code == code))
            {
                if (e.Size <= 0 || e.Start <= 0 || e.Start + e.Size > fileLen || e.Size > 64 << 20) continue;
                var blob = FileUtil.ReadRange(path, e.Start, e.Size);
                Bitmap? bmp = code switch
                {
                    2 => BitmapUtil.DibToBmp(blob) is { } b ? BitmapUtil.Decode(b, box) : null,
                    3 => MetafileRenderer.RenderBytes(blob, box, out _, out _),
                    _ => BitmapUtil.Decode(blob, box),
                };
                if (bmp != null)
                {
                    kind = code switch { 2 => "embedded BMP preview", 3 => "embedded WMF preview", _ => "embedded PNG preview" };
                    return bmp;
                }
            }
        }
        return null;
    }
}

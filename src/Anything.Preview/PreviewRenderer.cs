using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anything.Preview.Interop;
using Anything.Preview.Util;

namespace Anything.Preview;

/// <summary>
/// Headless preview pipeline: picks an ordered provider chain for the file (by extension and content sniffing) and
/// returns the first result that succeeds. Failures are recorded in <see cref="PreviewResult.Diagnostics"/> and the
/// next provider is tried; the final fallback is always an info card, so this never throws (except on cancellation).
/// Everything runs off the UI thread; shell COM calls are marshalled to an STA pool.
/// </summary>
public static class PreviewRenderer
{
    private sealed record Step(string Name, Func<Task<PreviewResult?>> Run);

    public static Task<PreviewResult> RenderAsync(string path, Size targetSize, CancellationToken ct) =>
        RenderAsync(path, targetSize, new PreviewOptions { AllowHostedHandler = false }, ct);

    public static async Task<PreviewResult> RenderAsync(string path, Size targetSize, PreviewOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Never trust a zero/hidden pane size: decode for a sensible default; the view refits on every resize anyway.
        if (targetSize.Width < 64 || targetSize.Height < 64)
            targetSize = new Size(Math.Max(targetSize.Width, 1024), Math.Max(targetSize.Height, 768));

        if (Directory.Exists(path))
        {
            try { return await FolderPreview.RenderAsync(path, targetSize, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return Error(path, "Folder", ex); }
        }
        if (!File.Exists(path))
        {
            var nf = new PreviewResult(path, PreviewKind.Info, "Info") { FormatName = "Not found" };
            nf.AddMeta("Error", "The file does not exist (it may have been moved or deleted).");
            return nf;
        }

        string ext = GetExtension(path);
        byte[] head;
        try
        {
            head = FileUtil.ReadHead(path, 64 * 1024);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var res = await InfoCard.RenderAsync(path, ct, "Info", Array.Empty<byte>()).ConfigureAwait(false);
            res.AddMeta("Error", ex.Message);
            return res;
        }

        var diag = new List<string>();
        foreach (var step in BuildChain(path, ext, head, targetSize, options, ct))
        {
            ct.ThrowIfCancellationRequested();
            PreviewResult? r = null;
            try
            {
                r = await step.Run().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                diag.Add($"{step.Name}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            if (r == null) continue;
            if (!r.HasContent) { r.Dispose(); continue; }
            Finish(r, path);
            r.Diagnostics.AddRange(diag);
            return r;
        }
        var info = await InfoCard.RenderAsync(path, ct, "Info", head.Length > 4096 ? head[..4096] : head).ConfigureAwait(false);
        info.Diagnostics.AddRange(diag);
        return info;
    }

    private static PreviewResult Error(string path, string provider, Exception ex)
    {
        var r = new PreviewResult(path, PreviewKind.Info, provider) { FormatName = "Error" };
        r.AddMeta("Error", ex.Message);
        return r;
    }

    internal static string GetExtension(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".tar.gz", StringComparison.Ordinal)) return ".tar.gz";
        return Path.GetExtension(name);
    }

    private static void Finish(PreviewResult r, string path)
    {
        try
        {
            r.FormatName ??= Shell.GetTypeName(path, false);
            if (r.Kind != PreviewKind.Info && r.GetMeta("Size") == null)
            {
                var fi = new FileInfo(path);
                r.AddMeta("Size", FileUtil.FormatShortSize(fi.Length));
                r.AddMeta("Modified", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ chain construction

    private static readonly HashSet<string> OleExt = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".dot", ".xls", ".xlt", ".ppt", ".pps", ".pot", ".msg", ".vsd", ".vss", ".vst", ".pub", ".mpp", ".oft" };
    private static readonly HashSet<string> OtherHandlerExt = new(StringComparer.OrdinalIgnoreCase)
        { ".vsdx", ".vsdm", ".vssx", ".vstx", ".one", ".onepkg", ".xps", ".oxps", ".eml", ".ics", ".vcf", ".stl", ".3mf", ".gcode", ".qoi", ".bgcode" };
    private static readonly HashSet<string> ZipPreviewExt = new(StringComparer.OrdinalIgnoreCase)
        { ".sketch", ".xd", ".kra", ".ora", ".3mf", ".key", ".pages", ".numbers", ".cdrx", ".fig", ".graffle", ".procreate", ".afphoto2" };
    private static readonly HashSet<string> ThumbFirstExt = new(StringComparer.OrdinalIgnoreCase)
        { ".svg", ".svgz", ".skp", ".blend", ".stl", ".fbx", ".3ds", ".max", ".c4d", ".afdesign", ".afphoto", ".afpub", ".xcf", ".kra", ".clip", ".psp", ".pspimage", ".cpt", ".mdi" };

    private static IEnumerable<Step> BuildChain(string path, string ext, byte[] head, Size target, PreviewOptions opt, CancellationToken ct)
    {
        var steps = new List<Step>();
        void Add(string name, Func<Task<PreviewResult?>> f) => steps.Add(new Step(name, f));
        void AddSync(string name, Func<PreviewResult?> f) => steps.Add(new Step(name, () => Task.FromResult(f())));

        // A search match is shown: the file's own text first, so the words can be highlighted (PreviewOptions.PreferBuiltInText).
        bool text = opt.PreferBuiltInText;
        Step Wic = new("WIC", () => WicImage.RenderAsync(path, ext, target, ct));
        Step ShellThumb = new("ShellThumbnail", () => ShellThumbnailAsync(path, target, ct));
        Step Handler = new("PreviewHandler", () => Task.FromResult(opt.AllowHostedHandler ? HandlerResult(path, ext) : null));
        Step Text = new("Text", () => Task.FromResult(TextPreview.Render(path, ext, opt.MaxTextBytes, force: false, keepLayout: text)));
        Step Xmp = new("XMP thumbnail", () => Task.FromResult(XmpThumb(path, target)));
        Step OfficeThumb = new("Embedded thumbnail", () => Task.FromResult(OfficeThumbnail(path, target)));
        Step Pdf = new("PDF", () => PdfPreview.RenderAsync(path, target, ct, keepSinglePage: text));

        // A built-in text step moved ahead of the file's own preview: say so on its result when that preview exists
        // (a preview handler that would have been hosted, or a saved thumbnail that came first), so the pane offers it.
        Step BuiltIn(string name, Func<PreviewResult?> render, bool thumbnailFirst) => new(name, () =>
        {
            var r = render();
            if (r != null && HasOriginal(path, ext, opt, thumbnailFirst)) r.InsteadOfOriginal = r.ShapedByMatch = true;
            return Task.FromResult(r);
        });

        bool isPdf = Bytes.StartsWithAscii(head, "%PDF") || Bytes.IndexOf(head.AsSpan(0, Math.Min(head.Length, 1024)), "%PDF"u8) >= 0;
        bool isZip = ArchivePreview.IsZip(head);
        bool isOle = head.Length >= 8 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0;

        switch (ext)
        {
            case ".psd":
            case ".psb":
                AddSync("PSD", () => PsdResult(path, target, ct));
                steps.Add(Xmp);
                steps.Add(Wic); // a Photoshop WIC codec may be installed
                steps.Add(ShellThumb);
                break;
            case ".tga":
            case ".icb":
            case ".vda":
            case ".vst" when !isOle:
                AddSync("TGA", () => TgaResult(path, target));
                steps.Add(ShellThumb);
                break;
            case ".emf":
            case ".wmf":
                AddSync("Metafile", () => MetafileRenderer.Render(path, target));
                steps.Add(ShellThumb);
                break;
            case ".pdf":
                steps.Add(Pdf);
                steps.Add(Handler);
                steps.Add(ShellThumb);
                break;
            case ".ai":
            case ".ait":
                if (isPdf) steps.Add(new Step("PDF", () => PdfPreview.RenderAsync(path, target, ct, "Illustrator (PDF)", keepSinglePage: text)));
                AddSync("EPS preview", () => EpsResult(path, target));
                steps.Add(Xmp);
                steps.Add(Handler);
                steps.Add(ShellThumb);
                AddSync("Embedded image scan", () => ScanResult(path, target));
                break;
            case ".eps":
            case ".epsf":
            case ".epsi":
            case ".ps":
                AddSync("EPS preview", () => EpsResult(path, target));
                steps.Add(Xmp);
                steps.Add(ShellThumb);
                AddSync("Text", () => TextPreview.Render(path, ext, opt.MaxTextBytes, force: false));
                break;
            case ".indd":
            case ".indt":
            case ".indl":
            case ".idml":
                steps.Add(Xmp);
                steps.Add(ShellThumb);
                AddSync("Embedded image scan", () => ScanResult(path, target));
                break;
            case ".cdr":
            case ".cdt":
            case ".cdrx":
            case ".cmx":
            case ".cdx":
            case ".des":
                AddSync("CorelDRAW", () => CorelResult(path, target));
                steps.Add(ShellThumb);
                break;
            case ".dwg":
            case ".dwt":
                AddSync("DWG", () => DwgResult(path, target));
                steps.Add(ShellThumb);
                break;
            case ".blend":
                steps.Add(ShellThumb);
                AddSync("Blender", () => ImageResult(path, "Blender", "Blender scene", EmbeddedPreviews.FromBlend(path, BitmapUtil.DecodeBox(target)), "embedded TEST thumbnail"));
                break;
            case ".afdesign":
            case ".afphoto":
            case ".afpub":
                steps.Add(ShellThumb);
                AddSync("Embedded image scan", () => ScanResult(path, target));
                break;
            case ".dxf":
                AddSync("DXF", () => DxfRenderer.Render(path, target, ct));
                steps.Add(ShellThumb);
                steps.Add(Text);
                break;
            case ".csv":
            case ".tsv":
            case ".tab":
                AddSync("CSV", () => OfficePreview.RenderCsv(path, ct));
                steps.Add(Text);
                break;
            case ".rtf":
                if (text)
                {
                    steps.Add(BuiltIn("RTF", () => RtfResult(path), thumbnailFirst: false));
                    steps.Add(Text);
                    steps.Add(Handler);
                    break;
                }
                steps.Add(Handler);
                AddSync("RTF", () => RtfResult(path));
                steps.Add(Text);
                break;
            default:
                if (WicImage.Extensions.Contains(ext))
                {
                    steps.Add(Wic);
                    steps.Add(ShellThumb);
                }
                else if (OfficePreview.WordExt.Contains(ext))
                {
                    if (text) steps.Add(BuiltIn("Word", () => OfficePreview.RenderDocx(path, ct), thumbnailFirst: false));
                    steps.Add(Handler);
                    if (!text) AddSync("Word", () => OfficePreview.RenderDocx(path, ct));
                    steps.Add(OfficeThumb);
                    steps.Add(ShellThumb);
                }
                else if (OfficePreview.ExcelExt.Contains(ext))
                {
                    if (text) steps.Add(BuiltIn("Excel", () => OfficePreview.RenderXlsx(path, ct, opt.Sheet), thumbnailFirst: false));
                    steps.Add(Handler);
                    if (!text) AddSync("Excel", () => OfficePreview.RenderXlsx(path, ct, opt.Sheet));
                    steps.Add(OfficeThumb);
                    steps.Add(ShellThumb);
                }
                else if (OfficePreview.PowerPointExt.Contains(ext))
                {
                    if (text)
                    {
                        steps.Add(BuiltIn("PowerPoint", () => OfficePreview.RenderPptxText(path, ct, presentationOrder: true), thumbnailFirst: true));
                        steps.Add(Handler);
                        steps.Add(OfficeThumb);
                    }
                    else
                    {
                        steps.Add(Handler);
                        steps.Add(OfficeThumb);
                        AddSync("PowerPoint", () => OfficePreview.RenderPptxText(path, ct));
                    }
                    steps.Add(ShellThumb);
                }
                else if (OfficePreview.OdfExt.Contains(ext))
                {
                    bool thumbFirst = ext is ".odp" or ".otp" or ".odg";
                    if (text) steps.Add(BuiltIn("OpenDocument", () => OfficePreview.RenderOdf(path, ext, ct, opt.Sheet), thumbFirst));
                    steps.Add(Handler);
                    if (!text)
                    {
                        if (thumbFirst) steps.Add(OfficeThumb);
                        AddSync("OpenDocument", () => OfficePreview.RenderOdf(path, ext, ct, opt.Sheet));
                    }
                    steps.Add(OfficeThumb);
                    steps.Add(ShellThumb);
                }
                else if (OleExt.Contains(ext) && isOle || OtherHandlerExt.Contains(ext))
                {
                    steps.Add(Handler);
                    steps.Add(ShellThumb);
                    steps.Add(Text);
                }
                else if (MediaPreview.Video.Contains(ext) || MediaPreview.Audio.Contains(ext))
                {
                    Add("Media", async () => await MediaPreview.RenderAsync(path, ext, target, ct).ConfigureAwait(false));
                }
                else if (FontPreview.Extensions.Contains(ext))
                {
                    Add("Font", () => FontPreview.RenderAsync(path, target, ct));
                    steps.Add(ShellThumb);
                }
                else if (ZipPreviewExt.Contains(ext) && isZip)
                {
                    AddSync("Embedded preview", () => ZipEmbeddedPreview(path, target));
                    steps.Add(ShellThumb);
                    AddSync("Archive", () => ArchivePreview.RenderZip(path, ct));
                }
                else if (ThumbFirstExt.Contains(ext))
                {
                    steps.Add(Handler);
                    steps.Add(ShellThumb);
                    steps.Add(Text);
                }
                else if (ArchivePreview.ZipLike.Contains(ext) || isZip && !TextPreview.KnownTextExtensions.Contains(ext))
                {
                    AddSync("Archive", () => ArchivePreview.RenderZip(path, ct));
                    steps.Add(ShellThumb);
                }
                else if (ArchivePreview.TarLike.Contains(ext))
                {
                    AddSync("Archive", () => ArchivePreview.RenderTar(path, ct));
                    steps.Add(ShellThumb);
                }
                else if (TextPreview.KnownTextExtensions.Contains(ext))
                {
                    steps.Add(Text);
                    steps.Add(Handler);
                    steps.Add(ShellThumb);
                }
                else
                {
                    // Unknown extension: sniff the content.
                    if (isPdf) steps.Add(Pdf);
                    if (LooksLikeImage(head)) steps.Add(Wic);
                    if (Bytes.StartsWithAscii(head, "8BPS")) AddSync("PSD", () => PsdResult(path, target, ct));
                    if (head.Length > 6 && Bytes.StartsWithAscii(head, "AC10")) AddSync("DWG", () => DwgResult(path, target));
                    if (Bytes.StartsWithAscii(head, "RIFF") && head.Length > 12 && (head[8] == 'C' || head[8] == 'c') && (head[9] == 'D' || head[9] == 'd'))
                        AddSync("CorelDRAW", () => CorelResult(path, target));
                    if (head.Length >= 4 && Bytes.StartsWithAscii(head, "wOFF") || Bytes.StartsWithAscii(head, "OTTO") ||
                        head.Length >= 4 && head[0] == 0 && head[1] == 1 && head[2] == 0 && head[3] == 0 && ext is ".tte" or ".eot" or "")
                        Add("Font", () => FontPreview.RenderAsync(path, target, ct));
                    steps.Add(Text);
                    steps.Add(Handler);
                    steps.Add(ShellThumb);
                }
                break;
        }
        return steps;
    }

    private static bool LooksLikeImage(byte[] h) =>
        h.Length >= 12 && (
            h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G' ||
            h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF ||
            h[0] == 'G' && h[1] == 'I' && h[2] == 'F' ||
            h[0] == 'B' && h[1] == 'M' && Bytes.U32LE(h, 2) > 26 ||
            h[0] == 'I' && h[1] == 'I' && h[2] == 42 && h[3] == 0 ||
            h[0] == 'M' && h[1] == 'M' && h[2] == 0 && h[3] == 42 ||
            Bytes.StartsWithAscii(h, "RIFF") && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P' ||
            h[4] == 'f' && h[5] == 't' && h[6] == 'y' && h[7] == 'p' && (h[8] == 'h' || h[8] == 'a' || h[8] == 'm'));

    /// <summary>
    /// Without a match, the file's own preview would have been shown ahead of the built-in text: a preview handler that is
    /// registered, allowed and not refused for a downloaded file, or (decks) a saved thumbnail.
    /// </summary>
    private static bool HasOriginal(string path, string ext, PreviewOptions opt, bool thumbnailFirst) =>
        opt.AllowHostedHandler && PreviewHandlerRegistry.TryFind(ext, out _, out _) && !InternetZone(path)
        || thumbnailFirst && OfficePreview.HasEmbeddedThumbnail(path);

    /// <summary>Files whose preview changes when a match is shown (<see cref="PreviewOptions.PreferBuiltInText"/>).</summary>
    internal static bool PrefersTextForMatch(string path)
    {
        string ext = GetExtension(path);
        return OfficePreview.WordExt.Contains(ext) || OfficePreview.ExcelExt.Contains(ext) || OfficePreview.PowerPointExt.Contains(ext)
            || OfficePreview.OdfExt.Contains(ext) || ext is ".rtf" or ".json";
    }

    // ------------------------------------------------------------------ individual steps

    internal static PreviewResult? HandlerResult(string path, string ext)
    {
        if (!PreviewHandlerRegistry.TryFind(ext, out var clsid, out var name)) return null;
        // Like Explorer, don't hand internet-downloaded files (Mark of the Web, zone 3/4) to third-party handlers:
        // Office refuses them anyway (after seconds of start-up). Our own parsers render them instead.
        if (InternetZone(path)) return null;
        var r = new PreviewResult(path, PreviewKind.HostedHandler, "PreviewHandler")
        {
            HandlerClsid = clsid,
            HandlerName = name,
            FormatName = Shell.GetTypeName(path, false),
        };
        r.AddMeta("Handler", name ?? clsid.ToString("B"));
        return r;
    }

    internal static bool InternetZone(string path)
    {
        try
        {
            using var s = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(s);
            string text = r.ReadToEnd();
            int i = text.IndexOf("ZoneId=", StringComparison.OrdinalIgnoreCase);
            return i >= 0 && i + 7 < text.Length && text[i + 7] is '3' or '4';
        }
        catch
        {
            return false;
        }
    }

    private static async Task<PreviewResult?> ShellThumbnailAsync(string path, Size target, CancellationToken ct)
    {
        int size = Math.Clamp(Math.Max(target.Width, target.Height), 256, 1024);
        var bmp = await Shell.GetThumbnailAsync(path, size, ct).ConfigureAwait(false);
        if (bmp == null) return null;
        var r = new PreviewResult(path, PreviewKind.Image, "ShellThumbnail")
        {
            Image = bmp,
            HasAlpha = true,
            FormatName = Shell.GetTypeName(path, false),
        };
        InfoCard.AddFileFacts(r, new FileInfo(path));
        return r;
    }

    private static PreviewResult? ImageResult(string path, string provider, string? format, Bitmap? bmp, string? note = null)
    {
        if (bmp == null) return null;
        var r = new PreviewResult(path, PreviewKind.Image, provider) { Image = bmp, FormatName = format, HasAlpha = true };
        r.AddMeta("Preview", note);
        return r;
    }

    private static PreviewResult? XmpThumb(string path, Size target)
    {
        var jpg = EmbeddedPreviews.FindXmpThumbnail(path);
        if (jpg == null) return null;
        var bmp = BitmapUtil.Decode(jpg, target, out var src, out _);
        var r = ImageResult(path, "XMP thumbnail", null, bmp, $"embedded XMP thumbnail ({src.Width}×{src.Height})");
        return r;
    }

    private static PreviewResult? ScanResult(string path, Size target)
    {
        var data = FileUtil.ReadHead(path, 48 * 1024 * 1024);
        var img = EmbeddedPreviews.FindLargestEmbeddedImage(data);
        if (img == null) return null;
        var bmp = BitmapUtil.Decode(img, target, out var src, out _);
        return ImageResult(path, "Embedded image", null, bmp, $"largest embedded image ({src.Width}×{src.Height})");
    }

    private static PreviewResult? EpsResult(string path, Size target)
    {
        var bmp = EmbeddedPreviews.FromEpsBinaryHeader(path, BitmapUtil.DecodeBox(target), out var kind);
        return ImageResult(path, "EPS", "Encapsulated PostScript", bmp, kind);
    }

    private static PreviewResult? CorelResult(string path, Size target)
    {
        var bmp = EmbeddedPreviews.FromCorel(path, BitmapUtil.DecodeBox(target), out var kind, out var version);
        var r = ImageResult(path, "CorelDRAW", "CorelDRAW drawing", bmp, kind != null ? "embedded " + kind : null);
        r?.AddMeta("Version", version);
        if (r?.Image != null) r.AddMeta("Preview size", $"{r.Image.Width} × {r.Image.Height}");
        return r;
    }

    private static PreviewResult? DwgResult(string path, Size target)
    {
        var bmp = EmbeddedPreviews.FromDwg(path, BitmapUtil.DecodeBox(target), out var kind, out var version);
        var r = ImageResult(path, "DWG", "AutoCAD drawing", bmp, kind);
        if (r != null && version != null) r.AddMeta("Version", (EmbeddedPreviews.DwgVersionName(version) ?? version) + $" ({version})");
        if (r != null) r.HasAlpha = false;
        return r;
    }

    private static PreviewResult? PsdResult(string path, Size target, CancellationToken ct)
    {
        var bmp = PsdDecoder.Decode(path, BitmapUtil.DecodeBox(target), ct, out var info, out bool fromThumb);
        if (bmp == null || info == null) return null;
        var r = new PreviewResult(path, PreviewKind.Image, "PSD")
        {
            Image = bmp,
            FormatName = info.IsPsb ? "Photoshop large document (PSB)" : "Photoshop document",
            SourcePixelSize = new Size(info.Width, info.Height),
        };
        r.AddMeta("Dimensions", $"{info.Width} × {info.Height}");
        r.AddMeta("Color", $"{info.ColorModeName}, {info.Depth}-bit, {info.Channels} channels");
        r.AddMeta("Preview", fromThumb ? "embedded thumbnail (composite not decodable)" : "merged composite");
        return r;
    }

    private static PreviewResult? TgaResult(string path, Size target)
    {
        var data = FileUtil.ReadAllCapped(path, 512L * 1024 * 1024);
        if (data == null) return null;
        var bmp = TgaDecoder.Decode(data, out bool alpha);
        if (bmp == null) return null;
        var src = bmp.Size;
        var r = new PreviewResult(path, PreviewKind.Image, "TGA")
        {
            Image = BitmapUtil.FitOrSelf(bmp, BitmapUtil.DecodeBox(target)), FormatName = "Truevision TGA image", HasAlpha = alpha,
            SourcePixelSize = src,
        };
        r.AddMeta("Dimensions", $"{src.Width} × {src.Height}");
        r.AddMeta("Bit depth", data[16] + "-bit" + (data[2] >= 9 ? ", RLE" : ""));
        return r;
    }

    private static PreviewResult? OfficeThumbnail(string path, Size target)
    {
        var bmp = OfficePreview.EmbeddedThumbnail(path, BitmapUtil.DecodeBox(target));
        return ImageResult(path, "Embedded thumbnail", null, bmp, "thumbnail saved with the document");
    }

    private static PreviewResult? ZipEmbeddedPreview(string path, Size target)
    {
        using var fs = FileUtil.OpenShared(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        string[] names = { "previews/preview.png", "preview.png", "mergedimage.png", "Thumbnails/thumbnail.png", "Metadata/thumbnail.png",
                           "QuickLook/Preview.jpg", "QuickLook/Thumbnail.jpg", "preview.jpg", "thumbnail.png", "renditions/thumbnail.png" };
        foreach (var n in names)
        {
            var e = zip.GetEntry(n) ?? zip.Entries.FirstOrDefault(x => x.FullName.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (e == null || e.Length > 64 << 20) continue;
            using var es = e.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            var bmp = BitmapUtil.Decode(ms.ToArray(), BitmapUtil.DecodeBox(target));
            if (bmp != null) return ImageResult(path, "Embedded preview", null, bmp, e.FullName);
        }
        return null;
    }

    private static PreviewResult? RtfResult(string path)
    {
        var bytes = FileUtil.ReadHead(path, 8 * 1024 * 1024);
        if (!Bytes.StartsWithAscii(bytes, "{\\rtf")) return null;
        return new PreviewResult(path, PreviewKind.Document, "RTF") { FormatName = "Rich Text document", Rtf = System.Text.Encoding.Latin1.GetString(bytes) };
    }
}

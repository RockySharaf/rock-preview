namespace Anything.Core.Thumbs;

/// <summary>Which source makes the real (Final) thumbnail of a kind of file, step 4 of the ladder.</summary>
public enum ThumbRoute : byte
{
    /// <summary>A picture: Rock Preview's own decoder at the size class, then Windows' extractor, then nothing.</summary>
    Picture,
    /// <summary>PSD and PSB: the composite (the thumbnail resource is only the stand-in).</summary>
    Psd,
    /// <summary>PDF, Office, fonts, text and the other kinds Rock Preview renders itself: its own extractor, then Windows'.</summary>
    OwnExtractor,
    /// <summary>Video, folders and every kind Rock Preview cannot decode: Windows' extractor, then the text sniff.</summary>
    Shell,
}

/// <summary>The preview a file may carry inside itself (step 3 of the ladder): always a stand-in, never stored.</summary>
public enum EmbeddedPreview : byte
{
    /// <summary>None worth reading (HEIC's codec thumbnail costs a full decode, so it counts as none).</summary>
    None,
    /// <summary>The EXIF thumbnail of a JPEG (at most 160 px, stored unturned: apply the EXIF orientation).</summary>
    Exif,
    /// <summary>The thumbnail resource (1036, or 1033 in old files) of a PSD or PSB.</summary>
    PsdResource,
    /// <summary>The preview JPEG a camera RAW file carries.</summary>
    Raw,
}

/// <summary>How a Final picture is encoded for the store.</summary>
public enum ThumbEncoding : byte
{
    /// <summary>JPEG at quality 85 (photo kinds).</summary>
    Jpeg85,
    /// <summary>PNG (flat kinds and anything with transparency).</summary>
    Png,
    /// <summary>JPEG at quality 90: a flat picture whose PNG came out too big.</summary>
    Jpeg90,
}

/// <summary>How good a picture is: Low is shown at once and replaced, Final is stored, None shows the icon.</summary>
public enum ThumbQuality : byte
{
    /// <summary>No picture can be made: the icon is shown.</summary>
    None,
    /// <summary>A stand-in (an embedded preview, or one too small for the class): never stored.</summary>
    Low,
    /// <summary>The real thumbnail at the size class.</summary>
    Final,
}

/// <summary>
/// The table of kinds: which extension takes which route, which carry an embedded preview, which are stored as PNG,
/// which icons belong to the file, and the size rules the ladder uses. Pure and thread-safe. Extensions are compared
/// without regard to case and may be given with or without the dot; a path may be given instead of an extension.
/// </summary>
public static class ThumbKinds
{
    /// <summary>Files this big or bigger ask Windows' thumbnail cache first; smaller pictures decode about as fast as asking.</summary>
    public const long CacheProbeMinBytes = 1536L * 1024;
    /// <summary>A full-frame decode bigger than this waits for the one "large decode" gate.</summary>
    public const long LargeDecodePixels = 64L * 1000 * 1000;
    /// <summary>A full-frame decode bigger than this is refused (Windows' extractor is asked instead).</summary>
    public const long RefusedDecodePixels = 400L * 1000 * 1000;
    /// <summary>A picture whose long side is under this share of what the class calls for is a stand-in (Low).</summary>
    public const double LowFraction = 0.8;
    /// <summary>A PNG over this many bytes at class 256 is stored as JPEG 90 instead.</summary>
    public const int PngLimitSmall = 48 * 1024;
    /// <summary>A PNG over this many bytes at class 512 is stored as JPEG 90 instead.</summary>
    public const int PngLimitLarge = 160 * 1024;

    static readonly HashSet<string> Raw = Set(
        "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "dng", "orf", "rw2", "raf", "pef", "srw",
        "x3f", "kdc", "dcr", "mrw", "3fr", "erf", "mef", "mos", "iiq", "rwl");

    // The kinds WicImage decodes (rle included: WicImage handles it), plus the RAW list.
    static readonly HashSet<string> Pictures = Union(Set(
        "jpg", "jpeg", "jpe", "jfif", "png", "apng", "bmp", "dib", "rle", "gif", "tif", "tiff", "webp", "heic", "heif",
        "hif", "avif", "jxr", "wdp", "hdp", "dds", "jxl", "ico", "cur"), Raw);

    static readonly HashSet<string> Jpeg = Set("jpg", "jpeg", "jpe", "jfif");
    static readonly HashSet<string> Psd = Set("psd", "psb");

    // What ThumbnailProvider.ExtractAsync renders itself, by family.
    static readonly HashSet<string> Vector = Set(
        "cdr", "cdt", "cdrx", "cmx", "des", "dwg", "dwt", "dxf", "ai", "ait", "eps", "epsf", "ps", "indd", "indt",
        "emf", "wmf", "afdesign", "afpub");
    static readonly HashSet<string> OtherOwn = Set("pdf", "tga", "icb", "vda", "blend", "afphoto");
    static readonly HashSet<string> Office = Set(
        "docx", "docm", "dotx", "dotm", "xlsx", "xlsm", "xltx", "xltm", "pptx", "pptm", "ppsx", "ppsm", "potx", "potm",
        "odt", "ott", "ods", "ots", "odp", "otp", "odg");
    static readonly HashSet<string> Fonts = Set("ttf", "otf", "ttc", "otc", "woff", "fon", "fnt");

    // The same list as TextPreview.KnownTextExtensions: these render as a text page.
    static readonly HashSet<string> Text = Set(
        "txt", "text", "log", "md", "markdown", "rst", "adoc", "json", "jsonc", "json5", "xml", "xaml", "xsd",
        "xsl", "xslt", "config", "csproj", "vbproj", "fsproj", "vcxproj", "props", "targets", "sln", "slnx", "nuspec",
        "resx", "manifest", "yml", "yaml", "toml", "ini", "cfg", "conf", "properties", "env", "editorconfig",
        "gitignore", "gitattributes", "cs", "csx", "vb", "fs", "fsx", "c", "h", "cpp", "cc", "cxx", "hpp", "hh",
        "hxx", "inl", "m", "mm", "java", "kt", "kts", "scala", "groovy", "gradle", "go", "rs", "swift", "dart",
        "py", "pyw", "pyi", "rb", "php", "pl", "pm", "lua", "r", "jl", "js", "mjs", "cjs", "jsx", "ts", "tsx",
        "vue", "svelte", "css", "scss", "sass", "less", "html", "htm", "xhtml", "shtml", "svg", "sql", "sh",
        "bash", "zsh", "fish", "ps1", "psm1", "psd1", "bat", "cmd", "reg", "inf", "nfo", "diz", "srt", "vtt",
        "ass", "sub", "tex", "bib", "sty", "cls", "asm", "s", "hlsl", "glsl", "fx", "shader", "cmake", "mk",
        "makefile", "dockerfile", "tf", "hcl", "graphql", "gql", "proto", "plist", "gcode", "nc", "obj", "mtl",
        "ply", "pem", "crt", "cer", "key", "pub", "asc", "ics", "vcf", "eml", "mbox", "rtf", "lst", "map",
        "csv", "tsv", "patch", "diff", "lock", "gitmodules", "npmrc", "babelrc", "eslintrc", "prettierrc", "ahk",
        "au3", "vbs", "vba", "bas", "frm", "pas", "dpr", "f90", "for", "cob", "ada", "erl", "ex", "exs", "hs",
        "elm", "clj", "lisp", "el", "scm", "ml", "nim", "zig", "v", "sv", "vhd", "vhdl", "tcl", "awk", "sed",
        "m3u", "m3u8", "pls", "cue", "url", "desktop", "ipynb", "geojson", "kml", "gpx", "wkt", "sol", "razor", "cshtml");

    // Flat pictures: text, lines and flat colour, where JPEG 85 measured 22 to 32 dB.
    static readonly HashSet<string> FlatPictures = Set("png", "apng", "bmp", "dib", "rle", "gif", "ico", "cur");

    // Videos (EverydayFiles' list): Windows' extractor makes their pictures, and they are stored like photos.
    static readonly HashSet<string> Video = Set(
        "mp4", "m4v", "mov", "avi", "mkv", "webm", "wmv", "flv", "f4v", "mpg", "mpeg", "mpe", "m2ts", "mts", "m2t",
        "3gp", "3g2", "ogv", "vob", "asf", "rm", "rmvb", "divx", "mxf");

    // Kinds whose icon is the file's own (drawn from inside it, or chosen per file), so it can't be shared per extension.
    static readonly HashSet<string> OwnIcon = Set("exe", "lnk", "url", "ico", "cur", "ani", "scr", "cpl", "msi", "pif", "appref-ms");

    // Spreadsheets carry a picture only when saved with a thumbnail, which they mostly aren't (measured: 40 of 40 xlsx
    // gave none). Without one, an Excel workbook gets a sheet page of its first cells, which is never kept on disk (like a
    // text page), and an OpenDocument one its icon: the estimate counts them all as nothing.
    static readonly HashSet<string> Spreadsheets = Set("xlsx", "xlsm", "xltx", "xltm", "ods", "ots");

    // Word and Excel 97-2003 files: Windows is asked first, exactly as for any kind it pictures (the picture the author
    // saved in the file, when there is one); after a miss Rock Preview draws a page of the first lines or a sheet page of
    // the first cells, and a file that only carries the extension (RTF, HTML or text named .doc or .xls) goes on to the
    // text handling as before. Those pages are never kept on disk, and what Windows gives for these kinds stays in memory
    // as before (the estimate counts them as nothing).
    static readonly HashSet<string> OldOffice = Set("doc", "dot", "xls", "xlt");

    // Own-extractor kinds whose own path renders a PDF page in this process (Windows.Data.Pdf, one page at a time for the
    // whole process, and an access violation at exit on some PCs): Windows is asked first, but its thumbnail cache only,
    // never its extractor (see WindowsCacheOnly); then the own page renderer.
    static readonly HashSet<string> PdfPages = Set("pdf", "ai", "ait");

    // Kinds Windows pictures first: the PDF pages above (its cache only), fonts, and the old Word and Excel files (whose
    // own picture, when the author saved one, only Windows reads). A font's own sample is drawn in this process with the
    // font loaded into GDI+, which holds the font engine every thread drawing text shares: measured, the grid's captions
    // took 4 to 5 ms each instead of 0.1 while three workers drew font samples, so a screen of them took over 100 ms to
    // paint. Windows draws its font thumbnails in a helper process (captions stayed at 0.12 ms).
    static readonly HashSet<string> ShellFirstKinds = Union(Union(PdfPages, Fonts), OldOffice);

    // Kinds whose own extractor draws text in this process (text pages, a Word file's page and an Excel file's sheet page
    // when it has no thumbnail of its own, a font's sample): one at a time, for the same reason (captions 0.3 ms with one
    // worker drawing pages, 0.9 with three). The old Word and Excel files are among them: their own extractor runs only
    // after Windows had no picture, and then it draws one of those pages.
    static readonly HashSet<string> WordFiles = Set("docx", "docm", "dotx", "dotm");
    static readonly HashSet<string> ExcelFiles = Set("xlsx", "xlsm", "xltx", "xltm");

    /// <summary>The route that makes the Final thumbnail of a file with this extension (a folder: <see cref="ThumbRoute.Shell"/>).</summary>
    public static ThumbRoute RouteOf(string extensionOrPath, bool isFolder = false)
    {
        if (isFolder) return ThumbRoute.Shell;
        string ext = Ext(extensionOrPath);
        if (Pictures.Contains(ext)) return ThumbRoute.Picture;
        if (Psd.Contains(ext)) return ThumbRoute.Psd;
        if (IsOwnExtractor(ext)) return ThumbRoute.OwnExtractor;
        return ThumbRoute.Shell;
    }

    /// <summary>Whether Rock Preview's own decoder or extractor makes this kind (pictures, PSD and the extractor kinds).</summary>
    public static bool HasOwnDecoder(string extensionOrPath) => RouteOf(extensionOrPath) != ThumbRoute.Shell;

    /// <summary>
    /// Whether Windows is asked before Rock Preview's own extractor for this kind: PDF and Illustrator files (often PDFs
    /// inside), whose own path renders PDF pages in this process, one at a time for the whole program (for them only
    /// Windows' thumbnail cache is asked: see <see cref="WindowsCacheOnly"/>); fonts, whose own sample holds the font
    /// engine the grid's captions need; and Word and Excel 97-2003 files (doc, dot, xls, xlt), whose saved picture only
    /// Windows reads, and which get Rock Preview's own page only when Windows has none.
    /// </summary>
    public static bool ShellFirst(string extensionOrPath) => ShellFirstKinds.Contains(Ext(extensionOrPath));

    /// <summary>Whether Rock Preview's own extractor renders a PDF page for this kind (pdf, ai, ait): one page at a time for the process.</summary>
    public static bool RendersPdfPage(string extensionOrPath) => PdfPages.Contains(Ext(extensionOrPath));

    /// <summary>
    /// Whether Windows is asked for this file's picture from its thumbnail cache only, never by its extractor in this
    /// process: any folder (<paramref name="isFolder"/>, whatever its name), the PDF kinds (pdf, ai, ait) and shortcuts
    /// (lnk). Windows has no PDF thumbnail maker of its own, so another program's handler answers, and it may be
    /// registered to load into the asking process (Wondershare PDFelement 12's is). Measured on 2026-11-22: that handler
    /// ended the process, inside libPdfCore.dll with exception 0x80000003, four times in one day, also when it was asked
    /// for one file at a time, so nothing done around the call makes it safe. Windows asks the same handler for a
    /// folder's picture (a PDF inside it) and for a shortcut to a PDF (seen in the handler's own log); a cache-only call
    /// (ThumbnailOnly | InCacheOnly) and an icon call never reach it (seen in the same log). So for these files: Windows'
    /// thumbnail cache only (it may hold a picture File Explorer made), then Rock Preview's own extractor for the PDF
    /// kinds, else the icon. The extension is read from the name as Windows opens it: a stream name after ':' is cut off,
    /// and trailing spaces and dots are ignored ("a.pdf ", "a.pdf." and "a.pdf:x" are PDFs to Windows).
    /// </summary>
    public static bool WindowsCacheOnly(string extensionOrPath, bool isFolder = false)
    {
        if (isFolder) return true;
        string ext = Ext(AsWindowsOpensIt(extensionOrPath));
        return ext == "lnk" || PdfPages.Contains(ext);
    }

    // The path with its file name as Windows opens it: no trailing separators, no alternate data stream (":name" after
    // the last separator; in a drive-relative name such as "D:report.pdf" the colon is the drive's), no trailing spaces
    // or dots. The folders before the name are kept, so a name without an extension still has none.
    static string AsWindowsOpensIt(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var s = path.AsSpan().TrimEnd("\\/");
        int start = s.LastIndexOfAny('\\', '/') + 1;
        if (start == 0 && s.Length >= 2 && s[1] == ':' && char.IsAsciiLetter(s[0])) start = 2;
        var name = s[start..];
        int colon = name.IndexOf(':');
        if (colon >= 0) name = name[..colon];
        name = name.TrimEnd(" .");
        return start + name.Length == path.Length ? path : string.Concat(s[..start], name);
    }

    /// <summary>
    /// Whether Rock Preview's own extractor may draw text for this kind (text pages, Word pages and Excel sheet pages
    /// without a thumbnail, old Word and Excel files, font samples): such work takes the font engine that every thread
    /// drawing text shares, the grid's captions included, so the service runs one at a time.
    /// </summary>
    public static bool DrawsTextWhenMade(string extensionOrPath)
    {
        string ext = Ext(extensionOrPath);
        return Text.Contains(ext) || Fonts.Contains(ext) || WordFiles.Contains(ext) || ExcelFiles.Contains(ext) || OldOffice.Contains(ext);
    }

    /// <summary>
    /// Whether this kind's thumbnail is a page of the file's own text (source, text, markup, mail, keys...): made each
    /// time it is needed and held in memory only. A text page is cheap to make, and a saved one would be a readable
    /// picture of a file's first lines (perhaps a key or a password) that outlives the file, on a store that may lie on a
    /// stick or a shared drive. Files of other kinds can come out as text pages too (an unknown kind that turns out to be
    /// text, a Word file's page or an Excel file's sheet page drawn when it has no thumbnail): the service knows those
    /// only once drawn, and keeps them in memory as well.
    /// </summary>
    public static bool DrawsText(string extensionOrPath) => Text.Contains(Ext(extensionOrPath));

    /// <summary>
    /// Whether a picture of this kind may be kept on disk: exactly the kinds the space estimate counts
    /// (<see cref="CostOf"/>): pictures, video frames and the pages of documents. Never text pages (<see cref="DrawsText"/>),
    /// and never what only Windows pictures for a kind the estimate counts as nothing (a music file's cover, a folder, a
    /// kind only a third-party handler draws): such pictures stay in memory (Windows' own cache keeps them, and is asked
    /// first for those kinds), so the space the user is told is the space used. The idle step makes none of them. Records
    /// without a picture ("none", "crashed") may be kept for any kind.
    /// </summary>
    public static bool KeepOnDisk(string extensionOrPath, bool isFolder = false) => CostOf(extensionOrPath, isFolder) != ThumbCost.Nothing;

    /// <summary>Whether this is a camera RAW kind.</summary>
    public static bool IsRaw(string extensionOrPath) => Raw.Contains(Ext(extensionOrPath));

    /// <summary>Whether this is a video kind (its picture comes from Windows' extractor).</summary>
    public static bool IsVideo(string extensionOrPath) => Video.Contains(Ext(extensionOrPath));

    /// <summary>
    /// Which kind of stored thumbnail a file of this extension gives, for the space estimate: photos (pictures, PSD,
    /// RAW and the photo-like kinds Rock Preview renders), videos, and documents (flat kinds: PDF, Office, fonts, vector
    /// formats, flat pictures); <see cref="ThumbCost.Nothing"/> for kinds that never give a stored picture: kinds that
    /// show their icon, text pages (never kept on disk), spreadsheets (mostly without a thumbnail; their sheet page is
    /// never kept) and the old Word and Excel files (Windows' picture or a page never kept). Folders count nothing.
    /// </summary>
    public static ThumbCost CostOf(string extensionOrPath, bool isFolder = false)
        => isFolder ? ThumbCost.Nothing : CostOfExt(Ext(extensionOrPath));

    /// <summary><see cref="CostOf"/> for a bare extension (no dot), any case, without allocating: the index walk.</summary>
    internal static ThumbCost CostOfExtension(ReadOnlySpan<char> extension)
        => CostLookup.TryGetValue(extension, out var cost) ? cost : ThumbCost.Nothing;

    static ThumbCost CostOfExt(string ext)
    {
        // Text pages are never stored; spreadsheets almost never have a picture; the old Word and Excel files counted
        // nothing when only Windows pictured them, and their own pages are never stored either.
        if (ext.Length == 0 || Text.Contains(ext) || Spreadsheets.Contains(ext) || OldOffice.Contains(ext)) return ThumbCost.Nothing;
        if (Video.Contains(ext)) return ThumbCost.Video;
        var route = RouteOf(ext);
        if (route == ThumbRoute.Shell) return ThumbCost.Nothing;
        return IsFlat(ext) ? ThumbCost.Document : ThumbCost.Photo;
    }

    // Every extension that costs something, for the allocation-free lookup of the index walk.
    static readonly Dictionary<string, ThumbCost>.AlternateLookup<ReadOnlySpan<char>> CostLookup = BuildCostLookup();

    static Dictionary<string, ThumbCost>.AlternateLookup<ReadOnlySpan<char>> BuildCostLookup()
    {
        var map = new Dictionary<string, ThumbCost>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in new[] { Pictures, Psd, Vector, OtherOwn, Office, Fonts, Video })
            foreach (var ext in set)
                if (CostOfExt(ext) is var cost and not ThumbCost.Nothing) map[ext] = cost;
        return map.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>Whether this is a JPEG (decoded once, at a scale, and may carry an EXIF thumbnail).</summary>
    public static bool IsJpeg(string extensionOrPath) => Jpeg.Contains(Ext(extensionOrPath));

    /// <summary>Whether this is an animated-capable GIF (only its first frame is decoded).</summary>
    public static bool IsGif(string extensionOrPath) => Ext(extensionOrPath) == "gif";

    /// <summary>The preview inside the file worth reading as a stand-in (never for HEIC).</summary>
    public static EmbeddedPreview EmbeddedPreviewOf(string extensionOrPath)
    {
        string ext = Ext(extensionOrPath);
        if (Jpeg.Contains(ext)) return EmbeddedPreview.Exif;
        if (Psd.Contains(ext)) return EmbeddedPreview.PsdResource;
        if (Raw.Contains(ext)) return EmbeddedPreview.Raw;
        return EmbeddedPreview.None;
    }

    /// <summary>
    /// Whether to ask Windows' thumbnail cache (cache only, never an extraction) before anything else: for kinds whose
    /// real source is Windows itself (video, kinds Rock Preview can't decode, folders, and the old Word and Excel files,
    /// whose saved picture only Windows reads), and for any file of 1.5 MB or more. A <paramref name="length"/> of 0 or
    /// less (not known) counts as small.
    /// </summary>
    public static bool AskWindowsCache(string extensionOrPath, long length, bool isFolder = false)
        => isFolder || RouteOf(extensionOrPath) == ThumbRoute.Shell || OldOffice.Contains(Ext(extensionOrPath)) || length >= CacheProbeMinBytes;

    /// <summary>Whether this kind is flat (documents, text pages, fonts, PDF, vector formats, icons, png/bmp/gif) and so stored as PNG.</summary>
    public static bool IsFlat(string extensionOrPath)
    {
        string ext = Ext(extensionOrPath);
        return FlatPictures.Contains(ext) || Vector.Contains(ext) || Office.Contains(ext) || Fonts.Contains(ext)
            || Text.Contains(ext) || ext == "pdf";
    }

    /// <summary>
    /// How to encode a Final picture for the store: PNG for flat kinds and anything with transparency, JPEG 85 for the
    /// rest. Ask again with <paramref name="pngBytes"/> once the PNG is made: over 48 KB at class 256 (160 KB at 512) it
    /// becomes JPEG 90. Pass 0 for <paramref name="pngBytes"/> before the PNG exists.
    /// </summary>
    public static ThumbEncoding EncodingFor(string extensionOrPath, bool hasAlpha, int sizeClass, int pngBytes = 0)
    {
        if (!hasAlpha && !IsFlat(extensionOrPath)) return ThumbEncoding.Jpeg85;
        int limit = ThumbSizeClass.For(sizeClass) == ThumbSizeClass.Small ? PngLimitSmall : PngLimitLarge;
        // A picture with transparency stays PNG whatever its size: JPEG would lose the transparency.
        return pngBytes > limit && !hasAlpha ? ThumbEncoding.Jpeg90 : ThumbEncoding.Png;
    }

    /// <summary>Whether a file's icon is its own (exe, lnk, url, ico, cur, ani, scr, cpl, msi, pif, appref-ms; folders too), so the icon can't be remembered per extension.</summary>
    public static bool IconBelongsToFile(string extensionOrPath, bool isFolder = false)
        => isFolder || OwnIcon.Contains(Ext(extensionOrPath));

    /// <summary>
    /// Low or Final for a picture whose long side is <paramref name="longSide"/>: Low when it is under 80 % of what a
    /// picture of that file at <paramref name="sizeClass"/> would have, that is the class, or the source's own long side
    /// when that is smaller (<paramref name="sourceLongSide"/>, 0 when not known). No picture is None.
    /// </summary>
    public static ThumbQuality QualityOf(int longSide, int sizeClass, int sourceLongSide = 0)
    {
        if (longSide <= 0) return ThumbQuality.None;
        int expected = ThumbSizeClass.For(sizeClass);
        if (sourceLongSide > 0 && sourceLongSide < expected) expected = sourceLongSide;
        return longSide < expected * LowFraction ? ThumbQuality.Low : ThumbQuality.Final;
    }

    /// <summary>What a full-frame decode of this many pixels may do: run, wait for the large-decode gate, or not run at all.</summary>
    public static DecodeAdmission AdmitDecode(long width, long height)
    {
        if (width <= 0 || height <= 0) return DecodeAdmission.Refused;
        long pixels = width * height;
        if (pixels > RefusedDecodePixels || pixels < 0) return DecodeAdmission.Refused;
        return pixels > LargeDecodePixels ? DecodeAdmission.LargeGate : DecodeAdmission.Run;
    }

    /// <summary>
    /// The most markers <see cref="JpegFrameOf"/> reads before it gives up (camera and web JPEGs have far fewer; a large
    /// colour profile or extended XMP split over many segments can have dozens).
    /// </summary>
    public const int JpegMaxMarkers = 256;

    /// <summary>How far <see cref="JpegFrameOf"/> looks past stray bytes for the next marker, as JPEG decoders do.</summary>
    public const int JpegMaxStray = 4096;

    /// <summary>
    /// What the first frame marker of a JPEG says, read through <paramref name="read"/> (four bytes per marker before
    /// the frame, at most <see cref="JpegMaxMarkers"/>): <see cref="JpegFrame.Baseline"/> only for a positively found
    /// SOF0 or SOF1; <see cref="JpegFrame.WholeFrame"/> for any other frame (progressive, lossless, hierarchical,
    /// arithmetic); <see cref="JpegFrame.Unknown"/> whenever the markers don't make it clear (no JPEG start, out of
    /// markers, no marker within <see cref="JpegMaxStray"/> bytes of stray bytes, a short or failed read, the end or a
    /// scan before any frame).
    /// </summary>
    public static JpegFrame JpegFrameOf(JpegReadAt read)
    {
        Span<byte> b = stackalloc byte[4];
        try
        {
            if (read(0, b[..2]) != 2 || b[0] != 0xFF || b[1] != 0xD8) return JpegFrame.Unknown;
            long pos = 2;
            for (int markers = 0; markers < JpegMaxMarkers; markers++)
            {
                if (read(pos, b) != 4) return JpegFrame.Unknown;
                if (b[0] != 0xFF || b[1] == 0x00)
                {
                    // Stray bytes where a marker should be: JPEG decoders skip them to the next marker, so this does too
                    // (a short way only; 0xFF followed by 0x00 is not a marker).
                    pos = NextMarker(read, pos + 1);
                    if (pos < 0) return JpegFrame.Unknown;
                    continue;
                }
                byte m = b[1];
                if (m == 0xFF) { pos++; continue; } // a fill byte before a marker
                // SOF0 and SOF1 are scaled while read; every other frame marker (C2 progressive, C3 lossless, C5 to C7 and
                // C9 to CF the hierarchical and arithmetic kinds) is not. C4, C8 and CC are tables, not frames.
                if (m is >= 0xC0 and <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC))
                    return m is 0xC0 or 0xC1 ? JpegFrame.Baseline : JpegFrame.WholeFrame;
                if (m is 0xD9 or 0xDA) return JpegFrame.Unknown; // the end, or a scan before any frame
                if (m is 0x01 or (>= 0xD0 and <= 0xD7)) { pos += 2; continue; } // markers without a length
                pos += 2 + ((b[2] << 8) | b[3]);
            }
        }
        catch (IOException) { }
        return JpegFrame.Unknown;
    }

    // Where the next 0xFF that starts a marker is, within JpegMaxStray bytes of from; -1 when there is none.
    static long NextMarker(JpegReadAt read, long from)
    {
        var buffer = new byte[JpegMaxStray + 1];
        int n = read(from, buffer);
        for (int i = 0; i + 1 < n; i++)
            if (buffer[i] == 0xFF && buffer[i + 1] != 0x00) return from + i;
        return -1;
    }

    /// <summary>
    /// Whether a picture is decoded scaled while it is read (and so is not a full-frame decode, which the 64- and
    /// 400-megapixel limits govern): only a single-frame file whose decoder says its container is JPEG
    /// (<paramref name="containerIsJpeg"/>: the decoder's word, not the extension) and whose first frame marker was
    /// positively found to be baseline or extended. Anything unclear needs the whole frame, so the limits apply: a
    /// progressive JPEG's codec holds every coefficient first (3 KB claiming 65500 x 65500 took 24 s and 5.8 GB).
    /// </summary>
    public static bool ScaledWhileRead(bool containerIsJpeg, uint frames, JpegFrame frame)
        => containerIsJpeg && frames == 1 && frame == JpegFrame.Baseline;

    static bool IsOwnExtractor(string ext)
        => Vector.Contains(ext) || OtherOwn.Contains(ext) || Office.Contains(ext) || OldOffice.Contains(ext) || Fonts.Contains(ext)
            || Text.Contains(ext);

    /// <summary>The extension without its dot, lower-case; empty when there is none.</summary>
    internal static string Ext(string extensionOrPath)
    {
        if (string.IsNullOrEmpty(extensionOrPath)) return "";
        var s = extensionOrPath.AsSpan();
        int slash = s.LastIndexOfAny('\\', '/');
        if (slash >= 0) s = s[(slash + 1)..];
        int dot = s.LastIndexOf('.');
        // A bare word ("jpg") is an extension; a file name without a dot ("README" given as a path) has none.
        if (dot >= 0) s = s[(dot + 1)..];
        else if (slash >= 0) return "";
        return s.ToString().ToLowerInvariant();
    }

    static HashSet<string> Set(params string[] items) => new(items, StringComparer.Ordinal);

    static HashSet<string> Union(HashSet<string> a, HashSet<string> b)
    {
        var all = new HashSet<string>(a, StringComparer.Ordinal);
        all.UnionWith(b);
        return all;
    }
}

/// <summary>What kind of stored thumbnail a file gives, for the space estimate (<see cref="ThumbKinds.CostOf"/>).</summary>
public enum ThumbCost : byte
{
    /// <summary>Never a picture (the icon is shown): costs nothing.</summary>
    Nothing,
    /// <summary>A photo kind, stored as JPEG.</summary>
    Photo,
    /// <summary>A video, stored as JPEG.</summary>
    Video,
    /// <summary>A flat kind (PDF, Office, fonts, vector, flat pictures), stored as PNG.</summary>
    Document,
}

/// <summary>What a JPEG's first frame marker says about decoding it (<see cref="ThumbKinds.JpegFrameOf"/>).</summary>
public enum JpegFrame : byte
{
    /// <summary>The markers don't make it clear (or it is no JPEG): treated as needing the whole frame.</summary>
    Unknown,
    /// <summary>A baseline or extended frame (SOF0, SOF1): its codec scales it while reading.</summary>
    Baseline,
    /// <summary>Any other frame (progressive, lossless, hierarchical, arithmetic): decoded whole first.</summary>
    WholeFrame,
}

/// <summary>Reads up to <c>buffer.Length</c> bytes of a file at <paramref name="offset"/>; the bytes read (fewer at the end).</summary>
public delegate int JpegReadAt(long offset, Span<byte> buffer);

/// <summary>What a full-frame decode may do (<see cref="ThumbKinds.AdmitDecode"/>).</summary>
public enum DecodeAdmission : byte
{
    /// <summary>Small enough to decode on any decode thread.</summary>
    Run,
    /// <summary>Over 64 megapixels: wait for the one large-decode gate.</summary>
    LargeGate,
    /// <summary>Over 400 megapixels (or nonsense sizes): don't decode; ask Windows' extractor, then give up.</summary>
    Refused,
}

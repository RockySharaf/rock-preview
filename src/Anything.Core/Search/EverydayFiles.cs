namespace Anything.Core.Search;

/// <summary>
/// The files people open and look at, chosen by kind in Settings › Thumbnails: pictures (camera raw too), videos, music,
/// PDFs, documents, e-books, design and 3D files, fonts and the common archives, plus any extensions added there. What
/// is left out is code, settings, logs, program and system files, and extensions hardly anyone has: the "Everyday files"
/// toggle hides those from Thumbnails ("every file in" a folder, and searches).
/// </summary>
public sealed class EverydayFiles
{
    /// <summary>A kind Settings offers: <see cref="Key"/> is saved, <see cref="Examples"/> is shown under its name.</summary>
    public sealed record Kind(string Key, string Name, string Examples, IReadOnlyList<string> Extensions);

    /// <summary>
    /// Every kind, all on until changed. Extensions are lower case without the dot. Ones that are also code (.ts, .key,
    /// .dot, .obj) are left out; .mts stays, for camcorder videos (as in the video: macro), though TypeScript uses it too.
    /// </summary>
    public static IReadOnlyList<Kind> Kinds { get; } =
    [
        // jxr, wdp, hdp: JPEG XR, Windows' HDR screenshots
        new("pictures", "Pictures", "JPG, PNG, HEIC, WebP, GIF",
            ["jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "webp", "heic", "heif", "avif", "jxl", "jxr", "wdp",
             "hdp", "tif", "tiff", "ico", "tga", "exr", "hdr", "wmf", "emf", "pcx"]),
        new("raw", "Camera raw photos", "CR3, NEF, ARW, DNG",
            ["cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "dng", "orf", "rw2", "raf", "pef", "srw", "3fr", "erf",
             "kdc", "mrw", "x3f", "rwl", "iiq"]),
        new("videos", "Videos", "MP4, MOV, MKV, AVI",
            ["mp4", "m4v", "mov", "avi", "mkv", "webm", "wmv", "flv", "f4v", "mpg", "mpeg", "mpe", "m2ts", "mts", "m2t",
             "3gp", "3g2", "ogv", "vob", "asf", "rm", "rmvb", "divx", "mxf"]),
        new("music", "Music and audio", "MP3, M4A, FLAC, WAV",
            ["mp3", "m4a", "m4b", "aac", "flac", "wav", "wma", "ogg", "oga", "opus", "aif", "aiff", "aifc", "mid", "midi",
             "amr", "ape", "wv", "dsf", "dff", "mka", "caf"]),
        new("pdf", "PDFs", "PDF and XPS", ["pdf", "xps", "oxps"]),
        new("documents", "Documents", "Word, Excel, PowerPoint, OneNote, TXT, CSV",
            ["doc", "docx", "docm", "dotx", "odt", "rtf", "txt", "wpd", "wps", "pages", "xls", "xlsx", "xlsm", "xlsb", "ods",
             "csv", "numbers", "ppt", "pptx", "pptm", "pps", "ppsx", "odp", "one", "vsd", "vsdx", "pub"]),
        new("ebooks", "E-books and comics", "EPUB, MOBI, AZW3, CBZ", ["epub", "mobi", "azw", "azw3", "fb2", "djvu", "cbz", "cbr"]),
        new("mail", "Saved emails", "EML and Outlook MSG", ["eml", "msg"]),
        new("design", "Design files", "PSD, AI, SVG, Figma, Affinity, Procreate",
            ["psd", "psb", "ai", "eps", "svg", "svgz", "indd", "indt", "idml", "sketch", "fig", "xd", "afdesign", "afphoto",
             "afpub", "cdr", "kra", "xcf", "clip", "procreate"]),
        new("3d", "3D and CAD", "Blender, STL, FBX, GLB, DWG",
            ["blend", "fbx", "stl", "3mf", "glb", "gltf", "usdz", "dwg", "dxf", "skp", "c4d"]),
        new("fonts", "Fonts", "TTF and OTF", ["ttf", "otf"]),
        new("archives", "Archives", "ZIP, RAR and 7z", ["zip", "rar", "7z"]),
    ];

    /// <summary>Every kind and nothing added: Everyday files until Settings changes it.</summary>
    public static EverydayFiles Default { get; } = new(null, null);

    readonly HashSet<string> _set;

    /// <param name="kinds">The <see cref="Kind.Key"/>s that are on (unknown ones are ignored); null = all.</param>
    /// <param name="added">More extensions, with or without the dot, any case; blanks are ignored.</param>
    public EverydayFiles(IEnumerable<string>? kinds, IEnumerable<string>? added)
    {
        var on = kinds is null ? null : new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase);
        KindsOn = Kinds.Where(k => on is null || on.Contains(k.Key)).Select(k => k.Key).ToList();
        Added = (added ?? []).Select(Clean).Where(e => e.Length > 0).Distinct().ToList();
        var list = Kinds.Where(k => KindsOn.Contains(k.Key)).SelectMany(k => k.Extensions).Concat(Added).Distinct().ToList();
        Extensions = list;
        _set = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        // With nothing chosen nothing is everyday: "ext:" alone would keep the files that have no extension.
        Filter = list.Count == 0 ? "file: !file:" : "ext:" + string.Join(";", list);
    }

    /// <summary>The keys of the kinds that are on, in <see cref="Kinds"/>' order.</summary>
    public IReadOnlyList<string> KindsOn { get; }

    /// <summary>The extensions added by hand, cleaned (lower case, no dot), each once.</summary>
    public IReadOnlyList<string> Added { get; }

    /// <summary>Every everyday extension: the kinds' that are on, then the added ones; lower case, without the dot, once each.</summary>
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>A search filter term that keeps only these files: <c>ext:jpg;jpeg;...</c>.</summary>
    public string Filter { get; }

    /// <summary>
    /// Whether a file name ends in one of the extensions (any case). The extension is the one the index finds
    /// (<see cref="FileIndex.Extension"/>): a name whose only dot comes first (".png") has none.
    /// </summary>
    public bool IsEveryday(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 && dot < fileName.Length - 1 && _set.GetAlternateLookup<ReadOnlySpan<char>>().Contains(fileName[(dot + 1)..]);
    }

    /// <summary>Extensions typed in a box: separated by spaces, commas or semicolons, with or without "*." or "."; once each.</summary>
    public static IReadOnlyList<string> ParseExtensions(string text)
        => text.Split([' ', ',', ';', '|', '	'], StringSplitOptions.RemoveEmptyEntries).Select(Clean).Where(e => e.Length > 0).Distinct().ToList();

    /// <summary>"*.MD", ".md", " md " → "md". Separators and wildcards inside are dropped, so an entry stays one extension.</summary>
    static string Clean(string extension)
    {
        string e = extension.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant();
        return new string(e.Where(c => !char.IsWhiteSpace(c) && c is not (';' or '|' or '*' or '?' or '"' or '<' or '>' or '!')).ToArray());
    }
}

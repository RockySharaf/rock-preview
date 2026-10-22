namespace Anything.Core.Search;

/// <summary>
/// The files people open and look at: pictures (camera raw too), videos, music, documents and PDFs, e-books, design
/// and 3D files, fonts and the common archives. What is left out is code, settings, logs, program and system files,
/// and extensions hardly anyone has: the "Everyday files" toggle hides those from "every file in" a folder.
/// </summary>
public static class EverydayFiles
{
    /// <summary>
    /// The extensions, lower case without the dot. Ones that are also code (.ts, .key, .dot, .obj) are left out; .mts
    /// stays, for camcorder videos (as in the video: macro), though TypeScript uses it too.
    /// </summary>
    public static IReadOnlyList<string> Extensions { get; } =
    [
        // pictures (jxr, wdp, hdp: JPEG XR, Windows' HDR screenshots)
        "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "webp", "heic", "heif", "avif", "jxl", "jxr", "wdp",
        "hdp", "tif", "tiff", "ico", "tga", "exr", "hdr", "wmf", "emf", "pcx",
        // camera raw
        "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "dng", "orf", "rw2", "raf", "pef", "srw", "3fr", "erf",
        "kdc", "mrw", "x3f", "rwl", "iiq",
        // design, 3D and fonts
        "psd", "psb", "ai", "eps", "svg", "svgz", "indd", "indt", "idml", "sketch", "fig", "xd", "afdesign", "afphoto",
        "afpub", "cdr", "kra", "xcf", "clip", "procreate", "blend", "fbx", "stl", "3mf", "glb", "gltf", "usdz", "dwg",
        "dxf", "skp", "c4d", "ttf", "otf",
        // video
        "mp4", "m4v", "mov", "avi", "mkv", "webm", "wmv", "flv", "f4v", "mpg", "mpeg", "mpe", "m2ts", "mts", "m2t",
        "3gp", "3g2", "ogv", "vob", "asf", "rm", "rmvb", "divx", "mxf",
        // music
        "mp3", "m4a", "m4b", "aac", "flac", "wav", "wma", "ogg", "oga", "opus", "aif", "aiff", "aifc", "mid", "midi",
        "amr", "ape", "wv", "dsf", "dff", "mka", "caf",
        // documents, e-books and mail
        "pdf", "doc", "docx", "docm", "dotx", "odt", "rtf", "txt", "wpd", "wps", "pages", "xls", "xlsx", "xlsm", "xlsb",
        "ods", "csv", "numbers", "ppt", "pptx", "pptm", "pps", "ppsx", "odp", "xps", "oxps", "one", "vsd", "vsdx", "pub",
        "epub", "mobi", "azw", "azw3", "fb2", "djvu", "cbz", "cbr", "eml", "msg",
        // archives
        "zip", "rar", "7z",
    ];

    static readonly HashSet<string> s_set = new(Extensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>A search filter term that keeps only these files: <c>ext:jpg;jpeg;...</c>.</summary>
    public static string Filter { get; } = "ext:" + string.Join(";", Extensions);

    /// <summary>
    /// Whether a file name ends in one of the extensions (any case). The extension is the one the index finds
    /// (<see cref="FileIndex.Extension"/>): a name whose only dot comes first (".png") has none.
    /// </summary>
    public static bool IsEveryday(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot > 0 && dot < fileName.Length - 1 && s_set.GetAlternateLookup<ReadOnlySpan<char>>().Contains(fileName[(dot + 1)..]);
    }
}

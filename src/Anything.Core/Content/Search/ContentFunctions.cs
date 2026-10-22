namespace Anything.Core.Content.Search;

/// <summary>
/// Function and restriction aliases → canonical names, added to QueryParser's tables by its static constructor. Everything
/// 1.4's content: aliases all mean content: (every format is decoded on extraction); the picture and property names follow
/// Everything 1.4/1.5 where it has one (dashes are stripped by the parser, so date-taken: is datetaken:), plus our own
/// dpi:, colormode:, camera:, contentstatus: and the cmyk:, rgb: and gps: restrictions.
/// </summary>
internal static class ContentFunctions
{
    /// <summary>Words inside files (content:).</summary>
    public const string Content = "content";
    /// <summary>Why a file's words are or aren't searchable (contentstatus:).</summary>
    public const string Status = "contentstatus";
    /// <summary>Pages, slides or sheets (pagecount:).</summary>
    public const string PageCount = "pagecount";
    /// <summary>The camera's make or model (camera:).</summary>
    public const string Camera = "camera";

    static readonly KeyValuePair<string, string>[] Functions =
    [
        new("content", Content), new("ansicontent", Content), new("contenta", Content), new("utf8content", Content),
        new("utf16content", Content), new("utf16lecontent", Content), new("utf16becontent", Content),
        new("unicodecontent", Content), new("wcharcontent", Content), new("contentw", Content),
        new("width", "width"), new("wide", "width"),
        new("height", "height"), new("high", "height"),
        new("dimensions", "dimensions"), new("dimension", "dimensions"),
        new("orientation", "orientation"),
        new("bitdepth", "bitdepth"), new("bitsperpixel", "bitdepth"), new("bpp", "bitdepth"),
        new("dpi", "dpi"),
        new("horizontalresolution", "horizontalresolution"), new("verticalresolution", "verticalresolution"),
        new("colormode", "colormode"),
        new("colorspace", "colorspace"), new("colorrepresentation", "colorspace"),
        new("datetaken", "datetaken"),
        new("rating", "rating"),
        new("cameramake", "cameramake"), new("cameramaker", "cameramake"),
        new("cameramodel", "cameramodel"),
        new("camera", Camera),
        new("keywords", "keywords"), new("keyword", "keywords"), new("tags", "keywords"), new("tag", "keywords"),
        new("title", "title"),
        new("subject", "subject"),
        new("author", "author"), new("authors", "author"),
        new("comment", "comment"), new("description", "comment"),
        // Saved emails: the sender (author: finds it too) and the recipients (To and Cc).
        new("from", "from"), new("sender", "from"),
        new("to", "to"), new("recipients", "to"),
        new("pagecount", PageCount),
        new("contentstatus", Status),
    ];

    static readonly KeyValuePair<string, string>[] Restrictions = [new("cmyk", "cmyk"), new("rgb", "rgb"), new("gps", "gps")];

    /// <summary>Text fields by canonical function name.</summary>
    static readonly Dictionary<string, DocField> FieldNames = new(StringComparer.Ordinal)
    {
        ["title"] = DocField.Title, ["subject"] = DocField.Subject, ["author"] = DocField.Author, ["keywords"] = DocField.Keywords,
        ["comment"] = DocField.Comment, ["cameramake"] = DocField.CameraMake, ["cameramodel"] = DocField.CameraModel,
        [Camera] = DocField.CameraMake, ["from"] = DocField.Sender, ["to"] = DocField.Recipients,
    };

    /// <summary>Picture properties by canonical function or restriction name.</summary>
    static readonly Dictionary<string, PictureProp> PictureNames = new(StringComparer.Ordinal)
    {
        ["width"] = PictureProp.Width, ["height"] = PictureProp.Height, ["dimensions"] = PictureProp.Dimensions,
        ["orientation"] = PictureProp.Orientation, ["bitdepth"] = PictureProp.BitDepth, ["dpi"] = PictureProp.Dpi,
        ["horizontalresolution"] = PictureProp.DpiX, ["verticalresolution"] = PictureProp.DpiY,
        ["colormode"] = PictureProp.ColorMode, ["cmyk"] = PictureProp.ColorMode, ["rgb"] = PictureProp.ColorMode,
        ["colorspace"] = PictureProp.ExifColorSpace, ["datetaken"] = PictureProp.DateTaken, ["gps"] = PictureProp.Gps,
        ["rating"] = PictureProp.Rating,
    };

    static readonly HashSet<string> Canonical = BuildCanonical();

    static HashSet<string> BuildCanonical()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, c) in Functions) set.Add(c);
        foreach (var (_, c) in Restrictions) set.Add(c);
        return set;
    }

    /// <summary>content…, width…, title…, contentstatus … (alias → canonical).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> FunctionAliases => Functions;
    /// <summary>cmyk, rgb, gps (alias → canonical).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> RestrictionAliases => Restrictions;
    /// <summary>A canonical name handled here.</summary>
    public static bool IsContentName(string canonical) => Canonical.Contains(canonical);

    /// <summary>The field a text-field function searches (camera: searches the make and the model).</summary>
    internal static DocField? FieldOf(string canonical) => FieldNames.TryGetValue(canonical, out var f) ? f : null;

    /// <summary>The picture property a picture function or restriction compares.</summary>
    internal static PictureProp? PictureOf(string canonical) => PictureNames.TryGetValue(canonical, out var p) ? p : null;

    /// <summary>
    /// The least mode a function needs: pictures' details (and the camera fields, which only pictures and photo files
    /// carry) need DocumentsAndPictures; words, other fields, page counts and statuses need Documents.
    /// </summary>
    internal static ContentMode NeedsMode(string canonical)
        => PictureNames.ContainsKey(canonical) || canonical is "cameramake" or "cameramodel" or Camera
            ? ContentMode.DocumentsAndPictures
            : ContentMode.Documents;
}

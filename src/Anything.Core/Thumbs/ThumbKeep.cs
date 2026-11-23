using Anything.Core.Search;

namespace Anything.Core.Thumbs;

/// <summary>The groups the user ticks under Settings › Instant thumbnails › "Keep thumbnails of".</summary>
public enum ThumbKeepGroup : byte
{
    /// <summary>A kind never kept on disk (<see cref="ThumbKinds.KeepOnDisk"/> is false): no box covers it.</summary>
    None,
    /// <summary>Photos, camera raw, PSD and flat pictures (png, gif, bmp, ico).</summary>
    Pictures,
    /// <summary>Video frames.</summary>
    Videos,
    /// <summary>PDF, Office, fonts and design files (Illustrator, CorelDRAW, AutoCAD and the like).</summary>
    Documents,
}

/// <summary>
/// Which thumbnails "Instant thumbnails" keeps on disk, as the user chose: the groups ticked and the file types never
/// kept. Asked in addition to <see cref="ThumbKinds.KeepOnDisk"/>, never instead of it: a kind that rule refuses is never
/// kept, whatever is ticked. Immutable and thread-safe; <see cref="All"/> (everything ticked, nothing left out) unless
/// the user changes it. Stored in RockPreview.ini as thumbnail_store_kinds ("pictures,videos,documents"; missing means
/// all three) and thumbnail_store_skip ("pdf psd": lower case, no dots, space separated).
/// </summary>
public sealed class ThumbKeep : IEquatable<ThumbKeep>
{
    const string PicturesKey = "pictures", VideosKey = "videos", DocumentsKey = "documents";

    readonly HashSet<string> _skip;
    readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _skipLookup;

    /// <summary>Every group ticked and no type left out: what a new install keeps.</summary>
    public static ThumbKeep All { get; } = new(true, true, true, []);

    public ThumbKeep(bool pictures, bool videos, bool documents, IEnumerable<string> skipped)
    {
        Pictures = pictures;
        Videos = videos;
        Documents = documents;
        Skipped = ParseExtensions(string.Join(" ", skipped));
        _skip = new HashSet<string>(Skipped, StringComparer.OrdinalIgnoreCase);
        _skipLookup = _skip.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public bool Pictures { get; }
    public bool Videos { get; }
    public bool Documents { get; }

    /// <summary>The file types never kept: lower case, without dots, each once, in the order typed.</summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <summary>No group ticked: nothing new is saved.</summary>
    public bool KeepsNothing => !Pictures && !Videos && !Documents;

    /// <summary>Whether a group is ticked (None never is).</summary>
    public bool Has(ThumbKeepGroup group) => group switch
    {
        ThumbKeepGroup.Pictures => Pictures,
        ThumbKeepGroup.Videos => Videos,
        ThumbKeepGroup.Documents => Documents,
        _ => false,
    };

    /// <summary>
    /// Which box covers a file of this kind: the space estimate's groups (<see cref="ThumbKinds.CostOf"/>), except that
    /// flat pictures (png, gif, bmp, ico), which the estimate prices as documents, are pictures to the user.
    /// </summary>
    public static ThumbKeepGroup GroupOf(string extensionOrPath, bool isFolder = false)
        => GroupFor(ThumbKinds.CostOf(extensionOrPath, isFolder), ThumbKinds.Ext(extensionOrPath));

    static ThumbKeepGroup GroupFor(ThumbCost cost, ReadOnlySpan<char> ext) => cost switch
    {
        ThumbCost.Photo => ThumbKeepGroup.Pictures,
        ThumbCost.Video => ThumbKeepGroup.Videos,
        ThumbCost.Document => ThumbKinds.IsFlatPicture(ext) ? ThumbKeepGroup.Pictures : ThumbKeepGroup.Documents,
        _ => ThumbKeepGroup.None,
    };

    /// <summary>
    /// Whether the user's choice keeps this file's thumbnail: its group ticked and its type not left out. Callers ask
    /// <see cref="ThumbKinds.KeepOnDisk"/> as well; a kind that rule refuses has no group, so it is never kept here either.
    /// </summary>
    public bool Keeps(string extensionOrPath, bool isFolder = false)
    {
        if (!Has(GroupOf(extensionOrPath, isFolder))) return false;
        return _skip.Count == 0 || !_skip.Contains(ThumbKinds.Ext(extensionOrPath));
    }

    /// <summary><see cref="Keeps"/> for a bare extension whose cost is known, without allocating: the index walk.</summary>
    internal bool KeepsExtension(ReadOnlySpan<char> extension, ThumbCost cost)
        => Has(GroupFor(cost, extension)) && (_skip.Count == 0 || !_skipLookup.Contains(extension));

    /// <summary>
    /// Types as a person types them: separated by spaces, commas or semicolons, with or without the dot, any case
    /// (".PDF,pdf ;psd" gives pdf, psd). Each once, in the order typed.
    /// </summary>
    public static IReadOnlyList<string> ParseExtensions(string? text) => EverydayFiles.ParseExtensions(text ?? "");

    /// <summary>The choice from the ini: the groups listed (missing: all three; empty: none) and the types left out.</summary>
    public static ThumbKeep FromIni(string? kinds, string? skip)
    {
        if (kinds is null) return new ThumbKeep(true, true, true, ParseExtensions(skip));
        var words = kinds.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Listed(string key) => words.Any(w => w.Equals(key, StringComparison.OrdinalIgnoreCase));
        return new ThumbKeep(Listed(PicturesKey), Listed(VideosKey), Listed(DocumentsKey), ParseExtensions(skip));
    }

    /// <summary>thumbnail_store_kinds: "pictures,videos,documents", the ticked ones only ("" for none).</summary>
    public string KindsIni => string.Join(",", new[] { (Pictures, PicturesKey), (Videos, VideosKey), (Documents, DocumentsKey) }
        .Where(k => k.Item1).Select(k => k.Item2));

    /// <summary>thumbnail_store_skip: "pdf psd".</summary>
    public string SkipIni => string.Join(" ", Skipped);

    public bool Equals(ThumbKeep? other)
        => other is not null && Pictures == other.Pictures && Videos == other.Videos && Documents == other.Documents
           && Skipped.SequenceEqual(other.Skipped);

    public override bool Equals(object? obj) => Equals(obj as ThumbKeep);

    public override int GetHashCode() => HashCode.Combine(Pictures, Videos, Documents, SkipIni);
}

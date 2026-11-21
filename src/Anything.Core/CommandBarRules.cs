namespace Anything.Core;

/// <summary>
/// Rules for the command bar and status bar over the Explorer view that need no view, so they can be tested: which
/// layout the View menu marks and its keys, which way a new sort goes, which files get "Extract all", what a selection
/// adds up to, and where a new folder can be made.
/// </summary>
public static class CommandBarRules
{
    /// <summary>FOLDERVIEWMODE values (ShObjIdl_core.h) that the View menu's layouts use.</summary>
    public const int FvmIcon = 1, FvmSmallIcon = 2, FvmList = 3, FvmDetails = 4, FvmThumbnail = 5, FvmTile = 6, FvmContent = 8;

    /// <summary>
    /// File Explorer's eight layouts in its View menu's order (Extra large icons to Content): the view mode and icon size
    /// each one sets. Ctrl+Shift+1 to Ctrl+Shift+8 pick them in this order.
    /// </summary>
    public static readonly IReadOnlyList<(int Mode, int IconSize)> Layouts =
    [
        (FvmIcon, 256), (FvmIcon, 96), (FvmIcon, 48), (FvmSmallIcon, 16),
        (FvmList, 16), (FvmDetails, 16), (FvmTile, 48), (FvmContent, 32),
    ];

    /// <summary>
    /// Which of <see cref="Layouts"/> the view is in, from its mode and icon size, or -1 for none. Icons zoomed with
    /// Ctrl+wheel to a size between two layouts count as the larger layout they have reached (72px is Medium icons).
    /// </summary>
    public static int LayoutIndex(int mode, int iconSize) => mode switch
    {
        FvmIcon or FvmThumbnail => iconSize >= 256 ? 0 : iconSize >= 96 ? 1 : iconSize >= 48 ? 2 : 3,
        FvmSmallIcon => 3,
        FvmList => 4,
        FvmDetails => 5,
        FvmTile => 6,
        FvmContent => 7,
        _ => -1,
    };

    /// <summary>The layout a Ctrl+Shift+digit picks (1 = Extra large icons ... 8 = Content), or -1 for another digit.</summary>
    public static int LayoutForDigit(int digit) => digit is >= 1 and <= 8 ? digit - 1 : -1;

    /// <summary>The shortcut shown next to a layout in the View menu ("Ctrl+Shift+6" for Details).</summary>
    public static string LayoutShortcut(int index) => $"Ctrl+Shift+{index + 1}";

    /// <summary>
    /// Which way the view sorts after a column is chosen in the Sort menu, as File Explorer's own Sort by does: a new
    /// column starts in its own default direction (newest first for dates, largest first for sizes) whatever the old
    /// column's was; choosing the column already sorted on turns its direction round.
    /// </summary>
    public static bool SortDescending(bool sameColumn, bool currentDescending, bool columnDefaultDescending)
        => sameColumn ? !currentDescending : columnDefaultDescending;

    /// <summary>
    /// The kinds of archive Windows' own "Extract all" handles: zip always (its Compressed Folder); the rest since Windows
    /// 11 added archive folders (checked against this Windows: each of these, and not .cab or .iso, has the "extract" verb).
    /// </summary>
    static readonly string[] s_archiveFolderExtensions = [".7z", ".rar", ".tar", ".tgz", ".gz", ".bz2", ".tbz2", ".xz", ".txz", ".zst", ".tzst"];

    /// <summary>
    /// Whether a file of this name can be extracted with Windows' "Extract all": a .zip, or (when this Windows has archive
    /// folders, <paramref name="archiveFolders"/>) a .7z, .rar, .tar and the like. From the name alone, so no disk.
    /// </summary>
    public static bool CanExtract(string? name, bool archiveFolders)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return true;
        return archiveFolders && s_archiveFolderExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The archive a location is inside ("C:\x\Photos.zip\2024" is inside "C:\x\Photos.zip", as is the archive itself),
    /// or null: the archive "Extract all" works on while its contents are open. From the location's parsing name.
    /// </summary>
    public static string? ArchiveContaining(string? location, bool archiveFolders)
    {
        if (string.IsNullOrEmpty(location) || location.StartsWith("::", StringComparison.Ordinal)) return null;
        int start = location.StartsWith(@"\\", StringComparison.Ordinal) ? 2 : 0;
        int first = location.IndexOf('\\', start); // the drive ("C:") or, for a share, the server is never the archive
        if (first < 0) return null;
        for (int i = first + 1; i <= location.Length; i++)
        {
            if (i < location.Length && location[i] != '\\') continue;
            string part = location[..i];
            int slash = part.LastIndexOf('\\');
            if (slash < part.Length - 1 && CanExtract(part[(slash + 1)..], archiveFolders)) return part;
        }
        return null;
    }

    /// <summary>
    /// Whether a new folder can be made where the view is: a folder on disk (it has a file-system path) or a library
    /// (Documents, Pictures..., which puts it in its save folder). Not This PC, Network, Home and the like.
    /// </summary>
    public static bool CanMakeFolderIn(string? fileSystemPath, string? parsingName)
        => !string.IsNullOrEmpty(fileSystemPath)
           || parsingName is { } p && p.EndsWith(".library-ms", StringComparison.OrdinalIgnoreCase);

    /// <summary>Above this many selected items the status bar shows the count only, without adding up their sizes.</summary>
    public const int SelectionSizeLimit = 10_000;

    /// <summary>
    /// What the selected items add up to for the status bar ("5 items selected  12.3 MB"), as File Explorer shows it:
    /// nothing once a folder is among them (Explorer shows no size then), nor when no item has a size. A zip counts as a
    /// file. Fed one item at a time, so a reader can stop early.
    /// </summary>
    public sealed class SelectionTotal
    {
        long _bytes;
        bool _sized;

        /// <summary>A folder was added: the selection gets no size.</summary>
        public bool HasFolder { get; private set; }

        public void Add(bool folder, long? size)
        {
            if (folder) { HasFolder = true; return; }
            if (size is not { } s || s < 0) return;
            _bytes += s;
            _sized = true;
        }

        /// <summary>The total in bytes, or null when the status bar shows the count only.</summary>
        public long? Bytes => HasFolder || !_sized ? null : _bytes;
    }
}

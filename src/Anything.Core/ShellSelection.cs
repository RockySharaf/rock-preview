namespace Anything.Core;

/// <summary>
/// Rules for the Explorer view's selection that need no view, so they can be tested: when the selection poll has to
/// read the whole selection again, and when a selection is folders a search can look inside.
/// </summary>
public static class ShellSelection
{
    /// <summary>Up to this many selected items, the poll reads the selection every tick: cheap, and it sees the first item renamed.</summary>
    public const int FullReadLimit = 256;

    /// <summary>
    /// Whether the selection poll can keep the first item it found last time instead of reading the selection again,
    /// which copies every selected item (100,000 after Ctrl+A in a big folder, five times a second). Only for a big
    /// selection whose count and focused item are as they were, and that the view hasn't reported changed since. A
    /// count or focus of -1 is unknown. The kept item's name is still read again each tick (<see cref="TryRefreshFirst"/>).
    /// </summary>
    public static bool CanKeepFirst(int selected, int focused, bool reportedChanged, int lastSelected, int lastFocused)
        => selected > FullReadLimit && selected == lastSelected && focused == lastFocused && !reportedChanged;

    /// <summary>Reads the view's item at an index: false when it can't be read or isn't selected; the path is null for a virtual item.</summary>
    public delegate bool SelectedItemPath(int index, out string? path);

    /// <summary>
    /// Where the selection array's first item (<paramref name="first"/> is its path) sits in the view, so that while a big
    /// selection is kept the poll can read just that item again. -1 when it isn't where DefView puts it; the poll then
    /// keeps the path it has.
    /// </summary>
    public static int IndexOfFirst(string? first, int focused, Func<int> firstSelected, SelectedItemPath read)
        => FirstIndex(focused, firstSelected, read, out var path) is var i and >= 0
            && string.Equals(path, first, StringComparison.OrdinalIgnoreCase) ? i : -1;

    /// <summary>
    /// A kept big selection's first item read again from <paramref name="firstIndex"/>: one item, cheap, and a rename (F2,
    /// another program) keeps the count, the focus and the view's silence, so nothing else would show the new name. False
    /// when the item DefView lists first is no longer there or can't be read: the poll then reads the whole selection
    /// again. An index of -1 (not found at the last full read) keeps <paramref name="kept"/>.
    /// </summary>
    public static bool TryRefreshFirst(int firstIndex, string? kept, int focused, Func<int> firstSelected, SelectedItemPath read, out string? first)
    {
        if (firstIndex < 0) { first = kept; return true; }
        if (FirstIndex(focused, firstSelected, read, out first) == firstIndex) return true;
        first = null;
        return false;
    }

    /// <summary>
    /// The view index of the item DefView lists first in the selection array, and its path: the focused item when it is
    /// selected, else the first selected one (asked for only then). -1 when neither can be read.
    /// </summary>
    static int FirstIndex(int focused, Func<int> firstSelected, SelectedItemPath read, out string? path)
    {
        if (focused >= 0 && read(focused, out path)) return focused;
        int i = firstSelected();
        if (i >= 0 && read(i, out path)) return i;
        path = null;
        return -1;
    }

    /// <summary>SFGAO_* bits (ShObjIdl_core.h) that tell a folder on disk from a file.</summary>
    public const uint SfgaoFolder = 0x20000000, SfgaoFileSystem = 0x40000000, SfgaoStream = 0x00400000;

    /// <summary>
    /// Whether the selected items are all folders on disk, from their shell attributes: <paramref name="all"/> combined
    /// with AND (bits every item has) and <paramref name="any"/> with OR (bits at least one has). The shell calls a zip a
    /// folder too, but it is also a stream, and only OR shows one zip among folders.
    /// </summary>
    public static bool AreFolders(uint all, uint any)
        => (all & (SfgaoFolder | SfgaoFileSystem)) == (SfgaoFolder | SfgaoFileSystem) && (any & SfgaoStream) == 0;
}

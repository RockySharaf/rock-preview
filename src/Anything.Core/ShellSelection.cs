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
    /// count or focus of -1 is unknown.
    /// </summary>
    public static bool CanKeepFirst(int selected, int focused, bool reportedChanged, int lastSelected, int lastFocused)
        => selected > FullReadLimit && selected == lastSelected && focused == lastFocused && !reportedChanged;

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

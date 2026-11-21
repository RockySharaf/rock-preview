namespace Anything.Core;

/// <summary>What a middle-click in the navigation pane does.</summary>
public enum MiddleClick
{
    /// <summary>Not a middle-click on a row: the pane does what it always does.</summary>
    None,
    /// <summary>The row opens in a new tab.</summary>
    OpenInNewTab,
    /// <summary>The second click of a middle double-click: taken, so it neither opens a second tab nor selects the row.</summary>
    Ignore,
}

/// <summary>
/// Rules for opening folders from the folder view and the navigation pane, kept apart from the windows so they can be
/// tested: which items a tab can open, which menu item is the shell's own "Open in new tab", which clicks in the pane
/// open a tab, which of the shell's extra roots (cloud storage, a linked phone) the pane adds, and a check that may wait
/// on a slow disk without holding up the window for long.
/// </summary>
public static class FolderTabs
{
    /// <summary>SFGAO_LINK (ShObjIdl_core.h): the item is a shortcut.</summary>
    public const uint SfgaoLink = 0x00010000;

    /// <summary>
    /// A place the app can show in a tab: a folder (a drive, This PC, a library, a phone too), not a zip or cab file the
    /// shell also calls a folder.
    /// </summary>
    public static bool CanOpenInTab(uint attributes)
        => (attributes & ShellSelection.SfgaoFolder) != 0 && (attributes & ShellSelection.SfgaoStream) == 0;

    /// <summary>One item selected and it is a shortcut: the one whose target a double-click can open in the tab.</summary>
    public static bool IsSingleShortcut(int count, uint attributes) => count == 1 && (attributes & SfgaoLink) != 0;

    /// <summary>A menu item's words as they read: without the access key's '&amp;', a shortcut key after a tab, or a trailing "...".</summary>
    public static string MenuText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string t = text.Replace("&&", "\u0001").Replace("&", "").Replace('\u0001', '&');
        int tab = t.IndexOf('\t');
        if (tab >= 0) t = t[..tab];
        return t.Trim().TrimEnd('.', '…', ' ');
    }

    /// <summary>Whether two menu items read the same (the shell's own "Open in new tab" beside ours, in the user's language).</summary>
    public static bool SameMenuText(string? a, string? b)
    {
        string x = MenuText(a);
        return x.Length > 0 && x.Equals(MenuText(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The shell's verb for File Explorer's own "Open in new tab".</summary>
    public const string ShellNewTabVerb = "opennewtab";

    // _NSTCECLICKTYPE and _NSTCEHITTEST (ShObjIdl.h).
    const uint ClickButton = 0x3, ClickMiddle = 0x2, ClickDouble = 0x4;
    const uint HitOnItem = 0x46, HitOnItemRight = 0x20, HitOnItemButton = 0x10;

    /// <summary>
    /// A click the navigation pane reports (its click type and where it hit): a middle-click on a row opens it in a new
    /// tab; on its expand chevron or empty space it is the pane's as before.
    /// </summary>
    public static MiddleClick ForPaneClick(uint clickType, uint hitTest)
    {
        if ((clickType & ClickButton) != ClickMiddle) return MiddleClick.None;
        if ((hitTest & (HitOnItem | HitOnItemRight)) == 0 || (hitTest & HitOnItemButton) != 0) return MiddleClick.None;
        return (clickType & ClickDouble) != 0 ? MiddleClick.Ignore : MiddleClick.OpenInNewTab;
    }

    /// <summary>
    /// Runs a check that can wait on a disk or a network share (one that doesn't answer can take half a minute) on another
    /// thread: its answer, or null when it hasn't come within <paramref name="wait"/>. The check then carries on by itself
    /// and its answer is dropped. A check that throws answers false.
    /// </summary>
    public static bool? Within(Func<bool> check, TimeSpan wait)
    {
        var task = Task.Run(() =>
        {
            try { return check(); }
            catch (Exception) { return false; }
        });
        return task.Wait(wait) ? task.Result : null;
    }

    // ---- the pane's extra roots ----------------------------------------------------------------

    /// <summary>
    /// The roots the navigation pane adds itself, by their shell class (Home, Quick access, Gallery, OneDrive, This PC,
    /// Network, Linux), and those Explorer can be set to show that the pane leaves out (Libraries, the Recycle Bin, the
    /// user's own folder): none of them is added again as an extra root.
    /// </summary>
    public static readonly IReadOnlySet<Guid> PaneRoots = new HashSet<Guid>
    {
        new("f874310e-b6b7-47dc-bc84-b9e6b38f5903"), // Home
        new("679f85cb-0220-4080-b29b-5540cc05aab6"), // Quick access
        new("e88865ea-0e1c-4e20-9aa6-edcd0212c87c"), // Gallery
        new("018d5c66-4533-4307-9b53-224de2ed1fe6"), // OneDrive
        new("20d04fe0-3aea-1069-a2d8-08002b30309d"), // This PC
        new("f02c1a0d-be21-4350-88b0-7367fc96ef3c"), // Network
        new("208d2c60-3aea-1069-a2d7-08002b30309d"), // Network (its older class)
        new("b2b4a4d1-2754-4140-a2eb-9a76d9d7cdc6"), // Linux
        new("031e4825-7b94-4dc3-b131-e946b44c8dd5"), // Libraries
        new("645ff040-5081-101b-9f08-00aa002f954e"), // Recycle Bin
        new("59031a47-3f72-44a7-89c5-5595fe6b30ee"), // the user's own folder
    };

    /// <summary>
    /// One root registered under Explorer\Desktop\NameSpace (the user's or the machine's): its key (the class, "{...}")
    /// and the values its class has for the user and for the machine: System.IsPinnedToNameSpaceTree (shown in the pane)
    /// and SortOrderIndex (where). A value is null where the class doesn't set it.
    /// </summary>
    public sealed record NameSpaceRoot(string Key, object? UserPinned, object? MachinePinned, object? UserSortOrder, object? MachineSortOrder);

    /// <summary>
    /// The roots File Explorer pins to its pane beside OneDrive that the pane doesn't add itself: other cloud storage
    /// (Dropbox, Google Drive, iCloud, another OneDrive account) and a linked phone. Each class once (the first time it is
    /// listed: the user's before the machine's), in Explorer's order (SortOrderIndex; those without one last, as listed).
    /// The user's value of a class wins over the machine's (a provider unpinned for the user stays out).
    /// </summary>
    public static IReadOnlyList<Guid> ExtraRoots(IEnumerable<NameSpaceRoot> registered, IReadOnlySet<Guid> shown)
    {
        var seen = new HashSet<Guid>();
        var roots = new List<(Guid Clsid, int Order, int Index)>();
        foreach (var root in registered)
        {
            if (!Guid.TryParse(root.Key, out var clsid) || !seen.Add(clsid) || shown.Contains(clsid)) continue;
            if ((Number(root.UserPinned) ?? Number(root.MachinePinned) ?? 0) == 0) continue;
            roots.Add((clsid, Number(root.UserSortOrder) ?? Number(root.MachineSortOrder) ?? int.MaxValue, roots.Count));
        }
        return roots.OrderBy(r => r.Order).ThenBy(r => r.Index).Select(r => r.Clsid).ToList();
    }

    /// <summary>A registry number (a DWORD, or text holding one), or null.</summary>
    static int? Number(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s.Trim(), out int n) => n,
        _ => null,
    };

    /// <summary>
    /// Whether an extra root whose folder is <paramref name="folder"/> (null for one that isn't a folder on disk, like a
    /// phone) can be added: not one on a network share, which may not answer and is never a sync root, and not the
    /// folder of a root already there (OneDrive's own, also when taken out of the pane).
    /// </summary>
    public static bool IsExtraRootFolder(string? folder, IEnumerable<string?> shownFolders)
    {
        if (string.IsNullOrEmpty(folder)) return true;
        if (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal)) return false;
        string mine = Path.TrimEndingDirectorySeparator(folder);
        return !shownFolders.Any(f => !string.IsNullOrEmpty(f) && string.Equals(Path.TrimEndingDirectorySeparator(f), mine, StringComparison.OrdinalIgnoreCase));
    }
}

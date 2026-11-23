namespace Anything.Core;

/// <summary>
/// A search as it showed over a folder, kept in Back and Forward so that coming back to the folder shows the search
/// again, as File Explorer does: what was typed, where it looked, the tab's own filters and options, the order, "every
/// file in" (and whether its files showed as thumbnails), and the result that was opened or focused when it was left,
/// selected again once the search has run afresh. Small on purpose: up to 200 of them sit in a tab's history, so it
/// holds no results, only what it takes to search again.
/// </summary>
public sealed record SearchPage
{
    /// <summary>What was typed in the search box (inside "every file in", the words that narrowed its files).</summary>
    public string Text { get; init; } = "";

    // Where it looked: everywhere (Alt+X, the scope chip), the selected folders, or else the open folder.
    public bool SearchEverywhere { get; init; }
    public bool UseSelScope { get; init; }
    public IReadOnlyList<string> SelScope { get; init; } = [];

    // The tab's own filters and options. The app-wide ones (Everyday files only, Include subfolders, list or
    // thumbnails for search results) are not kept: coming back never changes what every window shares.
    public int FilterIndex { get; init; }
    public bool MatchCase { get; init; }
    public bool WholeWord { get; init; }
    public bool MatchPath { get; init; }
    public bool Diacritics { get; init; }
    public bool Regex { get; init; }
    public bool MatchContent { get; init; }

    public SortKey Sort { get; init; }
    public bool Descending { get; init; }

    // "Every file in": its folders (none for Home's recent files, which are read again rather than kept), its name, its
    // thumbnails, and the orders it goes back to when it is left.
    public bool ZapActive { get; init; }
    public bool ZapThumbs { get; init; }
    public IReadOnlyList<string> ZapRoots { get; init; } = [];
    public bool ZapRecent { get; init; }
    public string ZapLabel { get; init; } = "";
    public (SortKey Key, bool Descending)? SortBeforeZap { get; init; }
    public (SortKey Key, bool Descending)? SortFromFolder { get; init; }

    // The result opened (or focused) when the search was left: by its id while the index still numbers its files the
    // same way, else by its full path. -1 / null when there was none.
    public int ItemId { get; init; } = -1;
    public long ItemGeneration { get; init; }
    /// <summary>The id is the app's own index's, not a disk listing's (a listing's ids mean nothing once it is gone).</summary>
    public bool ItemFromMainIndex { get; init; }
    public string? ItemPath { get; init; }

    /// <summary>Longest text a tooltip quotes before it is cut short.</summary>
    public const int LabelTextLimit = 60;

    /// <summary>
    /// What Back and Forward call this page in their tooltips: the search for "ABC" ("Back to the search for "ABC""),
    /// every file in Downloads, or "cat" in every file in Downloads.
    /// </summary>
    public string Label
    {
        get
        {
            string text = Text.Length > LabelTextLimit ? Text[..LabelTextLimit] + "..." : Text;
            if (!ZapActive) return $"the search for \"{text}\"";
            return text.Length > 0 ? $"\"{text}\" in every file in {ZapLabel}" : $"every file in {ZapLabel}";
        }
    }
}

/// <summary>
/// The decisions around search pages in Back and Forward, kept apart from the window so they can be tested: whether a
/// place left is recorded, what comes back when a folder opens from the history, which result is selected again, and
/// what Back does while a search page is still opening.
/// </summary>
public static class SearchPageRules
{
    /// <summary>
    /// A folder opened (not by Back or Forward) records the place left in Back when it is another place, as before, and
    /// also when it is the same place but a search page over it was left: opening a result's location inside the folder
    /// searched, say, which would otherwise lose the search and make Back skip the folder.
    /// </summary>
    public static bool RecordsLeaving(bool samePlace, bool leavingPage) => !samePlace || leavingPage;

    /// <summary>What a folder that has just opened does about a search page.</summary>
    public readonly record struct Arrival(SearchPage? Restore, SearchPage? KeepForForward);

    /// <summary>
    /// A folder has just opened. From Back or Forward, onto an entry that carries a page, the page shows again, unless
    /// something was typed meanwhile (the newer search stays) or Back was pressed again while it was still opening: then,
    /// as Back on a showing search does, the folder shows and the page is kept for Forward.
    /// </summary>
    public static Arrival OnArrival(bool fromHistory, SearchPage? page, bool searchBoxEmpty, SearchPage? closedWhileOpening)
    {
        if (!fromHistory || page is null) return default;
        if (ReferenceEquals(page, closedWhileOpening)) return new(null, page);
        return searchBoxEmpty ? new(page, null) : default;
    }

    /// <summary>What Back does.</summary>
    public enum BackMove
    {
        /// <summary>Results (or "every file in") show: Back closes them over the same folder, as it always has.</summary>
        LeaveResults,
        /// <summary>Back already went to a search page that is still opening: this Back means the folder under it.</summary>
        CloseOpeningPage,
        /// <summary>Back through the history.</summary>
        Step,
    }

    /// <summary>
    /// Back pressed twice quickly: the first is still opening a folder whose search is to show again, so the second
    /// doesn't step the history once more (past the folder) but lets the folder show without its search, where two
    /// presses would have ended had the first finished. A third steps the history as usual.
    /// </summary>
    public static BackMove Back(bool resultsShowing, SearchPage? pageOpening, SearchPage? closedWhileOpening)
        => resultsShowing ? BackMove.LeaveResults
         : pageOpening is not null && !ReferenceEquals(pageOpening, closedWhileOpening) ? BackMove.CloseOpeningPage
         : BackMove.Step;

    /// <summary>Where Back's tooltip says it goes: from results, the folder they were searched over; else the history's place or page.</summary>
    public static string? BackTarget(bool resultsShowing, string? folderName, string? backLabel)
        => resultsShowing ? folderName : backLabel;

    /// <summary>Where Forward's tooltip says it goes: the search Back closed here, else the history's place or page.</summary>
    public static string? ForwardTarget(SearchPage? closedSearchHere, string? forwardLabel)
        => closedSearchHere?.Label ?? forwardLabel;

    /// <summary>
    /// The result a page remembers: the one opened (<paramref name="opened"/>: the folder opened, or the item whose
    /// location was opened) when it is the focused result or one of the selected ones, else the focused result.
    /// </summary>
    public static (int Id, string? Path) PickRemembered((int Id, string Path)? focused, IEnumerable<(int Id, string Path)> selected, string? opened)
    {
        if (opened is not null)
        {
            if (focused is { } f && SamePath(f.Path, opened)) return f;
            foreach (var s in selected) if (SamePath(s.Path, opened)) return s;
        }
        return focused is { } only ? only : (-1, null);
    }

    /// <summary>
    /// Where the remembered result is in fresh results, or -1. While the index numbers its files as it did, by its id
    /// (an id not there means the result is gone from the search: nothing is selected, never a namesake); otherwise by
    /// its full path, names first so only same-named files have their path built.
    /// </summary>
    public static int FindResult(ReadOnlySpan<int> ids, SearchPage page, bool sameNumbering, Func<int, string> name, Func<int, string> fullPath)
    {
        if (sameNumbering && page.ItemId >= 0) return ids.IndexOf(page.ItemId);
        if (page.ItemPath is not { Length: > 0 } path) return -1;
        string wanted = Path.GetFileName(path.TrimEnd('\\'));
        if (wanted.Length == 0) return -1;
        for (int i = 0; i < ids.Length; i++)
        {
            int id = ids[i];
            if (!string.Equals(name(id), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (SamePath(fullPath(id), path)) return i;
        }
        return -1;
    }

    /// <summary>What to do with the remembered result when results come in.</summary>
    public enum SelectMove { Select, Wait, GiveUp }

    /// <summary>
    /// Results came in after a page showed again. Another search by then (other words, another tab): give up. The user
    /// selected something meanwhile (only possible after the first results): theirs stays. Found: select it. Not found
    /// while "every file in" is still reading the disk: wait for the next results. Otherwise nothing is selected.
    /// </summary>
    public static SelectMove DecideSelect(bool sameSearch, int found, bool stillReading, bool userSelected)
        => !sameSearch || userSelected ? SelectMove.GiveUp
         : found >= 0 ? SelectMove.Select
         : stillReading ? SelectMove.Wait
         : SelectMove.GiveUp;

    static bool SamePath(string a, string b)
        => string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    // "C:\" stays as it is: only a folder's trailing backslash goes.
    static string Trim(string p) => p.Length > 3 ? p.TrimEnd('\\') : p;
}

/// <summary>
/// The search page over the folder on screen that a navigation away from it keeps in Back with that folder, and where
/// that navigation goes. Set before the navigation starts (it can complete inside the call that starts it) and taken when
/// a navigation completes or a Back or Forward starts from the same folder.
/// </summary>
public readonly record struct LeavingPage<T>(SearchPage? Page, T? Target) where T : class
{
    /// <summary>
    /// A navigation starts from the folder on screen: its page when it has one, else the page an earlier navigation
    /// still opening kept (the folder on screen hasn't changed, so the page is still that folder's: a second click in the
    /// navigation pane while the first folder is still opening must not lose the search). The target is the newest
    /// navigation's, the one the shell goes on with.
    /// </summary>
    public LeavingPage<T> Start(SearchPage? page, T? target) => new(page ?? Page, target);

    /// <summary>
    /// A navigation failed. The page goes with it when it was the navigation's own target that failed (or either is
    /// unknown); a late failure of an earlier navigation the newer one cancelled leaves the newer one's page alone.
    /// </summary>
    public LeavingPage<T> Fail(T? failed, Func<T, T, bool> samePlace)
        => failed is null || Target is null || samePlace(failed, Target) ? default : this;
}

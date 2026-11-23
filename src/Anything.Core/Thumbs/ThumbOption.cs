using System.Globalization;
using Anything.Core.Content;

namespace Anything.Core.Thumbs;

// How to use the option's rules (Settings' "Instant thumbnails" card, the one-time recommendation bar, the windows'
// count of waits). Everything here is pure: no window, no file, no clock of its own (the caller passes the time).
//
//   ThumbOption.StoreFolder(databasePath, chosenPath)    where the store lies: "Thumbs" beside the database, or
//                                                          "Rock Preview Thumbnails" inside the folder the user chose.
//   ThumbOption.CapMegabytesFrom(ini value)               the limit, snapped to one of Settings' choices (500 when unknown).
//   ThumbOption.EstimateText / RecommendationText / SizeNowText / TurnOffQuestion / ProblemText / FreeSpaceText
//                                                          the sentences, with the real numbers in them.
//   ThumbOption.ShouldRecommend(...)                      whether the bar may show now.
//   ThumbFillWatch.Observe(now, unanswered)               a grid's screen: true once when it has taken more than a second
//                                                          to fill (a wait).
//   ThumbWaitOccasions.TryCount(now)                      whether that wait is a new occasion (the bar needs three).

/// <summary>The rules and the words of the "Instant thumbnails" option. Pure and thread-safe.</summary>
public static class ThumbOption
{
    /// <summary>Waits (a screen of thumbnails slower than a second to fill, on separate occasions) before the bar may show.</summary>
    public const int WaitsBeforeRecommending = 3;

    static readonly CultureInfo C = CultureInfo.InvariantCulture;
    // Every size in Settings is counted in decimal units (ContentPresentation.Size), the limit and the store's size too.
    const long MB = ThumbStore.BytesPerMegabyte;

    /// <summary>
    /// The store's folder: inside <paramref name="chosenPath"/> when the user picked a place (ini thumbnail_store_path),
    /// else "Thumbs" beside the index database. Never throws: a chosen path that can't be made full gives the default.
    /// </summary>
    public static string StoreFolder(string databasePath, string? chosenPath)
    {
        if (chosenPath is { Length: > 0 } chosen)
        {
            try { return ThumbStore.FolderIn(chosen.Trim().Trim('"')); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return ThumbStore.FolderFor(databasePath);
    }

    /// <summary>
    /// Whether <paramref name="folder"/> lies inside <paramref name="parent"/> (or is it), compared as Windows compares
    /// paths: a store moved into the old store's own folder would make that folder hold something foreign, so the old
    /// one could never be removed. Never throws: paths that can't be made full are not inside each other.
    /// </summary>
    public static bool IsWithin(string folder, string parent)
    {
        try
        {
            string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            return f.Equals(p, StringComparison.OrdinalIgnoreCase)
                || f.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// What Settings says after the old store's folder was to be removed (the option turned off, or a new location
    /// chosen) and wasn't; "" when it went, or was never there.
    /// </summary>
    public static string RemovalNote(StoreRemoval removal, string folder) => removal switch
    {
        StoreRemoval.LeftAlone => $"{folder} also holds files that aren't saved thumbnails, so the folder was left where it is. The saved thumbnails in it were deleted.",
        StoreRemoval.Failed => $"Some saved thumbnails in {folder} couldn't be deleted (a file was in use, or the drive isn't there). Delete that folder when you no longer need it.",
        _ => "",
    };

    /// <summary>
    /// The limit from the ini (megabytes): one of <see cref="ThumbStore.CapChoicesMegabytes"/>, the nearest at or below a
    /// value between them (an edited ini never gets more than it asked for), the smallest for less; 500 when missing or
    /// not a number.
    /// </summary>
    public static int CapMegabytesFrom(string? value)
    {
        if (value is null || !int.TryParse(value.Trim(), NumberStyles.Integer, C, out int mb)) return ThumbStore.DefaultCapMegabytes;
        var choices = ThumbStore.CapChoicesMegabytes;
        int pick = choices[0];
        foreach (int c in choices) if (c <= mb) pick = c;
        return pick;
    }

    /// <summary>A limit as Settings lists it: "250 MB", "500 MB", "1 GB", "8 GB" (decimal: 1 GB is 1,000 MB).</summary>
    public static string CapText(int megabytes)
        => megabytes >= 1000 && megabytes % 1000 == 0 ? (megabytes / 1000).ToString(C) + " GB"
            : megabytes >= 1000 ? (megabytes / 1000.0).ToString("0.#", C) + " GB"
            : megabytes.ToString(C) + " MB";

    /// <summary>
    /// What the store takes now, against its limit: "123 MB of 500 MB", "1.2 GB of 2 GB", "0 MB of 500 MB" while empty,
    /// "less than 1 MB of 500 MB". Counted as the limit and every other size in Settings are (1 MB = 1,000,000 bytes) and
    /// rounded down, so a full store reads as its limit, never more. Unknown (negative): "".
    /// </summary>
    public static string SizeNowText(long bytes, int capMegabytes)
        => bytes < 0 ? "" : $"{StoreSizeText(bytes)} of {CapText(capMegabytes)}";

    /// <summary>A store's size, counted as its limit is and rounded down: "0 MB", "less than 1 MB", "123 MB", "1.2 GB".</summary>
    public static string StoreSizeText(long bytes)
        => bytes <= 0 ? "0 MB"
            : bytes < MB ? "less than 1 MB"
            : bytes < 1000 * MB ? (bytes / MB).ToString("#,0", C) + " MB"
            : (Math.Floor(bytes / (100.0 * MB)) / 10).ToString("0.0", C) + " GB";

    /// <summary>
    /// The card's line, before the option is turned on: what it gains, what it could take on this PC, what the limit
    /// does. While indexing isn't complete (or nothing is counted yet) the cost can't be known, so only the gain is said.
    /// </summary>
    public static string EstimateText(ThumbCensus? census, bool indexingComplete, int capMegabytes, int sizeClass)
    {
        const string gain = "Thumbnails you have seen before appear at once, also after a restart, because small copies are kept on disk. ";
        if (!indexingComplete || census is not { } c) return gain + "The space this needs is worked out when indexing has finished.";
        string cap = CapText(capMegabytes);
        if (c.Files == 0) return gain + $"No pictures, videos or documents were found on this PC, so this takes almost no space; Rock Preview keeps at most {cap}.";
        string files = c.Files == 1 ? "the 1 picture, video or document on this PC it" : $"the {CountText(c.Files)} pictures, videos and documents on this PC they";
        return gain + $"For {files} could take {About(c.BytesAt(sizeClass))}; Rock Preview keeps at most {cap} and drops the oldest when that is full.";
    }

    // A count the card can show while indexing goes on: from 10,000 up rounded to the thousand ("roughly 372,000"), so it
    // doesn't change from one refresh to the next as files come and go; below that the number itself.
    static string CountText(long files)
        => files >= 10_000
            ? "roughly " + ((long)Math.Round(files / 1000.0, MidpointRounding.AwayFromZero) * 1000).ToString("#,0", C)
            : files.ToString("#,0", C);

    // "about 1.4 GB", but "less than 10 MB" where the size is too small to put a figure on (never "about under 10 MB").
    static string About(long bytes) => bytes < 10 * MB ? "less than 10 MB" : "about " + ContentPresentation.Size(bytes);

    /// <summary>
    /// The one-time bar's sentence. The store never grows past <paramref name="capMegabytes"/>, so when this PC's files
    /// (at the size class of the grid's thumbnail size) would need that much or more, the limit is the figure said;
    /// below it, the estimate.
    /// </summary>
    public static string RecommendationText(ThumbCensus census, int sizeClass, int capMegabytes)
    {
        long bytes = Math.Max(0, census.BytesAt(sizeClass));
        string uses = bytes < 10 * MB ? "less than 10 MB"
            : bytes >= capMegabytes * MB ? "at most " + CapText(capMegabytes)
            : "about " + ContentPresentation.Size(bytes);
        return $"Thumbnails can appear at once if Rock Preview keeps small copies of them on disk, using {uses}. You choose the limit and the folder.";
    }

    /// <summary>
    /// Settings' line while the option is off and an earlier store couldn't be deleted yet: "12 MB saved earlier are still
    /// in X:\Thumbs; Rock Preview deletes them when it can (at the next start)".
    /// </summary>
    public static string LeftoverText(long bytes, string folder)
        => $"{StoreSizeText(bytes)} saved earlier are still in {folder}; Rock Preview deletes them when it can (at the next start)";

    /// <summary>
    /// The question before turning the option off: what it deletes and the space that frees (unknown: not said). Only the
    /// store's own files are deleted, also from a folder that holds other things, so the space is what they take.
    /// </summary>
    public static string TurnOffQuestion(long storeBytes)
        => storeBytes >= MB
            ? $"The saved thumbnails are deleted, which frees {StoreSizeText(storeBytes)}. Thumbnails go on working the normal way: made as you browse, not kept."
            : "The saved thumbnails are deleted. Thumbnails go on working the normal way: made as you browse, not kept.";

    /// <summary>
    /// The free bytes for this user on the drive that holds <paramref name="folder"/> (or its nearest existing parent);
    /// -1 when unknown. The one thing here that touches the disk: call it off the UI thread (it may be a network share).
    /// </summary>
    public static long FreeBytes(string folder) => Content.Indexing.ContentNative.DiskSpace(folder).Free;

    /// <summary>The free space on the store's drive: "120 GB free on this drive"; "" when unknown (negative).</summary>
    public static string FreeSpaceText(long freeBytes) => freeBytes < 0 ? "" : $"{ContentPresentation.Size(freeBytes)} free on this drive";

    /// <summary>
    /// The line Settings shows when the store can't work where it is; "" when it can (or the option is off). The option
    /// stays on and thumbnails work the normal way meanwhile: this never becomes an error box.
    /// </summary>
    public static string ProblemText(ThumbStoreProblem problem) => problem switch
    {
        ThumbStoreProblem.FolderUnusable => "This location can't be used (the drive isn't there, or Rock Preview may not write to it). Thumbnails work the normal way until it can, or choose another location.",
        ThumbStoreProblem.InUse => "Another copy of Rock Preview is using this location. Thumbnails work the normal way here until it closes, or choose another location.",
        ThumbStoreProblem.OtherAccount => "Another Windows account keeps its thumbnails in this location, so Rock Preview doesn't keep any there. Choose another location.",
        _ => "",
    };

    /// <summary>
    /// Whether the one-time bar may show: the option off, the bar never shown, not the demo, indexing complete with an
    /// estimate worked out, and the user made to wait for thumbnails on <see cref="WaitsBeforeRecommending"/> occasions.
    /// </summary>
    public static bool ShouldRecommend(bool optionOn, bool shownBefore, bool demo, bool indexingComplete, bool estimateExists, int waits)
        => !optionOn && !shownBefore && !demo && indexingComplete && estimateExists && waits >= WaitsBeforeRecommending;
}

/// <summary>
/// Watches one grid's screen for waits: from the moment some cell on screen has nothing to show until every one has
/// (a picture, a stand-in or an icon). A stretch longer than <see cref="WaitMs"/> is one wait, reported once, as soon
/// as it passes the second (a screen that never fills is a wait too). Not thread-safe: one grid, its UI thread.
/// </summary>
public sealed class ThumbFillWatch
{
    /// <summary>A screen that takes longer than this to fill made the user wait.</summary>
    public const long WaitMs = 1000;

    long _since = -1;
    bool _reported;

    /// <summary>Whether a stretch of empty cells is being timed.</summary>
    public bool Running => _since >= 0;

    /// <summary>
    /// The cells on screen with nothing to show at <paramref name="nowMs"/> (any clock in milliseconds). True once per
    /// stretch: the call that first sees it last longer than a second.
    /// </summary>
    public bool Observe(long nowMs, int unanswered)
    {
        if (unanswered <= 0)
        {
            bool wait = _since >= 0 && !_reported && nowMs - _since > WaitMs;
            _since = -1;
            _reported = false;
            return wait;
        }
        if (_since < 0)
        {
            _since = nowMs;
            _reported = false;
            return false;
        }
        if (_reported || nowMs - _since <= WaitMs) return false;
        _reported = true;
        return true;
    }

    /// <summary>The screen went away (hidden, another list): the stretch ends without counting.</summary>
    public void Reset()
    {
        _since = -1;
        _reported = false;
    }
}

/// <summary>
/// Turns waits into occasions: waits closer together than <see cref="MinGapMs"/> are one occasion (scrolling through a
/// slow folder is one wait, not ten). Belongs to the program (all its windows); not thread-safe: the UI thread.
/// </summary>
public sealed class ThumbWaitOccasions
{
    /// <summary>Waits within this many milliseconds of the last one counted are the same occasion.</summary>
    public const long MinGapMs = 5 * 60 * 1000;

    long _lastCounted = long.MinValue;

    /// <summary>A wait at <paramref name="nowMs"/>: true when it is a new occasion (and remembers it), false when it is the same one.</summary>
    public bool TryCount(long nowMs)
    {
        if (_lastCounted != long.MinValue && nowMs - _lastCounted < MinGapMs) return false;
        _lastCounted = nowMs;
        return true;
    }
}

using System.Globalization;
using Anything.Core.Content;

namespace Anything.Core.Thumbs;

// How to use the option's rules (Settings' "Instant thumbnails" card, the one-time recommendation bar, the windows'
// count of waits). Everything here is pure: no window, no file, no clock of its own (the caller passes the time).
//
//   ThumbOption.StoreFolder(databasePath, chosenPath)    where the store lies: "Thumbs" beside the database, or
//                                                          "Rock Preview Thumbnails" inside the folder the user chose.
//   ThumbOption.CapMegabytesFrom(ini value)               the limit, kept within its bounds (500 when unknown).
//   ThumbOption.TryParseSize(typed text, out megabytes)   a limit typed in a box labelled in GB ("8", "1.5 TB", "500 MB").
//   ThumbOption.LimitBoxText / LimitTyped / EnoughMegabytes   the limit box: what it shows, what a typed text sets, and
//                                                          "Enough for everything" (the estimate rounded up to whole GB).
//   ThumbOption.ShouldTellFull(limit, toldAt, toldThisRun) whether the "store is full" bar may show.
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
    /// The limit from the ini (megabytes): the whole number written there, kept within <see cref="ThumbStore.MinCapMegabytes"/>
    /// and <see cref="ThumbStore.MaxCapMegabytes"/> (an ini of 1.7.30 or earlier holds one of its choices, read as it is);
    /// 500 when missing or not a whole number.
    /// </summary>
    public static int CapMegabytesFrom(string? value)
    {
        if (value is null || !long.TryParse(value.Trim(), NumberStyles.Integer, C, out long mb)) return ThumbStore.DefaultCapMegabytes;
        return (int)Math.Clamp(mb, ThumbStore.MinCapMegabytes, ThumbStore.MaxCapMegabytes);
    }

    // "1.5 TB", "8 GB", "500 MB", "0.5": a number with a point or a comma, then an optional unit.
    static readonly System.Text.RegularExpressions.Regex SizePattern = new(
        @"^\s*(?<n>\d+(?:[.,]\d*)?|[.,]\d+)\s*(?<unit>mb|gb|tb)?\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // "1,000" or "12,500": a comma that groups thousands, not one before decimals.
    static readonly System.Text.RegularExpressions.Regex ThousandsPattern = new(
        @"^\d{1,3},\d{3}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A limit as a person types it in a box labelled in GB: "8", "8 GB", "8gb", "0.5", "1,5", "500 MB", "1.5 TB" (spaces
    /// allowed, a point or a comma before the decimals; a bare number is GB). The megabytes are rounded to a whole number
    /// and kept within <see cref="ThumbStore.MinCapMegabytes"/> and <see cref="ThumbStore.MaxCapMegabytes"/>. False for
    /// anything else (a minus sign, another unit, two numbers, nothing), and for one to three digits, a comma and exactly
    /// three digits ("1,000", "250,000 MB"): that may be a thousand or one point nought, so it is not guessed.
    /// </summary>
    public static bool TryParseSize(string? text, out int megabytes)
    {
        megabytes = 0;
        if (text is null) return false;
        var m = SizePattern.Match(text);
        if (!m.Success || ThousandsPattern.IsMatch(m.Groups["n"].Value)) return false;
        if (!decimal.TryParse(m.Groups["n"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, C, out decimal n)) return false;
        decimal per = m.Groups["unit"].Value.ToLowerInvariant() switch { "mb" => 1m, "tb" => 1_000_000m, _ => 1000m };
        // Huge numbers stop at the bound before they could overflow.
        decimal mb = n > ThumbStore.MaxCapMegabytes ? ThumbStore.MaxCapMegabytes : Math.Round(n * per, MidpointRounding.AwayFromZero);
        megabytes = (int)Math.Clamp(mb, ThumbStore.MinCapMegabytes, ThumbStore.MaxCapMegabytes);
        return true;
    }

    /// <summary>
    /// The limit as the box labelled "GB" shows it: a plain number of GB, "0.5", "8", "120", "1.25" (decimal, at most
    /// three decimals, which is a whole MB).
    /// </summary>
    public static string LimitBoxText(int megabytes) => (megabytes / 1000m).ToString("0.###", C);

    /// <summary>
    /// What the limit becomes when <paramref name="typed"/> is committed in the box (Enter, or the box losing focus):
    /// the size typed (<see cref="TryParseSize"/>), or <paramref name="current"/> when the text isn't a size.
    /// </summary>
    public static int LimitTyped(string? typed, int current) => TryParseSize(typed, out int mb) ? mb : current;

    /// <summary>
    /// Whether to ask before the limit goes from <paramref name="oldMegabytes"/> to <paramref name="newMegabytes"/>: only
    /// when it is lowered below what the store holds now (<paramref name="storeBytes"/>; negative when not known, and then
    /// nothing is asked), since the oldest thumbnails are deleted at once until the store fits.
    /// </summary>
    public static bool AskBeforeLowering(int oldMegabytes, int newMegabytes, long storeBytes)
        => storeBytes >= 0 && newMegabytes < oldMegabytes && storeBytes > ThumbStore.CapBytesFor(newMegabytes);

    /// <summary>
    /// The question before such a lower limit is applied (<see cref="AskBeforeLowering"/>). The limit is said exactly and
    /// the store's size rounded up, so the size always reads larger than the limit ("2.1 GB" against "2 GB", never "2.0 GB").
    /// </summary>
    public static string LowerLimitQuestion(long storeBytes, int newMegabytes)
        => $"The store holds {StoreSizeUpText(storeBytes)} now. With a limit of {ExactCapText(newMegabytes)}, the oldest thumbnails are deleted until it fits. Lower the limit?";

    // A limit to the megabyte: "500 MB", "2 GB", "2.05 GB", "1.25 TB" (CapText rounds to a tenth, which could read as more).
    static string ExactCapText(int megabytes)
        => megabytes < 1000 ? megabytes.ToString(C) + " MB"
            : megabytes < 1_000_000 ? (megabytes / 1000m).ToString("0.###", C) + " GB"
            : (megabytes / 1_000_000m).ToString("0.######", C) + " TB";

    // A store's size laid out as StoreSizeText lays it out, but rounded up: anything over a limit reads over it.
    static string StoreSizeUpText(long bytes)
    {
        static decimal Up(long b, decimal unitBytes, int decimals)
        {
            decimal scale = decimals == 0 ? 1m : 10m;
            return Math.Ceiling(b / unitBytes * scale) / scale;
        }
        return bytes <= 0 ? "0 MB"
            : bytes <= 999 * MB ? Up(bytes, MB, 0).ToString("#,0", C) + " MB"
            : bytes <= 99_900 * MB ? Up(bytes, 1000m * MB, 1).ToString("0.0", C) + " GB"
            : bytes <= 999_000 * MB ? Up(bytes, 1000m * MB, 0).ToString("#,0", C) + " GB"
            : Up(bytes, 1_000_000m * MB, 1).ToString("0.0", C) + " TB";
    }

    /// <summary>"Enough for everything": the estimate rounded up to the next whole GB, at least 1 GB (and within the bounds).</summary>
    public static int EnoughMegabytes(long estimateBytes)
    {
        long gb = Math.Max(1, (Math.Max(0, estimateBytes) + 1000 * MB - 1) / (1000 * MB));
        return (int)Math.Clamp(gb * 1000, ThumbStore.MinCapMegabytes, ThumbStore.MaxCapMegabytes);
    }

    /// <summary>The link beside the limit box: "Enough for everything: 3 GB".</summary>
    public static string EnoughText(int megabytes) => $"Enough for everything: {CapText(megabytes)}";

    /// <summary>"drive C:" for a folder on a lettered drive, else "this drive" (a network share). Pure: reads no disk.</summary>
    public static string DriveName(string folder)
    {
        string? root = null;
        try { root = Path.GetPathRoot(folder); }
        catch (ArgumentException) { }
        return root is { Length: >= 2 } r && char.IsAsciiLetter(r[0]) && r[1] == ':' ? $"drive {char.ToUpperInvariant(r[0])}:" : "this drive";
    }

    /// <summary>
    /// The grey line under the limit, always true for the store's folder: "120 GB free on drive C:." and, when the limit
    /// is more than that plus what the store holds already, that saving stops when the drive is nearly full. "" when the
    /// free space is unknown (negative).
    /// </summary>
    public static string LimitSpaceText(long freeBytes, string folder, int capMegabytes, long storeBytes)
    {
        if (freeBytes < 0) return "";
        string text = $"{ContentPresentation.Size(freeBytes)} free on {DriveName(folder)}.";
        if (capMegabytes * MB > freeBytes + Math.Max(0, storeBytes))
            text += " The limit is more than that: saving stops when the drive is nearly full.";
        return text;
    }

    /// <summary>
    /// The card's line while the store is full: documents and videos are dropped before pictures, and new ones of a kind
    /// that would go first are not saved (<see cref="ThumbStore.Refuses"/>).
    /// </summary>
    public const string FullText = "Full: to keep your pictures, thumbnails of documents and videos go first, and new ones aren't saved while it is full. Raise the limit to keep more.";

    /// <summary>The card's line while new pictures are refused because the drive is nearly full.</summary>
    public static string DiskLowText(string folder) => $"Not saving new thumbnails now: {DriveName(folder)} is nearly full.";

    /// <summary>The bar above the files when the store has filled up: "Instant thumbnails is full (8 GB). ..."</summary>
    public static string FullBarText(int capMegabytes)
        => $"Instant thumbnails is full ({CapText(capMegabytes)}). To make room, documents and videos go first, then the oldest pictures.";

    /// <summary>
    /// Whether the "store is full" bar may show: not yet in this run, and not for the limit the user was last told about
    /// (<paramref name="toldAtMegabytes"/>, 0 when never; a different limit tells again).
    /// </summary>
    public static bool ShouldTellFull(int capMegabytes, int toldAtMegabytes, bool toldThisRun)
        => !toldThisRun && capMegabytes != toldAtMegabytes;

    /// <summary>
    /// The grey line under "Keep thumbnails of": that nothing new is saved when no box is ticked; otherwise which kinds
    /// go first when the store is full, and what happens to thumbnails already saved for a kind switched off (deleted:
    /// the store keeps the kinds apart) or a file type left out (they stay: the store doesn't keep types apart). With
    /// <paramref name="earlierSaved"/> (the store holds thumbnails an earlier version saved, which carry no kind) one more
    /// sentence says those are the exception to both rules.
    /// </summary>
    public static string KeepNote(ThumbKeep keep, bool earlierSaved = false)
        => keep.KeepsNothing ? "Nothing is being saved."
            : "When the store is full, thumbnails of documents are dropped first, then videos, then the oldest pictures. "
              + "Switching a kind off deletes its saved thumbnails; those of the file types listed here stay until the store needs their room, or until you clear it."
              + (earlierSaved ? " Thumbnails saved by an earlier version go first, and stay when you switch a kind off." : "");

    /// <summary>A limit as it reads: "250 MB", "1 GB", "1.5 GB", "120 GB", "2 TB" (decimal: 1 GB is 1,000 MB, 1 TB 1,000 GB).</summary>
    public static string CapText(int megabytes)
        => megabytes < 1000 ? megabytes.ToString(C) + " MB"
            // 999,960 MB would read "1000 GB": from there on it is TB.
            : megabytes < 999_950 ? (megabytes / 1000.0).ToString("0.#", C) + " GB"
            : (megabytes / 1_000_000.0).ToString("0.#", C) + " TB";

    /// <summary>
    /// What the store takes now, against its limit: "123 MB of 500 MB", "1.2 GB of 2 GB", "0 MB of 500 MB" while empty,
    /// "less than 1 MB of 500 MB". Counted as the limit and every other size in Settings are (1 MB = 1,000,000 bytes) and
    /// rounded down, so a full store reads as its limit, never more. Unknown (negative): "".
    /// </summary>
    public static string SizeNowText(long bytes, int capMegabytes)
        => bytes < 0 ? "" : $"{StoreSizeText(bytes)} of {CapText(capMegabytes)}";

    /// <summary>
    /// <see cref="SizeNowText(long, int)"/> with what each kind takes, when two or more hold something: "1.2 GB of 2 GB:
    /// pictures 900 MB, videos 100 MB, documents 200 MB". Thumbnails saved by an earlier version, which carry no kind,
    /// read "saved by an earlier version 300 MB". Only kinds that hold something are named.
    /// </summary>
    public static string SizeNowText(long bytes, int capMegabytes, ThumbStoreSplit split)
    {
        string text = SizeNowText(bytes, capMegabytes);
        if (text.Length == 0) return text;
        var parts = new List<string>(4);
        foreach (var (name, part) in new[] { ("pictures", split.Pictures), ("videos", split.Videos), ("documents", split.Documents), ("saved by an earlier version", split.Earlier) })
            if (part > 0) parts.Add($"{name} {StoreSizeText(part)}");
        return parts.Count < 2 ? text : $"{text}: {string.Join(", ", parts)}";
    }

    /// <summary>
    /// A store's size, counted as its limit is and rounded down: "0 MB", "less than 1 MB", "123 MB", "1.2 GB", "120 GB",
    /// "1.5 TB" (a tenth below 100 of a unit, whole units from 100 up).
    /// </summary>
    public static string StoreSizeText(long bytes)
        => bytes <= 0 ? "0 MB"
            : bytes < MB ? "less than 1 MB"
            : bytes < 1000 * MB ? (bytes / MB).ToString("#,0", C) + " MB"
            : bytes < 100_000 * MB ? (Math.Floor(bytes / (100.0 * MB)) / 10).ToString("0.0", C) + " GB"
            : bytes < 1_000_000 * MB ? (bytes / (1000 * MB)).ToString("#,0", C) + " GB"
            : bytes < 100_000_000 * MB ? (Math.Floor(bytes / (100_000.0 * MB)) / 10).ToString("0.0", C) + " TB"
            : (bytes / (1_000_000 * MB)).ToString("#,0", C) + " TB";

    /// <summary>
    /// The card's line, before the option is turned on: what it gains, what it could take on this PC, what the limit
    /// does. While indexing isn't complete (or nothing is counted yet) the cost can't be known, so only the gain is said.
    /// </summary>
    public static string EstimateText(ThumbCensus? census, bool indexingComplete, int capMegabytes, int sizeClass, ThumbKeep? keep = null)
    {
        const string gain = "Thumbnails you have seen before appear at once, also after a restart, because small copies are kept on disk. ";
        if (!indexingComplete || census is not { } c) return gain + "The space this needs is worked out when indexing has finished.";
        string cap = CapText(capMegabytes);
        // With some types switched off, the count is of the types chosen, not of every picture, video and document.
        bool chosen = keep is not null && !keep.Equals(ThumbKeep.All);
        if (c.Files == 0)
            return gain + (chosen ? "No files of the types you chose were found on this PC" : "No pictures, videos or documents were found on this PC")
                        + $", so this takes almost no space; Rock Preview keeps at most {cap}.";
        string files = chosen
            ? c.Files == 1 ? "the 1 file of the types you chose on this PC it" : $"the {CountText(c.Files)} files of the types you chose on this PC they"
            : c.Files == 1 ? "the 1 picture, video or document on this PC it" : $"the {CountText(c.Files)} pictures, videos and documents on this PC they";
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

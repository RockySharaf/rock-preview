using System.Globalization;
using System.Text;

namespace Anything.Core.Content;

/// <summary>
/// Plain-words text for search inside files: estimates, progress, reasons, the status bar, hints, the offer bar and the
/// confirmations. It is the one source of the three choices' titles, so setup, Settings and the hints say the same thing.
/// Sizes are in decimal units (1 MB = 1,000,000 bytes), numbers in the current culture.
/// </summary>
public static class ContentPresentation
{
    const long MB = 1_000_000, GB = 1_000_000_000, TB = 1_000_000_000_000;

    static CultureInfo C => CultureInfo.CurrentCulture;

    /// <summary>What the status bar shows while Search › Match inside files is off and the text index is on.</summary>
    public const string NamesOnlyIndicator = "NAMES ONLY";

    /// <summary>The status bar while some lookups inside files are still running (SearchResults.ContentPending).</summary>
    public const string LookingInside = "Looking inside files…";

    /// <summary>The kinds of document read, in the words setup and Settings use.</summary>
    public const string KindsDescription = "PDF, Word, Excel and PowerPoint (2007 and later), text, saved emails and design files (PSD, AI, InDesign)";

    /// <summary>What is read from pictures (the third choice).</summary>
    public const string PictureDetailsDescription =
        "size in pixels, resolution (DPI), colour mode (RGB or CMYK), date taken, camera, place, title and keywords";

    /// <summary>What the text index keeps (setup's footnote, Settings, the README).</summary>
    public const string PrivacyNote =
        "A copy of each document's text (up to 1 MB) is kept on this PC, in your own profile; never pictures or layout, and nothing leaves this PC.";

    /// <summary>Shown beside a content choice while the app is set to run as administrator.</summary>
    public const string AdminNote = "Reading inside files waits while the app runs as administrator.";

    /// <summary>Where the choice lives later, for setup's footnote.</summary>
    public const string ChangeLater = "Add drives or network folders, or change this, any time in Settings › Indexing.";

    /// <summary>
    /// The network speed Settings stores for "No limit" (KB/s; the ini needs a number). At or above it, nothing says "at
    /// most …".
    /// </summary>
    public const int NoNetworkLimitKbps = 1_000_000;

    // ---- sizes and times ------------------------------------------------------------------------

    /// <summary>
    /// "under 10 MB"; then two significant figures in MB ("27 MB", "110 MB"); from 500 MB, GB with one decimal place
    /// ("0.8 GB", "12.5 GB"), two significant figures from 100 GB ("200 GB"); TB likewise.
    /// </summary>
    public static string Size(long bytes)
    {
        if (bytes < 10 * MB) return "under 10 MB";
        if (bytes < 500 * MB)
        {
            double mb = Sig2(bytes / (double)MB);
            if (mb < 500) return mb.ToString("#,0", C) + " MB";
        }
        foreach (var (unit, name) in new[] { (GB, "GB"), (TB, "TB") })
        {
            double v = bytes / (double)unit;
            if (unit == TB || v < 1000)
            {
                double one = Math.Round(v, 1, MidpointRounding.AwayFromZero);
                if (one < 100) return one.ToString("0.0", C) + " " + name;
                double two = Sig2(v);
                if (unit == TB || two < 1000) return two.ToString("#,0", C) + " " + name;
            }
        }
        return Sig2(bytes / (double)TB).ToString("#,0", C) + " TB";
    }

    /// <summary>Two significant figures (27.46 → 27, 113.5 → 110, 0.0 → 0).</summary>
    static double Sig2(double v)
    {
        if (v <= 0 || double.IsNaN(v) || double.IsInfinity(v)) return 0;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(v)) - 1);
        return Math.Round(v / magnitude, MidpointRounding.AwayFromZero) * magnitude;
    }

    /// <summary>
    /// Always rounded up, never seconds: whole minutes under 10 minutes, then 5 minutes, then whole hours up to 48, then
    /// days ("3 minutes", "25 minutes", "33 hours", "3 days"); "under a minute" for nothing at all.
    /// </summary>
    public static string Duration(TimeSpan time)
    {
        if (time <= TimeSpan.Zero) return "under a minute";
        double minutes = time.TotalMinutes;
        int m = CeilInt(minutes);
        if (m < 10) return m == 1 ? "1 minute" : m + " minutes";
        if (minutes <= 60)
        {
            int five = CeilInt(minutes / 5) * 5;
            return five >= 60 ? "1 hour" : five + " minutes";
        }
        double hoursExact = time.TotalHours;
        int hours = CeilInt(hoursExact);
        if (hours <= 48) return hours == 1 ? "1 hour" : hours + " hours";
        int days = CeilInt(hoursExact / 24);
        return days.ToString("#,0", C) + " days";
    }

    // Rounding up, with a little room for floating-point noise (exactly 3 minutes stays 3).
    static int CeilInt(double v) => v >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(v - 1e-9);

    /// <summary>
    /// The first pass in words: "the first pass takes about 3 minutes in the background", or for long ones reading time
    /// rather than a date: "the first pass is about 33 hours of reading while this PC is on, slower while you use it".
    /// </summary>
    public static string FirstPass(TimeSpan time, bool atLeast)
    {
        string about = atLeast ? "at least" : "about";
        return time < TimeSpan.FromHours(1)
            ? $"the first pass takes {about} {Duration(time)} in the background"
            : $"the first pass is {about} {Duration(time)} of reading while this PC is on, slower while you use it";
    }

    /// <summary>"about 28 MB", "at least 28 MB" or "under 10 MB" (lower case: it starts no sentence).</summary>
    static string SizePhrase(long bytes, bool atLeast) => bytes < 10 * MB ? "under 10 MB" : (atLeast ? "at least " : "about ") + Size(bytes);

    static string Count(int n, string one, string many, bool atLeast)
        => (n == 1 && !atLeast ? "1 " + one : n.ToString("#,0", C) + " " + many) + (atLeast ? " so far" : "");

    static string Pictures(int n, bool atLeast) => n == 0 && !atLeast ? "no pictures" : Count(n, "picture", "pictures", atLeast);

    static string Rate(int kilobytesPerSecond) => kilobytesPerSecond >= 1024
        ? (kilobytesPerSecond / 1024.0).ToString("0.#", C) + " MB/s"
        : Math.Max(1, kilobytesPerSecond).ToString("#,0", C) + " KB/s";

    /// <summary>" (at most 8 MB/s)", or nothing when there is no limit.</summary>
    static string AtMost(int kilobytesPerSecond) => kilobytesPerSecond >= NoNetworkLimitKbps ? "" : $" (at most {Rate(kilobytesPerSecond)})";

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0], C) + s[1..];

    // ---- the three choices ------------------------------------------------------------------------

    /// <summary>The title of a choice, the same in setup, Settings › Indexing and the hints.</summary>
    public static string OptionTitle(ContentMode mode) => mode switch
    {
        ContentMode.Documents => "Also search inside documents",
        ContentMode.DocumentsAndPictures => "Also search inside documents and pictures' details",
        _ => "Search files by name",
    };

    /// <summary>
    /// The one-line detail of a choice: its size and its first pass, worked out from the files it covers. Null estimate =
    /// still counting. For <see cref="ContentMode.NamesOnly"/>, the estimate's IndexBytes is the text index already on disk,
    /// which that choice deletes (0 or null: none).
    /// </summary>
    public static string OptionDetail(ContentMode mode, ContentEstimate? estimate, ContentSettings settings)
    {
        if (mode == ContentMode.NamesOnly)
        {
            if (estimate is not { IndexBytes: > 0 } kept) return "Instant, as now. Uses no extra space.";
            return kept.IndexBytes < 10 * MB
                ? "Instant, as now. Choosing this deletes the text already read (under 10 MB)."
                : $"Instant, as now. Choosing this deletes the {Size(kept.IndexBytes)} of text already read.";
        }
        if (estimate is null) return "Counting your files…";
        int files = estimate.Documents + (mode == ContentMode.DocumentsAndPictures ? estimate.Pictures : 0);
        if (files == 0) return estimate.AtLeast ? "Counting your files…" : "Nothing to read in these folders yet; it takes no extra space until there is.";
        string size = SizePhrase(estimate.IndexBytes, estimate.AtLeast);
        string what = mode == ContentMode.DocumentsAndPictures
            ? $"{size} in all, with {Pictures(estimate.Pictures, estimate.AtLeast)}"
            : $"{size} for {Count(estimate.Documents, "document", "documents", estimate.AtLeast)}";
        var sb = new StringBuilder(Capital(what)).Append("; ").Append(FirstPass(estimate.FirstPass, estimate.AtLeast)).Append('.');
        if (estimate.NetworkBytesToRead >= 10 * MB)
            sb.Append($" Reads {SizePhrase(estimate.NetworkBytesToRead, estimate.AtLeast)} over the network{AtMost(settings.NetworkKilobytesPerSecond)}.");
        return sb.ToString();
    }

    /// <summary>A choice's cost in a few words for Settings' mode card: "about 28 MB for 5,230 documents, about 3 minutes".</summary>
    public static string ChoiceCost(ContentMode mode, ContentEstimate? estimate)
    {
        if (mode == ContentMode.NamesOnly) return "no extra space";
        if (estimate is null) return "counting…";
        int files = estimate.Documents + (mode == ContentMode.DocumentsAndPictures ? estimate.Pictures : 0);
        if (files == 0) return estimate.AtLeast ? "counting…" : "nothing to read yet";
        string about = estimate.AtLeast ? "at least" : "about";
        string size = SizePhrase(estimate.IndexBytes, estimate.AtLeast);
        return mode == ContentMode.DocumentsAndPictures
            ? $"{size} in all, with {Pictures(estimate.Pictures, estimate.AtLeast)}, {about} {Duration(estimate.FirstPass)}"
            : $"{size} for {Count(estimate.Documents, "document", "documents", estimate.AtLeast)}, {about} {Duration(estimate.FirstPass)}";
    }

    /// <summary>
    /// The description of Settings' mode card: every choice with its own size and time, so comparing them needs no
    /// change of setting, plus the index there is now. <paramref name="inUse"/> is the choice whose reading is done
    /// (the index up to date for these settings; NamesOnly for none): it says "in use" instead of a first pass it won't
    /// make, and the other choice says what switching reads (only the pictures, or nothing more) rather than a pass
    /// from scratch.
    /// </summary>
    /// <param name="notByName">
    /// The folders searched inside that the name index doesn't hold (ContentEstimator.FoldersNotSearchedByName): the
    /// documents choice says they're left out, and that the note under My folders adds them.
    /// </param>
    /// <param name="notCountedYet">The folders that note added, while Settings is still open: not counted until the name index holds them.</param>
    public static string ModeCardDescription(ContentEstimate? documents, ContentEstimate? pictures, long indexBytesNow,
        ContentMode inUse = ContentMode.NamesOnly, IReadOnlyList<string>? notByName = null, IReadOnlyList<string>? notCountedYet = null)
    {
        string names = indexBytesNow > 0 ? $"no extra space (deletes the {IndexNoun(indexBytesNow)} there is now)" : "no extra space";
        // "counting…" ends its sentence already.
        static string End(string s) => s.EndsWith('…') ? s : s + ".";
        string docs = ChoiceCost(ContentMode.Documents, documents), pics = ChoiceCost(ContentMode.DocumentsAndPictures, pictures);
        if (inUse == ContentMode.Documents && documents is { Documents: > 0 } d && pictures is { } p)
        {
            docs = ChoiceSize(ContentMode.Documents, d) + ", in use";
            var adds = p.FirstPass - d.FirstPass;
            pics = ChoiceSize(ContentMode.DocumentsAndPictures, p) + "; switching reads only the pictures: " +
                   (adds > TimeSpan.Zero ? $"{(p.AtLeast ? "at least" : "about")} {Duration(adds)}" : "nothing more");
        }
        else if (inUse == ContentMode.DocumentsAndPictures && documents is { } d2 && pictures is { } p2 && p2.Documents + p2.Pictures > 0)
        {
            if (d2.Documents > 0) docs = ChoiceSize(ContentMode.Documents, d2) + "; switching reads nothing more";
            pics = ChoiceSize(ContentMode.DocumentsAndPictures, p2) + ", in use";
        }
        return $"{OptionTitle(ContentMode.NamesOnly)}: {names}. " +
               $"Inside documents: {End(docs)}{LeftOutClause(notByName ?? [], inSettings: true)}{NotCountedYetClause(notCountedYet ?? [])} " +
               $"With pictures' details: {End(pics)}";
    }

    /// <summary>A choice's size and what it holds, without the time: "about 28 MB for 5,230 documents".</summary>
    static string ChoiceSize(ContentMode mode, ContentEstimate estimate)
    {
        string size = SizePhrase(estimate.IndexBytes, estimate.AtLeast);
        return mode == ContentMode.DocumentsAndPictures
            ? $"{size} in all, with {Pictures(estimate.Pictures, estimate.AtLeast)}"
            : $"{size} for {Count(estimate.Documents, "document", "documents", estimate.AtLeast)}";
    }

    /// <summary>"28 MB text index", or "text index (under 10 MB)" for a small one.</summary>
    static string IndexNoun(long bytes) => bytes < 10 * MB ? "text index (under 10 MB)" : Size(bytes) + " text index";

    // ---- progress and status -------------------------------------------------------------------

    /// <summary>The progress line of Settings › Indexing.</summary>
    public static string Progress(ContentStatus status)
    {
        string problems = status.Problems > 0 ? $" · {status.Problems.ToString("#,0", C)} couldn't be read" : "";
        string extra = status.Reason is { Length: > 0 } r && status.Phase is ContentPhase.Reading or ContentPhase.UpToDate or ContentPhase.Preparing
            ? " · " + r.TrimEnd('.') : "";
        // With pictures' details on, Done and Total count pictures too: "files" while reading, the two apart once up to date.
        string read = $"{status.Done.ToString("#,0", C)} of {status.Total.ToString("#,0", C)} {(status.WithPictures ? "files" : "documents")}";
        return status.Phase switch
        {
            ContentPhase.Off => "Off: only file names are searched.",
            ContentPhase.Preparing => "Preparing text search…" + extra,
            ContentPhase.Reading => $"Read {read} · {Size(status.IndexBytes)} so far"
                                    + (status.Remaining is { } left && left > TimeSpan.Zero ? $" · about {Duration(left)} left" : "") + problems + extra,
            ContentPhase.UpToDate => $"Up to date · {DocumentsAndPictures(status)} · {Size(status.IndexBytes)}" + problems + extra,
            ContentPhase.Paused => PausedText(status.Reason) + $" · read {read}" + problems,
            ContentPhase.Stopped => status.Reason is { Length: > 0 } why ? why.TrimEnd('.') : "Stopped reading inside files",
            _ => "",
        };
    }

    /// <summary>"26,183 documents and 9,754 pictures", "9,754 pictures" or "26,183 documents": what an up-to-date index holds.</summary>
    static string DocumentsAndPictures(ContentStatus status)
    {
        int pictures = status.WithPictures ? Math.Clamp(status.Pictures, 0, status.Total) : 0;
        int documents = status.Total - pictures;
        if (pictures == 0) return Count(documents, "document", "documents", false);
        string p = Count(pictures, "picture", "pictures", false);
        return documents > 0 ? $"{Count(documents, "document", "documents", false)} and {p}" : p;
    }

    static string PausedText(string? reason)
    {
        if (reason is not { Length: > 0 } r) return "Paused";
        r = r.Trim().TrimEnd('.');
        return r.StartsWith("Paused", StringComparison.OrdinalIgnoreCase) ? r : "Paused: " + r;
    }

    /// <summary>
    /// The status bar part; null when there is nothing to say (off, or up to date). <paramref name="resultsUseContent"/>:
    /// the results on screen depend on words inside files, so how much is read so far matters to them.
    /// </summary>
    public static string? StatusBar(ContentStatus status, bool resultsUseContent)
    {
        string progress = $"{status.Done.ToString("#,0", C)} of {status.Total.ToString("#,0", C)} read so far";
        switch (status.Phase)
        {
            case ContentPhase.Preparing:
                return "Preparing text search…";
            case ContentPhase.Reading:
                return resultsUseContent
                    ? "Inside files: " + progress
                    : $"Reading inside files {status.Done.ToString("#,0", C)} / {status.Total.ToString("#,0", C)}";
            case ContentPhase.Paused:
                string why = ShortReason(status.Reason) is { } s ? $" ({s})" : "";
                return resultsUseContent && status.Done < status.Total ? $"Inside files paused{why}: {progress}" : "Inside files paused" + why;
            case ContentPhase.Stopped:
                return "Inside files stopped";
            default:
                return null;
        }
    }

    /// <summary>A pause reason short enough for the status bar ("on battery"), or null.</summary>
    static string? ShortReason(string? reason)
    {
        if (reason is not { Length: > 0 }) return null;
        string r = reason.Trim().TrimEnd('.');
        foreach (var lead in new[] { "Paused ", "while " })
            if (r.StartsWith(lead, StringComparison.OrdinalIgnoreCase)) r = r[lead.Length..].TrimStart();
        int cut = r.IndexOfAny([':', ';', ',']);
        if (cut > 0) r = r[..cut];
        return r.Length is > 0 and <= 32 ? r : null;
    }

    /// <summary>The results' split, when plain words looked inside files: "12 by name · 1,272 inside files".</summary>
    public static string Split(int byName, int insideOnly) => $"{byName.ToString("#,0", C)} by name · {insideOnly.ToString("#,0", C)} inside files";

    /// <summary>
    /// The status bar parts search inside files adds after the window's toggles: NAMES ONLY when the window's Match inside
    /// files is off while the index is on, the split of the results, then reading progress.
    /// </summary>
    /// <param name="nameMatches">SearchResults.NameMatches of the results shown (-1: nothing looked inside files).</param>
    /// <param name="resultCount">How many results are shown.</param>
    /// <param name="status">The text index's status; null when there is none.</param>
    /// <param name="resultsUseContent">The query looks inside files.</param>
    /// <param name="explicitContent">The query has content functions of its own (content:, title:, dpi:…), which look inside files whatever the toggle says: no NAMES ONLY then.</param>
    public static IReadOnlyList<string> StatusParts(ContentMode mode, bool matchContent, int nameMatches, int resultCount,
        ContentStatus? status, bool resultsUseContent, bool explicitContent = false)
    {
        var parts = new List<string>(3);
        if (mode == ContentMode.NamesOnly) return parts;
        if (!matchContent && !explicitContent) parts.Add(NamesOnlyIndicator);
        if (nameMatches >= 0 && nameMatches < resultCount) parts.Add(Split(nameMatches, resultCount - nameMatches));
        if (status is not null && StatusBar(status, resultsUseContent) is { } s) parts.Add(s);
        return parts;
    }

    /// <summary>
    /// The status bar's right side with a tab's <paramref name="hint"/> (the choice a query needs, <see cref="LookingInside"/>):
    /// in place of <paramref name="right"/>, as an error would be, but after a disk listing's own line
    /// (<paramref name="listingLine"/>: its reading progress, or why it can't be read), which it must not hide.
    /// </summary>
    public static string StatusRight(string right, string? hint, bool listingLine)
        => hint is not { Length: > 0 } ? right : listingLine ? $"{right}   {hint}" : hint;

    /// <summary>
    /// Whether a tab's plain words look inside files: its Match inside files on, and neither Match path nor Regex on (path
    /// terms and regular expressions match names only). What the status bar (NAMES ONLY) and the Matching text column go by.
    /// </summary>
    public static bool PlainWordsLookInside(bool matchContent, bool matchPath, bool regex) => matchContent && !matchPath && !regex;

    /// <summary>
    /// Whether the window shows the Matching text column: allowed in its header menu, the index on, and Match inside files
    /// on or the query's own content functions (<paramref name="explicitContent"/>: content:, title:, dpi:… look inside
    /// files whatever the toggle, which is for plain words, says).
    /// </summary>
    public static bool ShowsMatchingText(bool allowed, ContentMode mode, bool matchContent, bool explicitContent = false)
        => allowed && mode != ContentMode.NamesOnly && (matchContent || explicitContent);

    /// <summary>Why a file's words aren't searchable.</summary>
    public static string Reason(DocStatus status) => status switch
    {
        DocStatus.Ok => "Its words are searchable",
        DocStatus.Truncated => "Only the first 1 MB of text is searchable",
        DocStatus.NoText => "No text to read (a scan or a picture)",
        DocStatus.CloudOnly => "Only online (not downloaded to this PC)",
        DocStatus.OptedOut => "Marked “don't index contents” in its properties",
        DocStatus.EfsEncrypted => "Encrypted by Windows",
        DocStatus.PasswordProtected => "Password-protected",
        DocStatus.TooBig => "Too big to read",
        DocStatus.Damaged => "Damaged or in an unusual format",
        DocStatus.Unsupported => "Uses fonts or a format the reader doesn't know",
        DocStatus.AccessDenied => "You don't have permission to open it",
        DocStatus.TimedOut => "Took too long to read",
        DocStatus.Crashed => "Reading it was interrupted when the app last closed unexpectedly; skipped until it changes",
        _ => "Couldn't be read",
    };

    /// <summary>
    /// Settings' counts of files left out or not read, by reason, one per line ("Password-protected: 3"). Files with no
    /// text (scans, pictures) are said apart: they were read, there was just nothing to find.
    /// </summary>
    public static string Problems(IReadOnlyDictionary<DocStatus, int> counts)
    {
        var lines = new List<string>();
        foreach (var (status, n) in counts.Where(c => c.Value > 0 && c.Key is not (DocStatus.Ok or DocStatus.Truncated or DocStatus.NoText)).OrderBy(c => c.Key))
            lines.Add($"{Reason(status)}: {n.ToString("#,0", C)}");
        if (counts.TryGetValue(DocStatus.NoText, out int none) && none > 0) lines.Add($"No text to read (scans and pictures, kept by their details): {none.ToString("#,0", C)}");
        if (counts.TryGetValue(DocStatus.Truncated, out int cut) && cut > 0) lines.Add($"Searchable in their first 1 MB of text only: {cut.ToString("#,0", C)}");
        return string.Join("\n", lines);
    }

    // ---- folders, offers and confirmations ---------------------------------------------------------

    /// <summary>
    /// What adding a folder costs: "Adding \\nas\jobs: about 0.8 GB more for 850,000 documents; the first pass is about
    /// 33 hours of reading …; reads about 200 GB over the network (at most 8 MB/s). Also added to the folders searched by name."
    /// </summary>
    /// <param name="usedBytes">
    /// For a count cut short (a big share listed for 20 seconds): the space its drive or share uses in all, so the "at
    /// least" figures can be weighed against it; 0 = unknown.
    /// </param>
    public static string AddedFolder(string folder, ContentEstimate estimate, bool alsoAddedToNames, int networkKilobytesPerSecond = 8192, long usedBytes = 0,
        bool extrapolated = false)
    {
        string names = alsoAddedToNames ? " Also added to the folders searched by name." : "";
        string used = extrapolated && usedBytes > 0
            ? $" Counting stopped early, so this is worked out from the {Size(usedBytes)} it holds in all; Settings counts it in full once it is searched by name."
            : estimate.AtLeast && usedBytes > 0
            ? $" Counting stopped early; the drive or share it is on holds {Size(usedBytes)} in all. Settings counts it in full once it is searched by name."
            : "";
        int files = estimate.Documents + (estimate.Mode == ContentMode.DocumentsAndPictures ? estimate.Pictures : 0);
        if (files == 0)
            return estimate.AtLeast ? $"Adding {folder}: counting its files…{names}" : $"Adding {folder}: nothing to read there yet.{names}";
        var sb = new StringBuilder($"Adding {folder}: {SizePhrase(estimate.IndexBytes, estimate.AtLeast)} more for ");
        bool pictures = estimate.Mode == ContentMode.DocumentsAndPictures;
        sb.Append(Count(files, pictures ? "file" : "document", pictures ? "files" : "documents", estimate.AtLeast));
        sb.Append("; ").Append(FirstPass(estimate.FirstPass, estimate.AtLeast));
        if (estimate.NetworkBytesToRead >= 10 * MB)
            sb.Append($"; reads {SizePhrase(estimate.NetworkBytesToRead, estimate.AtLeast)} over the network{AtMost(networkKilobytesPerSecond)}");
        return sb.Append('.').Append(used).Append(names).ToString();
    }

    /// <summary>
    /// What adding a folder costs when a folder searched inside already holds it: nothing ("Adding C:\Users\Me\Documents\
    /// Clients: already searched inside, as part of My folders."). <paramref name="coveredBy"/> null = one of My folders.
    /// </summary>
    public static string AddedFolderCovered(string folder, string? coveredBy, bool alsoAddedToNames)
    {
        string names = alsoAddedToNames ? " Also added to the folders searched by name." : "";
        string where = coveredBy is null ? "as part of My folders" : $"inside {coveredBy}";
        return $"Adding {folder}: already searched inside, {where}; nothing more to read.{names}";
    }

    /// <summary>The hint when a query needs a choice that is off, quoting the choice as Settings lists it; empty for none.</summary>
    public static string ModeHint(ContentMode needs) => needs switch
    {
        ContentMode.Documents => $"Words inside files need “{OptionTitle(ContentMode.Documents)}” (Settings › Indexing)",
        ContentMode.DocumentsAndPictures => $"Picture details need “{OptionTitle(ContentMode.DocumentsAndPictures)}” (Settings › Indexing)",
        _ => "",
    };

    /// <summary>
    /// What an empty list says after a search when search inside files knows why it is empty: the choice the query needs
    /// (<paramref name="needs"/> above <paramref name="mode"/>: it can't match, whatever else is set), else
    /// <see cref="LookingInside"/> while lookups still run (matches may yet come in); null when it knows nothing more.
    /// </summary>
    public static string? EmptyListText(ContentMode needs, ContentMode mode, bool lookingInside)
        => needs > mode && ModeHint(needs) is { Length: > 0 } hint ? $"No items match your search. {hint}."
            : lookingInside ? LookingInside : null;

    /// <summary>
    /// Whether a window offers search inside files: never answered, no mode chosen (or names only), not the demo, and the
    /// name index ready (the offer never turns reading on by itself).
    /// </summary>
    public static bool ShowsOffer(bool asked, ContentMode mode, bool modeExplicit, bool demo, IndexState names)
        => !asked && (!modeExplicit || mode == ContentMode.NamesOnly) && !demo && names == IndexState.Ready;

    /// <summary>
    /// The offer bar: "Search inside your documents too? About 28 MB; the first pass takes about 3 minutes in the
    /// background.", then, when some of the folders searched inside aren't searched by name (<paramref name="notByName"/>),
    /// " Documents and Pictures would be left out (not searched by name)."
    /// </summary>
    public static string Offer(ContentEstimate? documents, IReadOnlyList<string>? notByName = null)
    {
        const string ask = "Search inside your documents too?";
        string leftOut = notByName is { Count: > 0 } n ? $" {FolderList(n)} would be left out (not searched by name)." : "";
        if (documents is not { Documents: > 0 } e) return ask + " The words in your PDFs, Office files and other documents become searchable." + leftOut;
        return $"{ask} {Capital(SizePhrase(e.IndexBytes, e.AtLeast))}; {FirstPass(e.FirstPass, e.AtLeast)}.{leftOut}";
    }

    // ---- folders the name index doesn't hold -------------------------------------------------------

    /// <summary>
    /// The folders as the notes name them: by their last part ("Documents"), a drive or share by its whole path ("E:\",
    /// "\\nas\jobs"), and folders whose last parts are alike by their whole paths.
    /// </summary>
    static List<string> FolderNames(IReadOnlyList<string> folders)
    {
        var names = new List<string>(folders.Count);
        foreach (var folder in folders)
        {
            string f = folder.Trim(), t = f.TrimEnd('\\');
            string name = Path.GetFileName(t);
            bool root = name.Length == 0 || string.Equals(Path.GetPathRoot(f)?.TrimEnd('\\'), t, StringComparison.OrdinalIgnoreCase);
            names.Add(root ? (t.Length == 2 && t[1] == ':' ? t + "\\" : t) : name);
        }
        var alike = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            if (alike.Contains(names[i])) names[i] = folders[i].Trim();
        return names;
    }

    /// <summary>
    /// The folders in words: "Documents", "Documents and Pictures", "Documents, Pictures and OneDrive", then at most three
    /// named: "Documents, Pictures, OneDrive and 2 more".
    /// </summary>
    public static string FolderList(IReadOnlyList<string> folders)
    {
        var names = FolderNames(folders);
        return names.Count switch
        {
            0 => "",
            1 => names[0],
            <= 3 => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
            _ => string.Join(", ", names.Take(3)) + $" and {(names.Count - 3).ToString("#,0", C)} more",
        };
    }

    /// <summary>
    /// Settings' note under My folders, for the folders searched inside that the name index doesn't hold
    /// (ContentEstimator.FoldersNotSearchedByName): "Documents and Pictures aren't searched by name, so their insides
    /// aren't read either." Empty for none.
    /// </summary>
    public static string NotSearchedByName(IReadOnlyList<string> folders) => folders.Count switch
    {
        0 => "",
        1 => $"{FolderList(folders)} isn't searched by name, so its insides aren't read either.",
        _ => $"{FolderList(folders)} aren't searched by name, so their insides aren't read either.",
    };

    /// <summary>The note's button, which adds the folders to those searched by name: "Search them by name too".</summary>
    public static string SearchByNameButton(int folders) => folders == 1 ? "Search it by name too" : "Search them by name too";

    /// <summary>
    /// The note once its button added them (the name index takes them when Settings closes): "Documents and Pictures are
    /// added to the folders searched by name; their insides are read after you close Settings." Empty for none.
    /// </summary>
    public static string AddedToNames(IReadOnlyList<string> folders) => folders.Count switch
    {
        0 => "",
        1 => $"{FolderList(folders)} is added to the folders searched by name; its insides are read after you close Settings.",
        _ => $"{FolderList(folders)} are added to the folders searched by name; their insides are read after you close Settings.",
    };

    /// <summary>
    /// What the documents choice's detail ends with while folders searched inside aren't searched by name: " (Documents and
    /// Pictures aren't searched by name, so they're left out; Settings › Indexing can add them.)" in setup; in Settings
    /// (<paramref name="inSettings"/>) it points at the note's button below instead. Empty for none.
    /// </summary>
    public static string LeftOutClause(IReadOnlyList<string> folders, bool inSettings = false)
    {
        if (folders.Count == 0) return "";
        bool one = folders.Count == 1;
        string them = one ? "it" : "them";
        string add = inSettings ? $"“{SearchByNameButton(folders.Count)}” below adds {them}" : $"Settings › Indexing can add {them}";
        return one
            ? $" ({FolderList(folders)} isn't searched by name, so it's left out; {add}.)"
            : $" ({FolderList(folders)} aren't searched by name, so they're left out; {add}.)";
    }

    /// <summary>
    /// The documents choice in Settings once the note's button added them, until Settings closes and the name index lists
    /// them: " (Documents and Pictures aren't counted until they're searched by name, after you close Settings.)"
    /// </summary>
    public static string NotCountedYetClause(IReadOnlyList<string> folders) => folders.Count switch
    {
        0 => "",
        1 => $" ({FolderList(folders)} isn't counted until it's searched by name, after you close Settings.)",
        _ => $" ({FolderList(folders)} aren't counted until they're searched by name, after you close Settings.)",
    };

    /// <summary>The offer bar when an index from before was kept (an older version dropped the setting).</summary>
    public static string OfferKept(KeptIndex kept) => $"Your text index from before is still here ({Size(kept.Bytes)}). Turn search inside files back on?";

    /// <summary>Asked when names only is picked: "Delete the 28 MB text index? Searching inside files stops."</summary>
    public static string ConfirmNamesOnly(long indexBytes) => $"Delete the {IndexNoun(indexBytes)}? Searching inside files stops.";

    /// <summary>
    /// Asked before folders, My folders or a kind leave the scope with many documents: "Stop searching inside \\nas\jobs?
    /// 850,000 documents (0.8 GB) leave the text index now; reading them again later takes about 33 hours and 200 GB over the network."
    /// </summary>
    /// <param name="what">"\\nas\jobs", "My folders", "PDF files".</param>
    /// <param name="readAgain">The cost of reading them again (its IndexBytes is what is deleted); null when unknown.</param>
    public static string ConfirmRemoval(string what, int documents, ContentEstimate? readAgain)
        => ConfirmRemoval(what, Capital(Count(documents, "document", "documents", false)), readAgain);

    /// <summary>
    /// The same question for what a change takes out (<see cref="Removed"/>): its documents and, when pictures' details
    /// leave too, its pictures ("1,200 documents and 14,000 pictures").
    /// </summary>
    public static string ConfirmRemoval(string what, ContentEstimate removed)
    {
        int pictures = removed.Mode == ContentMode.DocumentsAndPictures ? removed.Pictures : 0;
        string count = removed.Documents > 0 && pictures > 0
            ? $"{Count(removed.Documents, "document", "documents", false)} and {Count(pictures, "picture", "pictures", false)}"
            : pictures > 0 ? Count(pictures, "picture", "pictures", false) : Count(removed.Documents, "document", "documents", false);
        return ConfirmRemoval(what, Capital(count), removed);
    }

    /// <summary>
    /// What a change of scope takes out of the text index: the estimate of the scope before it less the estimate after
    /// (each with its own mode), so documents still covered another way (My folders, another folder, pictures that don't
    /// follow the kinds) don't count. Its Pictures count only when its Mode is DocumentsAndPictures; nothing is negative.
    /// </summary>
    public static ContentEstimate Removed(ContentEstimate before, ContentEstimate after)
    {
        static long Less(long a, long b) => Math.Max(0, a - b);
        int pictures = (int)Less(before.Mode == ContentMode.DocumentsAndPictures ? before.Pictures : 0,
                                 after.Mode == ContentMode.DocumentsAndPictures ? after.Pictures : 0);
        var time = before.FirstPass > after.FirstPass ? before.FirstPass - after.FirstPass : TimeSpan.Zero;
        return new ContentEstimate(pictures > 0 ? ContentMode.DocumentsAndPictures : ContentMode.Documents,
            (int)Less(before.Documents, after.Documents), pictures, Less(before.IndexBytes, after.IndexBytes), time,
            Less(before.NetworkBytesToRead, after.NetworkBytesToRead), AtLeast: before.AtLeast || after.AtLeast);
    }

    /// <summary>The documents and pictures a <see cref="Removed"/> estimate takes out (the confirmations ask above 1,000).</summary>
    public static int RemovedCount(ContentEstimate removed)
        => (int)Math.Min(int.MaxValue, (long)removed.Documents + (removed.Mode == ContentMode.DocumentsAndPictures ? removed.Pictures : 0));

    static string ConfirmRemoval(string what, string count, ContentEstimate? readAgain)
    {
        string size = readAgain is { IndexBytes: > 0 } e ? $" ({(e.IndexBytes < 10 * MB ? "under 10 MB" : Size(e.IndexBytes))})" : "";
        var sb = new StringBuilder($"Stop searching inside {what}? {count}{size} leave the text index now");
        if (readAgain is { } r)
        {
            sb.Append($"; reading them again later takes about {Duration(r.FirstPass)}");
            if (r.NetworkBytesToRead >= 10 * MB) sb.Append($" and {Size(r.NetworkBytesToRead)} over the network");
        }
        return sb.Append('.').ToString();
    }

    // ---- snippets ------------------------------------------------------------------------------

    /// <summary>The name of a field as the snippet and the preview banner show it.</summary>
    public static string FieldName(DocField field) => field switch
    {
        DocField.CameraMake => "Camera make",
        DocField.CameraModel => "Camera model",
        DocField.ColorProfile => "Colour profile",
        DocField.CreatorTool => "Made with",
        DocField.Sender => "From",
        DocField.Recipients => "To",
        _ => field.ToString(),
    };

    /// <summary>
    /// Where a snippet is, drawn before it in a secondary colour: "p. 4 · ", "Slide 3 · ", "Sheet "2024" · ",
    /// "Line 120 · ", "Keywords · "; empty when nothing says more.
    /// </summary>
    public static string SnippetPrefix(ContentSnippet snippet)
    {
        if (snippet.Field is { } f) return FieldName(f) + " · ";
        if (snippet.Sheet is { Length: > 0 } sheet) return $"Sheet \"{sheet}\" · ";
        if (snippet.Line > 0) return $"Line {snippet.Line.ToString("#,0", C)} · ";
        if (snippet.Page > 0)
            return snippet.Kind is ContentKind.Pdf or ContentKind.Design ? $"p. {snippet.Page.ToString("#,0", C)} · " : $"Slide {snippet.Page.ToString("#,0", C)} · ";
        return "";
    }

    /// <summary>
    /// A snippet's text and bold ranges. The place prefix is never part of the snippet's text (SnippetFormatter leaves it
    /// out; <see cref="SnippetPrefix"/> gives it), so the document's own words are kept as they are, even when they happen
    /// to start like a prefix.
    /// </summary>
    public static (string Text, IReadOnlyList<(int Start, int Length)> Bold) SnippetBody(ContentSnippet snippet) => (snippet.Text, snippet.Highlights);

    /// <summary>
    /// A Matching text line started close to its first bold word, so a narrow cell still shows the match: at most
    /// <paramref name="keepWords"/> words and <paramref name="keepChars"/> characters before it, from the start of a word,
    /// with "…" in front; the bold ranges move with it. The line is unchanged when the match is that near its start
    /// already. Without a space within reach (Japanese, Chinese, a long URL), it starts at the nearest character or word
    /// edge the snippet itself may cut at, never inside a character. The tooltip keeps the whole line.
    /// </summary>
    public static (string Text, IReadOnlyList<(int Start, int Length)> Bold) StartNearMatch(string text, IReadOnlyList<(int Start, int Length)> bold,
        int keepChars = 20, int keepWords = 3)
    {
        int first = int.MaxValue;
        foreach (var (start, length) in bold)
            if (length > 0 && start >= 0 && start < text.Length && start < first) first = start;
        if (first == int.MaxValue) return (text, bold);
        keepChars = Math.Max(0, keepChars);
        keepWords = Math.Max(0, keepWords);
        if (first <= keepChars + 1 && WordsIn(text, 0, first) <= keepWords) return (text, bold);
        int lo = Math.Max(1, first - keepChars);
        int from = -1;
        // The start of a word after a space: the earliest within reach that leaves at most keepWords words.
        for (int i = lo; i <= first && from < 0; i++)
            if (char.IsWhiteSpace(text[i - 1]) && !char.IsWhiteSpace(text[i]) && WordsIn(text, i, first) <= keepWords) from = i;
        // No space within reach: any edge the snippet may cut at (between characters of a script written without
        // spaces, or at punctuation), else the match itself.
        for (int i = lo; i <= first && from < 0; i++)
            if (!char.IsWhiteSpace(text[i]) && !Search.SnippetFormatter.InsideWord(text, i) && WordsIn(text, i, first) <= keepWords) from = i;
        if (from < 0) from = first;
        int shift = 1 - from;
        var moved = new List<(int, int)>(bold.Count);
        foreach (var (start, length) in bold)
        {
            int s = Math.Max(1, start + shift), e = start + length + shift;
            if (e > s) moved.Add((s, e - s));
        }
        return ("…" + text[from..], moved);
    }

    /// <summary>The words (runs of characters between spaces) in text[start, end).</summary>
    static int WordsIn(string text, int start, int end)
    {
        int words = 0;
        bool inWord = false;
        for (int i = start; i < end; i++)
        {
            bool space = char.IsWhiteSpace(text[i]);
            if (!space && !inWord) words++;
            inWord = !space;
        }
        return words;
    }

    /// <summary>The tooltip of a result's Matching text: the whole line, then where it is ("Page 4 of 12").</summary>
    public static string SnippetToolTip(ContentSnippet snippet)
    {
        string text = SnippetBody(snippet).Text.Trim();
        string? where = null;
        if (snippet.Field is { } f) where = FieldName(f);
        else if (snippet.Sheet is { Length: > 0 } sheet) where = $"Sheet \"{sheet}\"";
        else if (snippet.Line > 0) where = $"Line {snippet.Line.ToString("#,0", C)}";
        else if (snippet.Page > 0)
        {
            string unit = snippet.Kind is ContentKind.Pdf or ContentKind.Design ? "Page" : "Slide";
            where = snippet.Pages >= snippet.Page
                ? $"{unit} {snippet.Page.ToString("#,0", C)} of {snippet.Pages.ToString("#,0", C)}"
                : $"{unit} {snippet.Page.ToString("#,0", C)}";
        }
        return where is null ? text : text + "\n" + where;
    }

    /// <summary>
    /// The preview's banner for a snippet, with its bold ranges: "Keywords: sunset, beach" for a field, the picture
    /// details for a picture, "Page 4: …invoice…" for a PDF page, "Message: …" for an email's text (the preview shows the
    /// message through its handler) and "Text: …" for a design file's text layers or stories (the preview shows the
    /// picture); null where the preview highlights in place (text, Office).
    /// </summary>
    public static (string? Text, IReadOnlyList<(int Start, int Length)> Bold) Banner(ContentSnippet snippet)
    {
        var (text, bold) = SnippetBody(snippet);
        string? lead = snippet.Field is { } f ? FieldName(f) + ": "
            : snippet.Kind == ContentKind.Picture ? ""
            : snippet.Page > 0 && snippet.Kind is ContentKind.Pdf or ContentKind.Design ? $"Page {snippet.Page.ToString("#,0", C)}: "
            : snippet.Kind == ContentKind.Mail ? "Message: "
            : snippet.Kind == ContentKind.Design ? "Text: "
            : null;
        if (lead is null) return (null, []);
        if (lead.Length == 0) return (text, bold);
        return (lead + text, [.. bold.Select(b => (b.Start + lead.Length, b.Length))]);
    }
}

/// <summary>
/// The content_* keys of RockPreview.ini, read and written for AppSettings here so the codec is tested in Core. Values are
/// whitelisted: an unknown or missing value means the default (and an unknown content_mode means no mode chosen).
/// </summary>
public static class ContentIni
{
    /// <summary>The ini names of the kinds of document, in the order Settings lists them.</summary>
    public static IReadOnlyList<(ContentKinds Kind, string Name)> KindNames { get; } =
    [
        (ContentKinds.Pdf, "pdf"), (ContentKinds.Office, "office"), (ContentKinds.Text, "text"), (ContentKinds.Mail, "mail"), (ContentKinds.Design, "design"),
    ];

    /// <summary>
    /// Reads the content_* keys into <paramref name="s"/>. <paramref name="value"/> returns a key's raw value, or null when
    /// it is missing. The demo's ini (<paramref name="demo"/>) starts with documents and pictures and .ini files as text,
    /// so its sample index and win.ini can be searched inside.
    /// </summary>
    public static void Load(ContentSettings s, Func<string, string?> value, bool demo)
    {
        string? mode = value("content_mode");
        if (mode is null && demo) { s.Mode = ContentMode.DocumentsAndPictures; s.ModeExplicit = true; }
        else s.LoadMode(mode);
        s.UseMyFolders = Bool(value("content_my_folders"), s.UseMyFolders);
        s.Folders = List(value("content_folders")) ?? s.Folders;
        s.ExcludeFolders = List(value("content_exclude_folders")) ?? s.ExcludeFolders;
        if (value("content_kinds") is { } kinds)
        {
            var parts = List(kinds) ?? [];
            ContentKinds k = ContentKinds.None;
            bool known = parts.Count == 0; // an empty value is every kind switched off
            foreach (var p in parts)
                foreach (var (kind, name) in KindNames)
                    if (p.Equals(name, StringComparison.OrdinalIgnoreCase)) { k |= kind; known = true; }
            if (known) s.Kinds = k;
        }
        s.ExtraTextExtensions = List(value("content_extensions")) ?? (demo ? ["ini"] : s.ExtraTextExtensions);
        s.MaxFileSizeMB = Int(value("content_max_size_mb"), s.MaxFileSizeMB, min: 0);
        s.ReadOnBattery = Bool(value("content_read_on_battery"), s.ReadOnBattery);
        s.ReadEncrypted = Bool(value("content_read_encrypted"), s.ReadEncrypted);
        s.NetworkKilobytesPerSecond = Int(value("content_network_kbps"), s.NetworkKilobytesPerSecond, min: 1);
        s.OcrFolders = List(value("content_ocr_folders")) ?? s.OcrFolders;
    }

    /// <summary>Writes the content_* keys; content_mode only once a mode was chosen (an unchosen one must stay unchosen).</summary>
    public static void Save(ContentSettings s, Action<string, string> write)
    {
        if (s.ModeToSave() is { } mode) write("content_mode", mode);
        write("content_my_folders", s.UseMyFolders ? "1" : "0");
        write("content_folders", Join(s.Folders));
        write("content_exclude_folders", Join(s.ExcludeFolders));
        write("content_kinds", string.Join('|', KindNames.Where(k => (s.Kinds & k.Kind) != 0).Select(k => k.Name)));
        write("content_extensions", Join(s.ExtraTextExtensions));
        write("content_max_size_mb", s.MaxFileSizeMB.ToString(CultureInfo.InvariantCulture));
        write("content_read_on_battery", s.ReadOnBattery ? "1" : "0");
        write("content_read_encrypted", s.ReadEncrypted ? "1" : "0");
        write("content_network_kbps", s.NetworkKilobytesPerSecond.ToString(CultureInfo.InvariantCulture));
        write("content_ocr_folders", Join(s.OcrFolders));
    }

    static string Join(IEnumerable<string> list) => string.Join('|', list.Select(x => x.Replace('|', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim()).Where(x => x.Length > 0));

    /// <summary>A 1/0 switch: 1, 0, true or false in any case; anything else (or nothing) is the default.</summary>
    public static bool Bool(string? v, bool def)
    {
        if (v is null) return def;
        string t = v.Trim();
        if (t == "1" || t.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (t == "0" || t.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return def;
    }

    static int Int(string? v, int def, int min)
        => v is not null && int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= min ? i : def;

    static List<string>? List(string? v) => v?.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

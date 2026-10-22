namespace Anything.Core.Content;

/// <summary>What setup and Settings › Indexing offer: names only (today), also the words in documents, or that plus pictures' details.</summary>
public enum ContentMode : byte { NamesOnly, Documents, DocumentsAndPictures }

/// <summary>Kinds of document whose words are read (Settings › Indexing › Kinds of files); pictures follow <see cref="ContentMode"/>.</summary>
[Flags]
public enum ContentKinds : byte { None = 0, Pdf = 1, Office = 2, Text = 4, Mail = 8, Design = 16, All = Pdf | Office | Text | Mail | Design }

/// <summary>The kind of a file (ContentTypes), stored per document (append only).</summary>
public enum ContentKind : byte { None, Pdf, Office, Text, Mail, Design, Picture }

/// <summary>How reading a file went: Ok, Truncated and NoText were read; CloudOnly, OptedOut and EfsEncrypted were left out by rule; the rest are problems.</summary>
public enum DocStatus : byte { Ok, Truncated, NoText, CloudOnly, OptedOut, EfsEncrypted, PasswordProtected, TooBig, Damaged, Unsupported, AccessDenied, TimedOut, Crashed }

/// <summary>Colour model from the file's own header (colormode:, cmyk:, rgb:), never EXIF ColorSpace.</summary>
public enum ColorMode : byte { Unknown, Bitmap, Gray, Indexed, Rgb, Cmyk, Lab, Multichannel, Duotone }

/// <summary>Named text besides the body; its words are plain words too, and searchable through the field's own function.</summary>
public enum DocField : byte { Title = 1, Subject, Author, Keywords, Comment, CameraMake, CameraModel, ColorProfile, CreatorTool, Fonts, Plates, Location, Sender, Recipients }

/// <summary>Picture details read from headers only, never pixels; 0 = unknown.</summary>
/// <param name="Orientation">EXIF orientation 1-8; 5-8 swap width and height for orientation: and dimensions:.</param>
/// <param name="ExifColorSpace">EXIF ColorSpace: 1 sRGB, 2 uncalibrated, 0 absent (colorspace:).</param>
public readonly record struct PictureInfo(int Width, int Height, int DpiX, int DpiY, byte BitDepth, ColorMode ColorMode,
    byte Orientation, byte ExifColorSpace, byte Rating, long DateTakenUtc, bool HasGps, int LatitudeMicro, int LongitudeMicro,
    int Iso, float FNumber, float ExposureSeconds, float FocalLengthMm)
{
    /// <summary>Width and height as shown: EXIF orientations 5-8 turn the picture a quarter turn.</summary>
    public (int Width, int Height) Shown => Orientation is >= 5 and <= 8 ? (Height, Width) : (Width, Height);
}

/// <summary>Hard limits for one file; reaching one stops reading (Truncated, or TimedOut/TooBig when nothing was read).</summary>
public sealed record ExtractLimits(int MaxTextChars = 1 << 20, int MaxPages = 2000, long MaxBytesRead = 64L << 20,
    long MaxDecodedBytes = 128L << 20, long MaxStreamBytes = 16L << 20, int MaxMilliseconds = 15_000)
{
    /// <summary>Limits for local files.</summary>
    public static ExtractLimits Local { get; } = new();
    /// <summary>Limits for network files: more time, fewer bytes.</summary>
    public static ExtractLimits Network { get; } = new(MaxBytesRead: 32L << 20, MaxMilliseconds: 60_000);
}

/// <summary>One file to read; the expected stamp is what the name index has (size -1 / date 0 = unknown).</summary>
/// <param name="MaxFileBytes">Documents larger than this are TooBig without being opened (pictures never are); 0 = no limit.</param>
public sealed record ExtractRequest(string FullPath, ContentKind Kind, long ExpectedSize, long ExpectedModifiedUtc,
    bool Network, bool ReadEncrypted, long MaxFileBytes, ExtractLimits Limits)
{
    /// <summary>
    /// The per-share speed limit this read waits on before each network read (the indexer's scheduler sets it, keyed by
    /// the root's volume key); null = the source's own default. Sources that read no real files ignore it.
    /// </summary>
    internal Extract.NetworkThrottle? Throttle { get; init; }
}

/// <summary>What was read from one file.</summary>
public sealed class ExtractedDoc
{
    /// <summary>The file's kind (the one it was read as; ContentTypes decides it).</summary>
    public required ContentKind Kind { get; init; }
    /// <summary>How reading went.</summary>
    public required DocStatus Status { get; init; }
    /// <summary>
    /// NFC body: '\n' between lines; '\f' between pages and slides, never before the first (a PDF whose first page has no
    /// text starts with '\f'). A workbook (xlsx, ods) is one page per sheet, each starting with the sheet's name on its own
    /// line; the first sheet opens the body with no '\f' before it (ExtractSink.StartSheet writes both). Empty unless the
    /// status is Ok or Truncated.
    /// </summary>
    public string Body { get; init; } = "";
    /// <summary>Named text (title, keywords, camera …), in reading order, at most one value per field.</summary>
    public IReadOnlyList<(DocField Field, string Value)> Fields { get; init; } = [];
    /// <summary>Picture details (pictures, PSD and other design files); null when none.</summary>
    public PictureInfo? Picture { get; init; }
    /// <summary>Pages, slides or sheets (pagecount:); 0 = unknown.</summary>
    public int Pages { get; init; }
    /// <summary>Version of the reader that produced it (compared per reader by ExtractorChanges).</summary>
    public byte ExtractorVersion { get; init; }
    /// <summary>Bytes actually read from the file (the network cost).</summary>
    public long BytesRead { get; init; }
    /// <summary>Time spent reading, throttle waits excluded.</summary>
    public int ElapsedMs { get; init; }
    /// <summary>A file that gave nothing, with the reason.</summary>
    public static ExtractedDoc Failed(ContentKind kind, DocStatus status, byte version = 0) => new() { Kind = kind, Status = status, ExtractorVersion = version };
}

/// <summary>One read, with the size and date seen on disk around it.</summary>
/// <param name="Retry">Temporary trouble (in use, changed while read): record nothing, try later.</param>
/// <param name="Offline">The drive or share didn't answer: back its root off, record nothing.</param>
public readonly record struct ContentRead(ExtractedDoc Doc, long Size, long ModifiedUtc, bool Retry, bool Offline);

/// <summary>Reads files' insides for the indexer: the real readers (ContentServices.Files), made-up text (demo) or fakes (tests).</summary>
public interface IContentSource
{
    /// <summary>Reads one file; never throws for file problems (status, Retry or Offline), only OperationCanceledException.</summary>
    ContentRead Read(ExtractRequest request, CancellationToken ct);
    /// <summary>Size, date and attributes on disk now; false when the file can't be stat'ed.</summary>
    bool TryStat(string fullPath, out long size, out long modifiedUtc, out FileAttributes attributes);
}

/// <summary>What search inside files is doing.</summary>
public enum ContentPhase : byte { Off, Preparing, Reading, UpToDate, Paused, Stopped }

/// <summary>Progress for the status bar and Settings; Done and Total count documents in scope, and pictures with pictures' details on (problems included in Done).</summary>
/// <param name="Reason">Why paused or stopped (or "finishing deleting…"), in plain words; null otherwise.</param>
/// <param name="Linked">Documents are mapped to the current name index (false while catching up after a rebuild).</param>
public sealed record ContentStatus(ContentPhase Phase, int Done, int Total, int Problems, int NoText, long IndexBytes,
    TimeSpan? Remaining, string? Reason, bool Linked)
{
    /// <summary>Search inside files is off.</summary>
    public static ContentStatus Off { get; } = new(ContentPhase.Off, 0, 0, 0, 0, 0, null, null, false);

    /// <summary>Pictures' details are read too (ContentMode.DocumentsAndPictures): Done and Total count pictures as well.</summary>
    public bool WithPictures { get; init; }

    /// <summary>The pictures among Total, counted from the index's rows (as Problems is); 0 unless <see cref="WithPictures"/>.</summary>
    public int Pictures { get; init; }
}

/// <summary>A text index left on disk while no mode is chosen (an older build dropped content_mode): kept, not followed, until the user decides.</summary>
/// <param name="Mode">The mode it was built for (recorded in its manifest), to offer back.</param>
/// <param name="Bytes">Its size on disk.</param>
public readonly record struct KeptIndex(ContentMode Mode, long Bytes);

/// <summary>The line a result matched, from the index only.</summary>
/// <param name="Text">The line itself, without its place prefix (ContentPresentation.SnippetPrefix: "p. 4 · ", "Keywords · "), which the app draws apart.</param>
/// <param name="Highlights">Matching ranges in Text.</param>
/// <param name="Page">1-based page, slide or sheet; 0 = none.</param>
/// <param name="Line">1-based line in the body, only for files whose lines the preview shows as stored (txt, text, md, markdown and the user's extra text extensions); 0 = none.</param>
/// <param name="Sheet">The sheet's name (xlsx, ods); null = none.</param>
/// <param name="Field">The field the match is in; null = the body.</param>
public sealed record ContentSnippet(string Text, IReadOnlyList<(int Start, int Length)> Highlights, ContentKind Kind,
    int Page, int Line, string? Sheet, DocField? Field)
{
    /// <summary>Pages, slides or sheets in the document (its row's page count), for "Page 4 of 12"; 0 = unknown.</summary>
    public int Pages { get; init; }
}

/// <summary>A match on a PDF page, in 0..1 of the shown page (CropBox, rotation applied), origin top-left.</summary>
public readonly record struct PageBox(float X, float Y, float Width, float Height);

/// <summary>Files and bytes of one kind in a scope.</summary>
public readonly record struct KindTally(int Files, long Bytes, int NetworkFiles, long NetworkBytes)
{
    /// <summary>
    /// The bytes with each file counted at most 1 MB (ContentEstimator.RatioCapBytes: the text a file can give the index),
    /// what the ratio estimator of stored text multiplies; 0 = not counted (the per-file prior then).
    /// </summary>
    public long CappedBytes { get; init; }
}

/// <summary>
/// What a scope holds, plus sample paths per kind for calibration. Documents over the size limit aren't counted (they are
/// never read); pictures always are.
/// </summary>
public sealed record ContentCensus(IReadOnlyDictionary<ContentKind, KindTally> Kinds,
    IReadOnlyDictionary<ContentKind, IReadOnlyList<string>> Samples, int CloudOnly, bool Complete)
{
    /// <summary>
    /// Bytes of every file a folder listing went through so far, of any kind (0 = not counted, as for the name index's
    /// census): a count cut short can be scaled up to what its drive or share holds (ContentEstimator.Extrapolate).
    /// </summary>
    public long ListedBytes { get; init; }
}

/// <summary>Average cost per file of one kind, over all files of the kind (those without text included).</summary>
public readonly record struct KindCost(double MsPerFile, double StoredBytesPerFile, double WordsPerFile, double BytesReadPerFile);

/// <summary>Cost per kind and how many files each was measured on (0 = built-in prior).</summary>
public sealed record ContentCalibration(IReadOnlyDictionary<ContentKind, KindCost> Costs, IReadOnlyDictionary<ContentKind, int> Measured)
{
    /// <summary>Time per network file beyond reading it and moving its bytes at the throttle (opening, round trips), in ms; 5 ms built in.</summary>
    public double NetworkOverheadMs { get; init; } = 5;
    /// <summary>How many network files <see cref="NetworkOverheadMs"/> was measured on (0 = built-in prior).</summary>
    public int NetworkMeasured { get; init; }
    /// <summary>
    /// The kinds whose stored text is priced as a ratio (ContentEstimator.PricedByRatio), with their samples' sums; a kind
    /// missing here (no samples, or priced from the index's own statistics) is priced per file.
    /// </summary>
    public IReadOnlyDictionary<ContentKind, StoredRatio> StoredRatios { get; init; } = new Dictionary<ContentKind, StoredRatio>();
}

/// <summary>A kind's calibration samples: how many, their stored text and their files' bytes counted at most 1 MB each.</summary>
public readonly record struct StoredRatio(int Samples, double StoredBytes, double CappedFileBytes);

/// <summary>What one choice would cost.</summary>
public sealed record ContentEstimate(ContentMode Mode, int Documents, int Pictures, long IndexBytes, TimeSpan FirstPass,
    long NetworkBytesToRead, bool AtLeast);

/// <summary>
/// Search inside files as the app sees it (real or in memory); events fire on background threads. Every entry point that
/// takes <see cref="ContentSettings"/> copies them (<see cref="ContentSettings.Clone"/>) before it returns or first awaits,
/// so Settings can go on editing its object. Commands (Apply, Pause, Resume, Rebuild, RetryProblems) are queued to the
/// indexer's own thread in order, return at once, and are ignored after <see cref="Stop"/>.
/// </summary>
public interface IContentIndex : IDisposable
{
    /// <summary>Where it stands; cheap and safe from the UI thread.</summary>
    ContentStatus Status { get; }
    /// <summary>Progress, phase or counts changed (at most once a second).</summary>
    event Action? StatusChanged;
    /// <summary>
    /// Search again where results used content: new words are searchable or the index caught up (debounced 3 s), or a
    /// lookup that ran past its budget finished (at once, coalesced).
    /// </summary>
    event Action? ContentChanged;
    /// <summary>
    /// A text index left on disk while the mode is unset (<see cref="ContentSettings.ModeExplicit"/> false), with the mode
    /// recorded in it; null when there is none or a mode was chosen. Call off the UI thread the first time.
    /// </summary>
    KeptIndex? Kept { get; }
    /// <summary>
    /// Attaches to names.Index and follows it with a copy of the settings; returns at once. An explicit NamesOnly deletes an
    /// index left on disk; an unset mode keeps it without following it (see <see cref="Kept"/>).
    /// </summary>
    void Start(IIndexManager names, ContentSettings settings);
    /// <summary>Takes a copy of changed settings and applies it in the background; an explicit NamesOnly stops and deletes the index.</summary>
    void Apply(ContentSettings settings);
    /// <summary>Pauses reading for a while (null = until Resume or restart); following renames and deletes goes on.</summary>
    void Pause(TimeSpan? forHowLong);
    /// <summary>Resumes reading after Pause.</summary>
    void Resume();
    /// <summary>Deletes the text index and reads everything in scope again (files still in use are deleted as soon as they are released).</summary>
    void Rebuild();
    /// <summary>Forgets problem records so those files are read again.</summary>
    void RetryProblems();
    /// <summary>Left-out and problem counts by status (Settings).</summary>
    IReadOnlyDictionary<DocStatus, int> CountsByStatus();
    /// <summary>The matching line of a file from the index only; null when not indexed or nothing matches. Call off the UI thread.</summary>
    ContentSnippet? GetSnippet(string fullPath, ContentHighlighter highlighter);
    /// <summary>
    /// Why a file's words aren't searchable (a left-out or problem status); null when they are, when it was read and has no
    /// words (NoText: a scan, a picture), or when it is out of scope. Call off the UI thread.
    /// </summary>
    DocStatus? GetStatus(string fullPath);
    /// <summary>Match boxes on one PDF page (reads that page's content only, inside a crash-marker slot). Call off the UI thread.</summary>
    Task<IReadOnlyList<PageBox>> LocateOnPdfPageAsync(string fullPath, int pageIndex, ContentHighlighter highlighter, CancellationToken ct);
    /// <summary>
    /// About maxBytes of a large text file's decoded text centred on the 1-based line, and its first line number, for the
    /// preview (PreviewMatch.TextWindow): <see cref="TextWindow.LoadAsync"/> run inside a crash-marker slot like every
    /// on-demand read. Null when unreadable or when search inside files is off. Call off the UI thread.
    /// </summary>
    Task<(string Text, int FirstLine)?> LoadTextWindowAsync(string fullPath, int line, int maxBytes, CancellationToken ct);
    /// <summary>Size and first-pass time for the settings' mode: live census plus calibration (index statistics, or a short real sample).</summary>
    Task<ContentEstimate> EstimateAsync(ContentSettings settings, CancellationToken ct);
    /// <summary>
    /// <see cref="EstimateAsync"/> without the real sample: the census priced with the index's statistics and the priors,
    /// no file opened. For the offer bar, shown while no choice is made: names only reads nothing, whatever it offers.
    /// </summary>
    Task<ContentEstimate> EstimateWithoutReadingAsync(ContentSettings settings, CancellationToken ct) => EstimateAsync(settings, ct);
    /// <summary>
    /// The calibration EstimateAsync prices with, for a census counted elsewhere (a folder listed before it is added, the
    /// scope a change takes documents out of): the index's own statistics for kinds it holds 200 or more documents of, the
    /// rest a 3 s real sample of the census's files in background mode (at most 10 on network shares), inside the index's
    /// crash marker and never a file it recorded Crashed, TimedOut or Damaged; priors only while running as administrator.
    /// </summary>
    Task<ContentCalibration> CalibrateAsync(ContentCensus census, CancellationToken ct);
    /// <summary>
    /// Windows asked to end the session, which can still be cancelled: commits what fits within the wait and clears the
    /// crash marker, but keeps running.
    /// </summary>
    void Checkpoint(TimeSpan wait);
    /// <summary>
    /// Stops within the wait: cancels reading, commits what fits, releases files; committed work is kept. Thread-safe,
    /// idempotent and final: later calls and commands are ignored.
    /// </summary>
    void Stop(TimeSpan wait);
}

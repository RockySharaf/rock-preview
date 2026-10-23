using Anything.Core;
using Anything.Core.Content;
using Anything.Demo;
using Microsoft.Win32;

namespace Anything;

/// <summary>
/// Search inside files, owned by the app (not a window) so it keeps up while every window is hidden or the app started
/// with Windows: the text index, the Matching text resolver, the offer to turn it on, and applying Settings' changes.
/// </summary>
internal sealed partial class AnythingApp
{
    IContentIndex _content = new NoContentIndex();
    SnippetResolver? _snippets;
    ContentMode _contentMode;
    int _contentStatusPending, _contentChangePending, _offerStatePending, _snippetsPending;
    KeptIndex? _kept;
    ContentEstimate? _offerEstimate;
    bool _offerEstimating, _focusContentSettings;

    /// <summary>The text index (an inert one until it started, or when it couldn't).</summary>
    public IContentIndex Content => _content;

    /// <summary>The choice the text index works with now (Settings' changes count once applied, when Settings closes).</summary>
    public ContentMode ContentMode => _contentMode;

    /// <summary>Search inside files is on.</summary>
    public bool ContentOn => _contentMode != ContentMode.NamesOnly;

    /// <summary>The text index reads inside network drives (Settings' switch as last applied, not as being edited).</summary>
    public bool NetworkDrivesApplied { get; private set; }

    /// <summary>Matching text for the windows' rows; null before the text index started.</summary>
    public SnippetResolver? Snippets => _snippets;

    /// <summary>A text index from before kept on disk while no choice is made (an older version dropped the setting); null when none.</summary>
    public KeptIndex? KeptContent => _kept;

    static ContentMode AppliedMode(ContentSettings s) => s.ModeExplicit ? s.Mode : ContentMode.NamesOnly;

    /// <summary>Starts the text index after the name index (its database loaded): it follows that index from now on.</summary>
    void StartContent()
    {
        try
        {
            _content = IsDemo
                ? ContentServices.CreateInMemory(new DemoContentSource(IndexManager.Index), demo: true) // never on disk
                : ContentServices.Create(Settings.ContentFolder);
        }
        catch (Exception)
        {
            _content = new NoContentIndex(); // names-only search still works
        }
        _content.StatusChanged += OnContentStatusBackground;
        _content.ContentChanged += OnContentChangedBackground;
        _snippets = new SnippetResolver(_content);
        _snippets.Resolved += OnSnippetsResolvedBackground;
        if (SearchEngine is DemoContentEngine demo) demo.ContentOn = () => ContentOn;
        try { _content.Start(IndexManager, Settings.Content); }
        catch (Exception) { }
        _contentMode = AppliedMode(Settings.Content);
        NetworkDrivesApplied = Settings.Content.ReadNetworkDrives;
        IndexManager.StateChanged += OnOfferStateBackground;
        // A network drive mapped or gone: the question about them may be due, and what reading them costs is counted again.
        IndexManager.DrivesChanged += () => Post(() =>
        {
            ForgetNetworkEstimate();
            UpdateOfferBars();
        });
        SystemEvents.SessionEnding += OnSessionEnding;
        LookForKeptIndex();
    }

    /// <summary>At exit, before the name index saves: reading stops, what fits is committed, files are let go (bounded).</summary>
    void StopContent()
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        IndexManager.StateChanged -= OnOfferStateBackground;
        _snippets?.Dispose();
        try { _content.Stop(TimeSpan.FromMilliseconds(800)); } catch (Exception) { }
        try { _content.Dispose(); } catch (Exception) { }
    }

    /// <summary>
    /// Windows asks whether the session may end, which can still be cancelled: commit what fits and keep going (the app
    /// exits through ExitApp if the session really ends).
    /// </summary>
    void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        try { _content.Checkpoint(TimeSpan.FromMilliseconds(500)); } catch (Exception) { }
    }

    /// <summary>Settings › Indexing closed with search inside files changed: the text index takes a copy and applies it at once.</summary>
    public void ApplyContentSettings()
    {
        if (_exiting) return;
        Settings.Save();
        try { _content.Apply(Settings.Content); } catch (Exception) { }
        _contentMode = AppliedMode(Settings.Content);
        NetworkDrivesApplied = Settings.Content.ReadNetworkDrives;
        if (Settings.Content.ModeExplicit) _kept = null; // chosen now: kept means followed again, or deleted
        _snippets?.Clear(); // what is searched inside may have changed: no line from before is shown again
        // What the question's estimate counts may have changed (the mode, kinds, folders); search inside files just turned on
        // makes the question due.
        ForgetNetworkEstimate();
        if (ShowNetworkOffer) EstimateNetworkOffer();
        foreach (var w in _windows.ToArray()) w.OnContentModeChanged();
    }

    void OnContentStatusBackground()
    {
        if (Interlocked.Exchange(ref _contentStatusPending, 1) != 0) return;
        Post(() =>
        {
            Volatile.Write(ref _contentStatusPending, 0);
            foreach (var w in _windows.ToArray()) w.OnContentStatusChanged();
        });
    }

    void OnContentChangedBackground()
    {
        if (Interlocked.Exchange(ref _contentChangePending, 1) != 0) return;
        Post(() =>
        {
            Volatile.Write(ref _contentChangePending, 0);
            _snippets?.MarkStale(); // lines on screen stay until they're looked up again (no blank flicker)
            foreach (var w in _windows.ToArray()) w.OnContentChanged();
        });
    }

    void OnSnippetsResolvedBackground()
    {
        if (Interlocked.Exchange(ref _snippetsPending, 1) != 0) return;
        Post(() =>
        {
            Volatile.Write(ref _snippetsPending, 0);
            foreach (var w in _windows.ToArray()) w.OnSnippetsResolved();
        });
    }

    // ---- the offer to search inside files --------------------------------------------------------------

    /// <summary>Whether windows offer search inside files now (never answered, nothing chosen, not the demo, names ready).</summary>
    public bool ShowContentOffer => !_exiting && ContentPresentation.ShowsOffer(Settings.ContentAsked, Settings.Content.Mode,
        Settings.Content.ModeExplicit, IsDemo, IndexManager.State);

    /// <summary>
    /// The offer's sentence, with what My folders would cost (or the index kept from before), and which of them would be
    /// left out for not being searched by name.
    /// </summary>
    public string ContentOfferText => _kept is { } kept ? ContentPresentation.OfferKept(kept)
        : ContentPresentation.Offer(_offerEstimate, ContentFoldersNotSearchedByName());

    /// <summary>
    /// The folders searched inside that the name index doesn't hold under the settings (none in the demo), by the plan's
    /// own answer, as the folder bar has it (<see cref="CoverageOf"/>: remembered until the settings or the drives change).
    /// </summary>
    IReadOnlyList<string> ContentFoldersNotSearchedByName()
    {
        if (IsDemo) return [];
        try { return ContentEstimator.FoldersNotSearchedByName(Settings.Content, Settings.Index, coverageOf: f => CoverageOf(f)); }
        catch (Exception) { return []; }
    }

    void OnOfferStateBackground()
    {
        if (Interlocked.Exchange(ref _offerStatePending, 1) != 0) return;
        Post(() =>
        {
            Volatile.Write(ref _offerStatePending, 0);
            UpdateOfferBars();
        });
    }

    void UpdateOfferBars()
    {
        if (ShowContentOffer && _kept is null) EstimateOffer();
        else if (ShowNetworkOffer) EstimateNetworkOffer();
        foreach (var w in _windows.ToArray()) w.UpdateOfferBar();
    }

    /// <summary>
    /// Works out once, in the background, what searching inside My folders' documents would cost, for the offer: from the
    /// name index's counts and the measured priors only, never a file opened (names only reads nothing; Settings and Setup
    /// sample real files once the user looks at the choice).
    /// </summary>
    void EstimateOffer()
    {
        if (_offerEstimating || _offerEstimate is not null) return;
        _offerEstimating = true;
        var documents = Settings.Content.Clone();
        documents.Mode = ContentMode.Documents;
        documents.ModeExplicit = true;
        var content = _content;
        Task.Run<ContentEstimate?>(async () =>
        {
            try { return await content.EstimateWithoutReadingAsync(documents, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { return null; }
        }).ContinueWith(t => Post(() =>
        {
            _offerEstimating = false;
            if (t.Result is { } e) _offerEstimate = e;
            if (!_exiting) foreach (var w in _windows.ToArray()) w.UpdateOfferBar();
        }), TaskScheduler.Default);
    }

    /// <summary>While no choice is made, looks (once, in the background) for a text index left on disk from before.</summary>
    void LookForKeptIndex()
    {
        if (IsDemo || Settings.Content.ModeExplicit) return;
        var content = _content;
        Task.Run(() =>
        {
            try { return content.Kept; }
            catch (Exception) { return null; }
        }).ContinueWith(t => Post(() =>
        {
            if (Settings.Content.ModeExplicit || _exiting) return;
            _kept = t.Result;
            UpdateOfferBars();
        }), TaskScheduler.Default);
    }

    // ---- the question about network drives -----------------------------------------------------------

    /// <summary>
    /// Whether windows ask about reading inside network drives now: search inside files on, the Network drives switch off
    /// and never answered, and a network drive the name index planned that nothing reads inside (the documents' offer first).
    /// </summary>
    public bool ShowNetworkOffer => !_exiting && ContentOn && !Settings.Content.ReadNetworkDrives && !Settings.ContentNetworkAsked
        && IndexManager.State == IndexState.Ready && NetworkDrivesNotReadInside().Count > 0 && !NothingToRead(_networkEstimate);

    /// <summary>A whole count that found nothing to read inside them: the question would offer nothing.</summary>
    static bool NothingToRead(ContentEstimate? estimate) => estimate is { AtLeast: false } e && ContentPresentation.NetworkOfferFiles(e) == 0;

    /// <summary>The question's sentence: which drives, what reading them costs once worked out, at what speed, and the administrator pause.</summary>
    public string NetworkOfferText => ContentPresentation.NetworkOffer(NetworkDrivesNotReadInside(),
        Settings.Content.NetworkKilobytesPerSecond, IndexManager.IsElevated, _networkEstimate);

    ContentEstimate? _networkEstimate; // shown while it is the latest; counted again until a whole count (not AtLeast) came in
    bool _networkEstimating;
    int _networkEstimateFor; // bumped when what it counts changes (drives, settings): an older count is dropped when it comes in

    /// <summary>What the question's estimate counted changed: it is counted again when the question is next shown.</summary>
    void ForgetNetworkEstimate()
    {
        _networkEstimateFor++;
        _networkEstimate = null;
    }

    /// <summary>
    /// The network drives and shares the name index holds that no folder searched inside covers or leaves out (the plan's
    /// own answer, as the folder bar has it); none in the demo.
    /// </summary>
    IReadOnlyList<string> NetworkDrivesNotReadInside()
    {
        if (IsDemo) return [];
        try { return ContentEstimator.NetworkRootsNotReadInside(Settings.Content, Settings.Index, IndexManager.PlannedDrives, f => CoverageOf(f)); }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// Works out once, in the background, what reading inside the network drives would cost, for the question: from the
    /// name index's counts and the measured priors only, never a file opened.
    /// </summary>
    void EstimateNetworkOffer()
    {
        // Counted once the name index is ready (a share still being scanned would count only what it listed so far), and
        // again after a count that wasn't whole.
        if (_networkEstimating || _networkEstimate is { AtLeast: false } || IndexManager.State != IndexState.Ready) return;
        _networkEstimating = true;
        int counting = _networkEstimateFor;
        var network = Settings.Content.Clone();
        // Only what switching it on adds: the folders searched inside already are left out of the count.
        network.ExcludeFolders = [.. network.ExcludeFolders, .. network.EffectiveFolders()];
        network.UseMyFolders = false;
        network.Folders = [];
        network.ReadNetworkDrives = true;
        network.Mode = _contentMode;
        network.ModeExplicit = true;
        var content = _content;
        Task.Run<ContentEstimate?>(async () =>
        {
            try { return await content.EstimateWithoutReadingAsync(network, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { return null; }
        }).ContinueWith(t => Post(() =>
        {
            _networkEstimating = false;
            if (t.Result is { } e && counting == _networkEstimateFor) _networkEstimate = e;
            if (!_exiting) foreach (var w in _windows.ToArray()) w.UpdateOfferBar();
        }), TaskScheduler.Default);
    }

    /// <summary>The question was answered: "Search inside them" switches Network drives on (applied now), "Not now" doesn't; it doesn't come back.</summary>
    public void NetworkOfferAnswered(bool turnOn)
    {
        Settings.ContentNetworkAsked = true;
        if (turnOn)
        {
            Settings.Content.ReadNetworkDrives = true;
            ApplyContentSettings(); // saves the settings too
        }
        else Settings.Save();
        foreach (var w in _windows.ToArray()) w.UpdateOfferBar();
    }

    /// <summary>The offer was answered (Choose… or Not now): it doesn't come back.</summary>
    public void ContentOfferAnswered()
    {
        Settings.ContentAsked = true;
        Settings.Save();
        foreach (var w in _windows.ToArray()) w.UpdateOfferBar();
    }

    /// <summary>The offer's "Turn it back on" for an index kept from before: the choice it was made for, chosen again.</summary>
    public void TurnKeptContentBackOn()
    {
        if (_kept is not { } kept) return;
        Settings.Content.Mode = kept.Mode == ContentMode.NamesOnly ? ContentMode.Documents : kept.Mode;
        Settings.Content.ModeExplicit = true;
        Settings.ContentAsked = true;
        ApplyContentSettings();
        foreach (var w in _windows.ToArray()) w.UpdateOfferBar();
    }

    DateTime _pausedUntil; // when the user's pause ends (UTC); MaxValue = until Resume or restart

    /// <summary>The user paused reading (for an hour, or until Resume); pauses for battery and the like aren't counted.</summary>
    public bool ContentPausedByUser => _pausedUntil > DateTime.UtcNow;

    /// <summary>Pauses reading inside files for a while (null: until Resume or the app restarts); following changes goes on.</summary>
    public void PauseContent(TimeSpan? forHowLong)
    {
        _pausedUntil = forHowLong is { } d ? DateTime.UtcNow + d : DateTime.MaxValue;
        try { _content.Pause(forHowLong); } catch (Exception) { }
    }

    /// <summary>Resumes reading after the user's pause.</summary>
    public void ResumeContent()
    {
        _pausedUntil = default;
        try { _content.Resume(); } catch (Exception) { }
    }

    /// <summary>Settings › Indexing, scrolled to search inside files (the offer's Choose…).</summary>
    public void ShowContentSettings(IWin32Window? owner)
    {
        _focusContentSettings = true;
        ShowSettings(owner, "Indexing");
    }

    /// <summary>Whether Settings was opened for search inside files (asked once).</summary>
    public bool TakeContentSettingsFocus()
    {
        bool focus = _focusContentSettings;
        _focusContentSettings = false;
        return focus;
    }

    /// <summary>Stands in when the text index couldn't be created: nothing is read, written or found.</summary>
    sealed class NoContentIndex : IContentIndex
    {
        public ContentStatus Status => ContentStatus.Off;
        public event Action? StatusChanged { add { } remove { } }
        public event Action? ContentChanged { add { } remove { } }
        public KeptIndex? Kept => null;
        public void Start(IIndexManager names, ContentSettings settings) { }
        public void Apply(ContentSettings settings) { }
        public void Pause(TimeSpan? forHowLong) { }
        public void Resume() { }
        public void Rebuild() { }
        public void RetryProblems() { }
        public IReadOnlyDictionary<DocStatus, int> CountsByStatus() => new Dictionary<DocStatus, int>();
        public ContentSnippet? GetSnippet(string fullPath, ContentHighlighter highlighter) => null;
        public DocStatus? GetStatus(string fullPath) => null;
        public Task<IReadOnlyList<PageBox>> LocateOnPdfPageAsync(string fullPath, int pageIndex, ContentHighlighter highlighter, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<PageBox>>([]);
        public Task<(string Text, int FirstLine)?> LoadTextWindowAsync(string fullPath, int line, int maxBytes, CancellationToken ct)
            => Task.FromResult<(string Text, int FirstLine)?>(null);
        public Task<ContentEstimate> EstimateAsync(ContentSettings settings, CancellationToken ct)
            => Task.FromResult(new ContentEstimate(settings.Mode, 0, 0, 0, TimeSpan.Zero, 0, AtLeast: true));
        public Task<ContentCalibration> CalibrateAsync(ContentCensus census, CancellationToken ct) => Task.FromResult(ContentEstimator.Priors);
        public void Checkpoint(TimeSpan wait) { }
        public void Stop(TimeSpan wait) { }
        public void Dispose() { }
    }
}

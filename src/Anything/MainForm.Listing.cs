using Anything.Core;
using Anything.Core.Indexing;
using Anything.Core.Search;

namespace Anything;

/// <summary>
/// "Every file in" a folder the index doesn't have (left out by Settings, its drive turned off, or not read yet): the
/// tab reads it straight from the disk instead (a <see cref="FolderListing"/>, kept current as long as the tab shows
/// every file in it, hidden window included), and a bar says why its files don't show up in searches, with a way to
/// add them. Also the text an empty list or grid shows.
/// </summary>
internal sealed partial class MainForm
{
    bool _folderBarShown; // the bar's own state: Visible reads false while the window is hidden

    void BuildFolderBar()
    {
        _folderBar.Dock = DockStyle.Top;
        _folderBar.AutoSize = true;
        _folderBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _folderBar.WrapContents = true;
        _folderBar.Padding = new Padding(6, 3, 6, 3);
        _folderBar.BackColor = SystemColors.Info;
        _folderBar.ForeColor = SystemColors.InfoText;
        _folderInclude.LinkClicked += (_, _) =>
        {
            var gaps = FolderGaps();
            if (!_app.ShowFolderInSearches(gaps.Select(g => g.Folder))) _app.ShowSettings(this, SettingsPageFor(gaps));
            UpdateFolderBar();
        };
        _folderDrive.LinkClicked += (_, _) =>
        {
            if (FolderGaps() is [{ Drive: { } drive }, ..] && !_app.IncludeLeftOutDrives([drive])) _app.ShowSettings(this, "Folders");
            UpdateFolderBar();
        };
        _folderSettings.LinkClicked += (_, _) => _app.ShowSettings(this, SettingsPageFor(FolderGaps()));
        var dismiss = new LinkLabel { AutoSize = true, Text = "Dismiss", Margin = new Padding(0, 2, 0, 2) };
        dismiss.LinkClicked += (_, _) => { _tab.FolderBarDismissedFor = CoverageFolders().ToList(); UpdateFolderBar(); };
        _folderBar.Controls.AddRange([_folderText, _folderInclude, _folderDrive, _folderSettings, dismiss]);
        _folderBar.Visible = false;
    }

    /// <summary>The folders on screen: every file in them, or a search inside them (the open folder, or those selected).</summary>
    List<string> CoverageFolders()
    {
        if (_zapActive) return _zapRoots;
        if (_search.TextLength == 0) return [];
        if (_useSelScope && _selScope.Count > 0) return _selScope;
        if (_browsing && _scope.Checked && _explorer.CurrentPath is { } folder) return [folder];
        return [];
    }

    /// <summary>The folders on screen the index doesn't have, and why (from the settings: nothing touches the disk).</summary>
    List<FolderCoverage> FolderGaps() => FolderGaps(CoverageFolders());

    List<FolderCoverage> FolderGaps(List<string> folders)
    {
        var gaps = new List<FolderCoverage>();
        foreach (var f in folders)
            if (_app.CoverageOf(f) is { Covered: false } gap) gaps.Add(gap);
        return gaps;
    }

    /// <summary>The folder bar was dismissed for these folders (a search in other folders, or every file in them, shows it again).</summary>
    bool FolderBarDismissed(List<string> folders) =>
        _tab.FolderBarDismissedFor is { } dismissed && dismissed.SequenceEqual(folders, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Shows the folder bar while the files on screen (every file in a folder, or a search inside folders) don't show up
    /// in searches, saying why. It takes the place of the left-out drive bar meanwhile, which says less.
    /// </summary>
    internal void UpdateFolderBar()
    {
        var folders = CoverageFolders();
        var gaps = ExplorerShown || FolderBarDismissed(folders) ? [] : FolderGaps(folders);
        bool show = gaps.Count > 0;
        if (show)
        {
            var gap = gaps[0];
            _folderText.Text = $"Files in {GapLabel(folders, gaps)} don't show up in searches: {Why(gap)}.";
            bool driveRoot = _app.IsWholeDrive(gap.Folder);
            // A whole drive can be shown again only when the list of folders to leave out hides it; its switches are in
            // Settings. A drive that isn't ready is searched by itself once it is: adding its folders would only stay behind.
            _folderInclude.Visible = gap.Reason != CoverageReason.DriveNotReady && (!driveRoot || gap.Reason == CoverageReason.DriveLeftOut);
            _folderInclude.Text = driveRoot ? $"Show {DriveName(gap.Folder)} in searches" : "Show them in searches";
            // Not read back from Visible, which says false while the bar or the window is hidden.
            bool showDrive = !driveRoot && gap.Reason == CoverageReason.DriveLeftOut && gap.Drive is { } drive
                             && !_app.Settings.IsLeftOutDriveKept(drive); // "Keep it left out" was the answer
            _folderDrive.Visible = showDrive;
            if (showDrive) _folderDrive.Text = $"Show all of {DriveName(gap.Drive!)}";
        }
        _folderBarShown = show;
        _folderBar.Visible = show;
        UpdateLeftOutBar();
        UpdateEmptyText(); // the empty list says the same, or no longer does
    }

    /// <summary>
    /// The folder bar was dismissed while its reason was a drive the left-out drive bar is about: that bar stays away too
    /// while those folders show, or Dismiss would only bring up the same reason again.
    /// </summary>
    bool FolderBarDismissedForLeftOutDrive()
    {
        var folders = CoverageFolders();
        return FolderBarDismissed(folders) && FolderGaps(folders) is [{ Reason: CoverageReason.DriveLeftOut, Drive: { } drive }, ..]
               && _app.LeftOutDrives.Any(d => AppSettings.SamePath(d, drive));
    }

    /// <summary>What the bar and the empty text call the folders whose files don't show up: only those, when others on screen do.</summary>
    static string GapLabel(List<string> folders, List<FolderCoverage> gaps) =>
        gaps.Count == folders.Count ? (folders.Count == 1 ? FolderName(folders[0]) : "these folders")
        : gaps.Count == 1 ? FolderName(gaps[0].Folder) : $"{gaps.Count} of these folders";

    string Why(FolderCoverage c) => c.Reason switch
    {
        CoverageReason.DriveLeftOut or CoverageReason.FolderLeftOut => $"{c.Rule ?? c.Drive} is on the list of folders to leave out",
        CoverageReason.NameLeftOut => $"names like {c.Rule} are left out",
        CoverageReason.HiddenLeftOut => "hidden folders are left out",
        CoverageReason.SystemLeftOut => "system folders are left out",
        CoverageReason.DriveTurnedOff => $"{DriveName(c.Drive ?? c.Folder)} is turned off in Settings › Indexing",
        // As Settings words the drive's row, by the kind the drive list has (asking Windows can wait on a share).
        CoverageReason.DriveKindOff => (c.Drive is { } d ? _app.KindOfDrive(d) : DriveKind.Unknown) switch
        {
            DriveKind.Network => "network drives are off in Settings › Indexing",
            DriveKind.Optical => "CDs and DVDs are off in Settings › Indexing",
            _ => "USB drives are off in Settings › Indexing",
        },
        CoverageReason.AllDrivesOff => "Index whole drives is off in Settings › Indexing",
        CoverageReason.DriveNotReady => $"{DriveName(c.Drive ?? c.Folder)} isn't ready",
        CoverageReason.BehindLink => "they're reached through a link to a folder that isn't searched either",
        CoverageReason.MountNotIndexed => $"the drive mounted at {(c.Drive ?? c.Folder).TrimEnd('\\')} isn't searched",
        _ => "no folder in Settings › Folders holds them",
    };

    /// <summary>Where Settings can change what the bar says: drive switches are on Indexing, everything else on Folders.</summary>
    static string SettingsPageFor(List<FolderCoverage> gaps) =>
        gaps is [{ Reason: CoverageReason.AllDrivesOff or CoverageReason.DriveTurnedOff or CoverageReason.DriveKindOff or CoverageReason.DriveNotReady }, ..]
            ? "Indexing" : "Folders";

    /// <summary>"C:" for a drive, the folder's path otherwise.</summary>
    static string DriveName(string root) => IndexPlan.IsDriveRoot(root) ? root.Trim()[..2].ToUpperInvariant() : root.TrimEnd('\\');

    static string FolderName(string path) => Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : DriveName(path);

    // ---- "every file in": the index, or the disk ---------------------------------------------------

    /// <summary>
    /// "Every file in" comes from the index when it has the folders and has read them, else from the disk: a listing for
    /// this tab (left out by Settings, their drive turned off, the database still loading, a first scan under way).
    /// </summary>
    void ChooseZapSource(List<string> roots)
    {
        DisposeListing(_tab);
        _tab.FolderBarDismissedFor = null;
        ResetDiskCheck(_tab);
        _tab.ZapSearchRoots = roots;
        if (!_app.IsDemo)
        {
            var im = _app.IndexManager;
            var coverage = roots.Select(r => _app.CoverageOf(r, fresh: true)).ToList(); // a new look each time
            bool fromIndex = im.State != IndexState.Loading
                             && coverage.All(c => c is { Covered: true, Root: { } root } && im.StateOfRoot(root) is { HasEntries: true, Complete: true });
            if (fromIndex) _tab.ZapSearchRoots = coverage.Select(c => c!.IndexedAs ?? c.Folder).ToList();
            else StartListing(_tab, roots);
        }
        UpdateFolderBar();
    }

    /// <summary>Reads the folders from the disk for a tab, with the same rules the index would use for them.</summary>
    void StartListing(FolderTab tab, List<string> roots)
    {
        DisposeListing(tab);
        FolderListing listing;
        // From the index manager, so that a drive being ejected is let go of, as the index does.
        try { listing = _app.IndexManager.CreateListing(roots, _app.Settings.Index.Clone()); }
        catch (ArgumentException) { return; } // no folder left to read: the index answers
        // Searches of the listing look inside files too, through the main index's text index (mapped by path): a folder
        // the index knows by another name (a SUBST letter, a link, a mapped letter above a folder searched by its share)
        // under that name.
        var names = ListingNames(roots);
        Anything.Core.Content.ContentServices.ShareWith(_app.IndexManager.Index, listing.Index, names);
        tab.Listing = listing;
        tab.ListingNames = names;
        tab.ZapSearchRoots = roots;
        listing.Changed += () => _app.Post(() => OnListingChanged(tab, listing));
        listing.StateChanged += () => _app.Post(() => OnListingStateChanged(tab, listing));
        listing.Start();
    }

    /// <summary>
    /// The names a listing of these folders finds their files' words under (IndexPlan.ListingNames): each folder's name in
    /// the index first (IndexedAs), then every other name it has, so roots spelled either way are reached (Z:\Music picked
    /// in Settings, \\nas\share\Docs shown in searches by its share); a folder behind a link only where the link points, as
    /// its own name is in a root that stops at the link. For a new listing, CoverageOf remembers the fresh look
    /// ChooseZapSource took, names and all, so nothing is read again here.
    /// </summary>
    Dictionary<string, IReadOnlyList<string>> ListingNames(List<string> roots)
    {
        var names = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in roots)
            if (_app.ListingNamesOf(r) is { Count: > 0 } known) names[r] = known;
        return names;
    }

    /// <summary>
    /// What the index holds changed (settings applied, the drives listed again): a tab reading "every file in" from the
    /// disk looks for its files' words under the names its folders have in the index now (a folder just shown in searches
    /// is named where its link points), and searches again: now when it shows, else when it does.
    /// </summary>
    internal void RefreshListingNames()
    {
        if (!_loaded) return;
        foreach (var t in _tabs)
        {
            if (t.Listing is not { } listing) continue;
            var names = ListingNames(t.ZapSearchRoots);
            if (t.ListingNames is { } shared && SameNames(shared, names)) continue;
            Anything.Core.Content.ContentServices.ShareWith(_app.IndexManager.Index, listing.Index, names);
            t.ListingNames = names;
            if (t == _tab && Visible) RunSearch(SearchReason.IndexChanged);
            else t.Stale = true;
        }
    }

    static bool SameNames(Dictionary<string, IReadOnlyList<string>> a, Dictionary<string, IReadOnlyList<string>> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && p.Value.SequenceEqual(other, StringComparer.OrdinalIgnoreCase));

    /// <summary>Stops a tab's listing. Its index stays, so the ids on screen can be read until new results replace them.</summary>
    static void DisposeListing(FolderTab tab)
    {
        var listing = tab.Listing;
        tab.Listing = null;
        listing?.Dispose(); // returns at once
    }

    /// <summary>
    /// The folder view shows again after "every file in" read from the disk: its results are let go of, so the listing's
    /// whole index can be (they would be searched again anyway once they show).
    /// </summary>
    void DropListingResults()
    {
        if (!ExplorerShown || _listing != null || _resultIndex is not { } index || ReferenceEquals(index, _app.IndexManager.Index)) return;
        _ids = [];
        _resultIndex = null;
        _rows.Clear();
        _suppress = true;
        try
        {
            if (_list.SelectedIndices.Count > 0) _list.SetAllSelected(false);
            _list.VirtualListSize = 0;
        }
        catch (ArgumentException) { }
        finally { _suppress = false; }
        _grid.SetCount(0, reset: true);
    }

    /// <summary>Files came in (or changed) in a listing: its tab searches again, soon (quickly while it shows nothing yet).</summary>
    void OnListingChanged(FolderTab tab, FolderListing listing)
    {
        if (IsDisposed || tab.Listing != listing) return;
        if (tab != _tab) { tab.Stale = true; return; }
        if (!Visible) { tab.Stale = true; return; } // searched again when the window shows
        if (_listingTimer.Enabled) return;
        _listingTimer.Interval = _ids.Length == 0 ? 100 : 400;
        _listingTimer.Start();
    }

    void OnListingStateChanged(FolderTab tab, FolderListing listing)
    {
        if (IsDisposed || tab.Listing != listing) return;
        if (tab != _tab) { tab.Stale = true; return; } // its last search may have come before the read was over
        QueueStatusUpdate();
        UpdateEmptyText();
        if (listing.State != ListingState.Reading) RunSearch(SearchReason.IndexChanged);
    }

    /// <summary>Forgets the disk check: the next empty result looks at the disk again, and a check still running is ignored.</summary>
    static void ResetDiskCheck(FolderTab tab)
    {
        tab.DiskCheck++;
        tab.DiskChecked = tab.CheckingDisk = false;
    }

    /// <summary>
    /// The index has the folder, yet found no file in it (an empty search, no kind of file chosen): one quick look at the
    /// disk, with the same rules, and the folder is read from there when it does have files the index missed.
    /// </summary>
    void CheckDiskForMissedFiles()
    {
        var tab = _tab;
        if (!_zapActive || _listing != null || tab.DiskChecked || _search.TextLength > 0 || _filterIndex > 0 || _app.IsDemo) return;
        var roots = _zapRoots.ToList();
        bool deep = _app.Settings.ZapIncludeSubfolders;
        var filter = new ExclusionFilter(_app.Settings.Index);
        // Only files the view would show count: with Everyday files on, a folder of code is rightly empty.
        Func<string, bool>? counts = _app.Settings.EverydayFilesOnly ? n => EverydayFiles.IsEveryday(n) : null;
        tab.DiskChecked = tab.CheckingDisk = true;
        int check = ++tab.DiskCheck;
        UpdateEmptyText();
        Task.Run(() => roots.Any(r => FolderListing.HasFile(r, filter, deep, maxFolders: 200, CancellationToken.None, counts) == true))
            .ContinueWith(t =>
            {
                if (tab.DiskCheck != check) return; // asked about other files meanwhile: a newer look answers
                tab.CheckingDisk = false;
                if (IsDisposed) return;
                bool same = _tabs.Contains(tab) && tab.ZapActive && tab.Listing == null && tab.ZapRoots.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase);
                if (t.IsCompletedSuccessfully && t.Result && same)
                {
                    StartListing(tab, roots);
                    if (tab == _tab) RunSearch(SearchReason.Refresh);
                    else tab.Stale = true; // shown from the disk when it comes back
                }
                else if (tab == _tab) UpdateEmptyText();
            }, CancellationToken.None, TaskContinuationOptions.None, _ui);
    }

    /// <summary>
    /// Everyday files or Include subfolders changed (one setting for every window): every "every file in" searches again,
    /// the one showing now, the others when they show.
    /// </summary>
    internal void OnZapOptionsChanged()
    {
        if (!_loaded) return;
        foreach (var t in _tabs)
        {
            if (!t.ZapActive) continue;
            ResetDiskCheck(t); // nothing found means something else now
            if (t != _tab) t.Stale = t.StaleNewQuery = true;
        }
        SyncMenus();
        UpdateCommandStates();
        if (_zapActive)
        {
            if (Visible) RunSearch(SearchReason.NewQuery);
            else _tab.Stale = _tab.StaleNewQuery = true;
        }
        QueueStatusUpdate();
    }

    // ---- what an empty list says --------------------------------------------------------------

    /// <summary>The gray text an empty list or grid shows, as Explorer does ("No items match your search."), or none.</summary>
    void UpdateEmptyText()
    {
        string text = "";
        // Words inside files can say better why a search found nothing (a choice that is off), or that it may yet find some.
        string? content = _search.TextLength > 0 ? ContentEmptyText() : null;
        if (_zapActive)
        {
            string where = _zapLabel;
            bool everyday = _app.Settings.EverydayFilesOnly;
            if (_listing is { State: ListingState.Unavailable } gone) text = $"{where} can't be read: {gone.Problem}";
            else if (_listing is { State: ListingState.Reading } || _tab.CheckingDisk) text = $"Looking for files in {where}...";
            // A typed search keeps what its last results said until the new ones come in, as the list keeps its rows:
            // wiping the text at each key would only make it blink.
            else if (_searchPending && _search.TextLength > 0) return;
            // Until the first results come in, what the list holds is from before.
            else if (_searchPending && _pendingNewQuery) text = $"Looking for files in {where}...";
            // The Everyday files filter applies to a search too, and is the likelier reason it finds nothing (after words
            // inside files: a choice that is off matches nothing whatever the filter).
            else if (_search.TextLength > 0 || _filterIndex > 0)
                text = content ?? "No items match your search." + (everyday ? " Turn off Everyday files to see every file." : "");
            else
            {
                string files = everyday ? "pictures, videos, music, documents or design files" : "files";
                string its = _zapRoots.Count > 1 ? "their" : "its";
                text = _app.Settings.ZapIncludeSubfolders ? $"No {files} in {where} or {its} subfolders." : $"No {files} directly in {where}.";
                if (everyday) text += " Turn off Everyday files to see every file.";
            }
        }
        else if (_search.TextLength > 0)
        {
            if (_searchPending) return; // as above
            var folders = CoverageFolders();
            var gaps = FolderGaps(folders);
            text = content ?? (gaps.Count > 0 ? $"Files in {GapLabel(folders, gaps)} don't show up in searches." : "No items match your search.");
        }
        _list.EmptyText = text;
        _grid.EmptyText = text;
    }
}

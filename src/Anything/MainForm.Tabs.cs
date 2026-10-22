using Anything.Core;
using Anything.Core.Indexing;
using Anything.Explorer;

namespace Anything;

/// <summary>
/// Tabs, like Windows 11 File Explorer's: several folders and searches in one window. Each tab has its own Explorer
/// view (so its own Back/Forward history) and its own search, results, Thumbnails and sort; the window's chrome (address
/// row, command bar, navigation pane, results list, preview pane, status bar) shows the active tab. The other tabs'
/// views stay alive, hidden, so switching back is instant and the folder doesn't load again.
/// </summary>
internal sealed partial class MainForm
{
    readonly TabStrip _tabStrip = new() { Dock = DockStyle.Fill };
    readonly List<FolderTab> _tabs = [];
    FolderTab _tab = new(); // the active tab
    bool _searchPending;    // a search was started and its results haven't arrived yet

    // The active tab's state, under the names it had as the window's own fields before there were tabs: the rest of
    // the window reads and writes it as before, and it follows whichever tab is active.
    ExplorerHost _explorer => _tab.Explorer;
    int[] _ids { get => _tab.Ids; set => _tab.Ids = value; }
    long _resultGeneration { get => _tab.ResultGeneration; set => _tab.ResultGeneration = value; }
    FileIndex? _resultIndex { get => _tab.ResultIndex; set => _tab.ResultIndex = value; }
    FolderListing? _listing => _tab.Listing;
    string _resultQuery { get => _tab.ResultQuery; set => _tab.ResultQuery = value; }
    SearchOptions _resultOptions { get => _tab.ResultOptions; set => _tab.ResultOptions = value; }
    string? _searchError { get => _tab.SearchError; set => _tab.SearchError = value; }
    bool _matchCase { get => _tab.MatchCase; set => _tab.MatchCase = value; }
    bool _wholeWord { get => _tab.WholeWord; set => _tab.WholeWord = value; }
    bool _matchPath { get => _tab.MatchPath; set => _tab.MatchPath = value; }
    bool _diacritics { get => _tab.Diacritics; set => _tab.Diacritics = value; }
    bool _regex { get => _tab.Regex; set => _tab.Regex = value; }
    int _filterIndex { get => _tab.FilterIndex; set => _tab.FilterIndex = value; }
    SortKey _sort { get => _tab.Sort; set => _tab.Sort = value; }
    bool _desc { get => _tab.Descending; set => _tab.Descending = value; }
    bool _staleWhileHidden { get => _tab.Stale; set => _tab.Stale = value; }
    bool _browsing { get => _tab.Browsing; set => _tab.Browsing = value; }
    bool _zapActive { get => _tab.ZapActive; set => _tab.ZapActive = value; }
    bool _zapThumbs { get => _tab.ZapThumbs; set => _tab.ZapThumbs = value; }
    List<string> _zapRoots { get => _tab.ZapRoots; set => _tab.ZapRoots = value; }
    string _zapLabel { get => _tab.ZapLabel; set => _tab.ZapLabel = value; }
    List<string> _selScope { get => _tab.SelScope; set => _tab.SelScope = value; }
    bool _useSelScope { get => _tab.UseSelScope; set => _tab.UseSelScope = value; }
    (SortKey Key, bool Descending)? _sortBeforeZap { get => _tab.SortBeforeZap; set => _tab.SortBeforeZap = value; }
    (SortKey Key, bool Descending)? _sortFromFolder { get => _tab.SortFromFolder; set => _tab.SortFromFolder = value; }
    bool _explorerStart { get => _tab.ExplorerStart; set => _tab.ExplorerStart = value; }
    string? _explorerLocation { get => _tab.ExplorerLocation; set => _tab.ExplorerLocation = value; }

    /// <summary>One tab: its Explorer view, and what the window shows for it while it is the active tab.</summary>
    sealed class FolderTab : ITabStripItem
    {
        public readonly ExplorerHost Explorer = new() { Dock = DockStyle.Fill, Visible = false };
        public string SearchText = "";
        public int[] Ids = [];
        public long ResultGeneration;
        /// <summary>The index the Ids came from (the app's, or <see cref="Listing"/>'s): ids mean nothing in another one.</summary>
        public FileIndex? ResultIndex;
        /// <summary>"Every file in" folders the index doesn't have, read straight from the disk for this tab.</summary>
        public FolderListing? Listing;
        /// <summary>The names <see cref="Listing"/>'s folders were shared with the text index under (its words are looked for there).</summary>
        public Dictionary<string, IReadOnlyList<string>>? ListingNames;
        /// <summary>The folders' names in the index for the "every file in" search (a SUBST drive's folder under its target).</summary>
        public List<string> ZapSearchRoots = [];
        /// <summary>The folders on screen when the folder bar was dismissed: it stays away while they are the ones on screen.</summary>
        public List<string>? FolderBarDismissedFor;
        /// <summary>The disk is being checked because the index found nothing where it should have files.</summary>
        public bool CheckingDisk;
        /// <summary>That check ran for this "every file in" (once each, so an empty folder doesn't loop).</summary>
        public bool DiskChecked;
        /// <summary>Counts the checks started and forgotten: a check that finishes after a newer one started, or after a reset, is ignored.</summary>
        public int DiskCheck;
        public string ResultQuery = "";
        public SearchOptions ResultOptions = new();
        public string? SearchError;
        public bool MatchCase, WholeWord, MatchPath, Diacritics, Regex;
        public int FilterIndex;
        public SortKey Sort;
        public bool Descending;
        public bool Browsing, ZapActive, ZapThumbs;
        public List<string> ZapRoots = [];
        public string ZapLabel = "";
        public List<string> SelScope = [];
        public bool UseSelScope;
        public (SortKey Key, bool Descending)? SortBeforeZap, SortFromFolder;
        public bool ExplorerStart;
        public string? ExplorerLocation;
        /// <summary>The results may be out of date (the index changed, or the search was cut short): searched again when it shows.</summary>
        public bool Stale, StaleNewQuery;
        // Where the results list was, kept while another tab shows (the list itself belongs to the window).
        public int FocusedId = -1, TopIndex;
        public List<int>? SelectedIds;

        bool ShowsFolder => Browsing && SearchText.Length == 0 && !ZapActive;

        public string Title => ZapActive ? $"Every file in {ZapLabel}"
            : SearchText.Length > 0 ? SearchText
            : Explorer.CurrentDisplayName is { Length: > 0 } name ? name : "New tab";

        public string? ToolTip => ShowsFolder ? Explorer.CurrentEditingName : Title;
        public int IconIndex => Browsing && SearchText.Length == 0 ? Explorer.CurrentIconIndex : -1;
        public char Glyph => ExplorerTheme.GSearch;
    }

    void BuildTabStrip()
    {
        _tabs.Add(_tab);
        _tabStrip.Tabs = _tabs;
        _tabStrip.TabClicked += i => SelectTab(_tabs[i]);
        _tabStrip.CloseClicked += i => CloseTab(_tabs[i]);
        _tabStrip.NewTabClicked += () => NewTab();
        _tabStrip.TabMoved += MoveTab;
        _tabStrip.TabMenuRequested += ShowTabMenu;
    }

    /// <summary>Hooks a tab's Explorer view up to the window and puts it with the other views, hidden until it shows.</summary>
    void AttachTab(FolderTab t)
    {
        var host = t.Explorer;
        host.OpenFoldersInPlace = _app.Settings.UseInsteadOfExplorer;
        host.Navigated += () => OnExplorerNavigated(t);
        host.SelectionChanged += () =>
        {
            if (t != _tab) return;
            QueuePreview();
            QueueStatusUpdate();
            UpdateSearchPlaceholder();
        };
        host.NavigationFailed += msg => { if (t == _tab) _statusRight.Text = msg; };
        _split.Panel1.Controls.Add(host);
    }

    /// <summary>Opens a tab at the end of the strip and shows it: at <paramref name="location"/>, or Home like Explorer's new tabs.</summary>
    public void NewTab(string? location = null) => OpenTab(location, -1, null, null);

    /// <summary>
    /// Opens a tab and shows it: at <paramref name="location"/>, or Home, at <paramref name="index"/> (-1 = the end). It
    /// searches the way <paramref name="like"/> (by default the active tab) does: match options, kind of file and sort,
    /// as a new window does.
    /// </summary>
    void OpenTab(string? location, int index, string? search, FolderTab? like)
    {
        var from = like ?? _tab;
        var t = new FolderTab
        {
            MatchCase = from.MatchCase, WholeWord = from.WholeWord, MatchPath = from.MatchPath, Diacritics = from.Diacritics, Regex = from.Regex,
            FilterIndex = from.FilterIndex,
            Browsing = true, // a folder is on its way: show its view, not an empty results list, while it loads
        };
        (t.Sort, t.Descending) = from.SortBeforeZap ?? (from.Sort, from.Descending);
        _tabs.Insert(index < 0 || index > _tabs.Count ? _tabs.Count : index, t);
        AttachTab(t);
        SelectTab(t);
        if (location is not { Length: > 0 } || !BrowseTo(location)) GoHome();
        if (search is { Length: > 0 }) SetSearch(search);
        else RunSearch(SearchReason.NewQuery);
        BeginInvoke(() => { if (t == _tab) FocusTabContent(); }); // after whatever else wants the focus now (showing the window)
    }

    /// <summary>Shows another tab: its view, its search, and its results where it left them.</summary>
    void SelectTab(FolderTab t)
    {
        if (t == _tab || !_tabs.Contains(t)) return;
        var old = _tab;
        bool focusContent = ContainsFocus && !_search.Focused; // typing in the search box carries on there
        _breadcrumb.EndEdit();

        // Leave the old tab. A search that hasn't finished is done again when it comes back, and results nobody sees
        // (its folder view is showing) are let go: they would be searched again anyway once they are needed.
        RememberListPosition(old);
        if (_searchPending) { old.Stale = true; old.StaleNewQuery |= _pendingNewQuery; }
        _cts?.Cancel();
        _searchSeq++;
        _searchPending = _pendingNewQuery = false;
        // Its listing changed and was about to be searched again: that is done when it comes back instead.
        if (_listingTimer.Enabled) { _listingTimer.Stop(); old.Stale = true; }
        // The results let go of include the index they came from (a listing's can be a whole drive).
        if (ExplorerShown && old.Ids.Length > 0) { old.Ids = []; old.ResultIndex = null; old.Stale = true; }
        old.Explorer.Visible = false;
        _hintDelay.Stop(); // the folder that was to stay open 3 seconds isn't the one showing now

        _tab = t;
        _rows.Clear();
        _suppress = true;
        try
        {
            _search.Text = t.SearchText;
            _search.SelectionStart = _search.TextLength;
        }
        finally { _suppress = false; }
        ShowListPosition(t);
        UpdateSortIndicator();
        SyncMenus();
        SyncAddress();
        SyncNavTree();
        ApplyViewMode();
        UpdateLeftView(); // and the folder bar and the empty text
        _tabStrip.SelectedIndex = _tabs.IndexOf(t);
        if (t.Stale && _loaded)
        {
            var reason = t.StaleNewQuery ? SearchReason.NewQuery : SearchReason.IndexChanged;
            t.Stale = t.StaleNewQuery = false;
            RunSearch(reason);
        }
        if (focusContent) FocusTabContent();
    }

    /// <summary>Remembers the active tab's place in its results (by id, so it survives the results being refreshed).</summary>
    void RememberListPosition(FolderTab t)
    {
        t.FocusedId = -1;
        t.SelectedIds = null;
        t.TopIndex = 0;
        if (ExplorerShown || _ids.Length == 0 || !ResultsCurrent) return;
        int fi = FocusedIndex();
        if (fi >= 0 && fi < _ids.Length) t.FocusedId = _ids[fi];
        int selCount = _list.SelectedIndices.Count;
        if (selCount > 0 && selCount <= 5000)
        {
            t.SelectedIds = [];
            foreach (int i in _list.GetSelectedIndices(5000)) if (i < _ids.Length) t.SelectedIds.Add(_ids[i]);
        }
        try { t.TopIndex = _list.TopItem?.Index ?? 0; }
        catch (ArgumentException) { }
    }

    /// <summary>Puts the active tab's results in the list, with the selection, focus and scroll position it had.</summary>
    void ShowListPosition(FolderTab t)
    {
        if (_ids.Length > 0 && !ResultsCurrent) t.Stale = true; // renumbered by a compaction meanwhile: the ids can't be shown
        _suppress = true;
        _list.BeginUpdate();
        try
        {
            if (_list.SelectedIndices.Count > 0) _list.SetAllSelected(false);
            _list.VirtualListSize = _ids.Length;
            if (_ids.Length > 0 && ResultsCurrent)
            {
                SelectIds(t.FocusedId, t.SelectedIds);
                if (t.TopIndex > 0 && t.TopIndex < _ids.Length) _list.TopItem = _list.Items[t.TopIndex];
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        finally
        {
            _list.EndUpdate();
            _suppress = false;
        }
        _list.Invalidate();
        if (ThumbnailMode)
        {
            _grid.SetCount(_ids.Length, reset: true);
            // The tab's focused result, as the list shows it (the grid may stay shown across the switch).
            if (FocusedIndex() is int focus and >= 0) _grid.EnsureVisible(focus);
        }
    }

    /// <summary>Selects the results with these ids and focuses <paramref name="focusedId"/>'s; returns its index, or -1.</summary>
    int SelectIds(int focusedId, List<int>? selectedIds)
    {
        if (_ids.Length == 0 || (focusedId < 0 && selectedIds is null)) return -1;
        var want = new HashSet<int>(selectedIds ?? []);
        if (focusedId >= 0) want.Add(focusedId);
        var found = new Dictionary<int, int>(want.Count);
        for (int i = 0; i < _ids.Length && found.Count < want.Count; i++)
            if (want.Contains(_ids[i])) found[_ids[i]] = i;
        if (selectedIds is not null)
            foreach (int id in selectedIds)
                if (found.TryGetValue(id, out int ix)) _list.SetSelected(ix, true);
        if (focusedId < 0 || !found.TryGetValue(focusedId, out int fx)) return -1;
        _list.Items[fx].Focused = true;
        return fx;
    }

    /// <summary>Puts the keyboard in the active tab's files: the folder view, or the results.</summary>
    void FocusTabContent()
    {
        if (ExplorerShown) _explorer.FocusView();
        else if (ThumbnailMode) _grid.Focus();
        else _list.Focus();
    }

    /// <summary>Closes a tab; closing the last one closes the window, as in Explorer.</summary>
    void CloseTab(FolderTab t)
    {
        int i = _tabs.IndexOf(t);
        if (i < 0) return;
        if (_tabs.Count == 1) { Close(); return; }
        if (t == _tab) SelectTab(_tabs[i + 1 < _tabs.Count ? i + 1 : i - 1]);
        _tabs.RemoveAt(i);
        DisposeListing(t);
        _tabStrip.SelectedIndex = _tabs.IndexOf(_tab);
        _tabStrip.TabsChanged();
        t.Explorer.Visible = false;
        BeginInvoke(t.Explorer.Dispose); // not from inside one of the view's own callbacks
    }

    void CloseTabs(IEnumerable<FolderTab> tabs)
    {
        foreach (var t in tabs.ToList()) CloseTab(t);
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: the next or previous tab, round the end.</summary>
    void SwitchTab(int step)
    {
        if (_tabs.Count < 2) return;
        int i = _tabs.IndexOf(_tab) + step;
        SelectTab(_tabs[(i % _tabs.Count + _tabs.Count) % _tabs.Count]);
    }

    void MoveTab(int from, int to)
    {
        var t = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(to, t);
        _tabStrip.SelectedIndex = _tabs.IndexOf(_tab);
        _tabStrip.TabsChanged();
    }

    string? TabLocation(FolderTab t) => t.Browsing ? t.Explorer.CurrentParsingName : null;

    void ShowTabMenu(int index, Point screen)
    {
        if ((uint)index >= (uint)_tabs.Count) return;
        var t = _tabs[index];
        var others = Item("Close other tabs", () => { SelectTab(t); CloseTabs(_tabs.Where(x => x != t)); });
        var right = Item("Close tabs to the right", () =>
        {
            int at = _tabs.IndexOf(t);
            if (_tabs.IndexOf(_tab) > at) SelectTab(t);
            CloseTabs(_tabs.Skip(at + 1));
        });
        others.Enabled = _tabs.Count > 1;
        right.Enabled = index < _tabs.Count - 1;
        var m = new ContextMenuStrip();
        m.Items.AddRange(
        [
            Item("New tab", () => NewTab(), display: "Ctrl+T"),
            Item("Duplicate tab", () => OpenTab(TabLocation(t), _tabs.IndexOf(t) + 1, t.SearchText, t)),
            Item("Open in new window", () =>
            {
                var w = _app.CreateWindow(this);
                w.OpenAsExplorer(TabLocation(t));
                _app.ShowWindow(w);
            }),
            new ToolStripSeparator(),
            Item("Close tab", () => CloseTab(t), display: "Ctrl+W"),
            others,
            right,
        ]);
        ShowTransient(m, screen);
    }
}

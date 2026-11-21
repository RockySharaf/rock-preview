using Anything.Core;
using Anything.Core.Indexing;
using Anything.Explorer;
using Anything.Interop;

namespace Anything;

/// <summary>
/// Tabs, like Windows 11 File Explorer's: several folders and searches in one window. Each tab has its own Explorer
/// view (so its own Back/Forward history) and its own search, results, Thumbnails and sort; the window's chrome (address
/// row, command bar, navigation pane, results list, preview pane, status bar) shows the active tab. The other tabs'
/// views stay alive, hidden, so switching back is instant and the folder doesn't load again. A tab can move to another
/// window, view and all (MainForm.TearOff.cs).
/// </summary>
internal sealed partial class MainForm
{
    readonly TabStrip _tabStrip = new() { Dock = DockStyle.Fill };
    readonly List<FolderTab> _tabs = [];
    FolderTab _tab;         // the active tab
    readonly bool _adopted; // the window opened for a tab from another window, which it shows as it was there
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
    internal sealed class FolderTab : ITabStripItem
    {
        public readonly ExplorerHost Explorer = new() { Dock = DockStyle.Fill, Visible = false };

        public FolderTab()
        {
            // The view reports to the window the tab is in now, as tabs move between windows.
            Explorer.Navigated += () => Window?.OnExplorerNavigated(this);
            Explorer.SelectionChanged += () => Window?.OnTabSelectionChanged(this);
            Explorer.SelectionContentChanged += () => { if (Window?._tab == this) Window.QueueStatusUpdate(); }; // the selection's size
            Explorer.NavigationFailed += msg => Window?.OnTabNavigationFailed(this, msg);
            Explorer.FolderOpenInNewTab += location => Window?.OpenTabBehind(location); // its menu's "Open in new tab"
        }

        /// <summary>The window the tab is in.</summary>
        public MainForm? Window;
        public string SearchText = "";
        /// <summary>The search Back closed, which Forward shows again; dropped once another folder or search shows.</summary>
        public (string Text, SortKey Sort, bool Descending)? ForwardSearch;
        /// <summary>Where <see cref="ForwardSearch"/> was closed: the folder, and how many places Back held there.</summary>
        public (string? Place, int BackCount) ForwardSearchAt;
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
        /// <summary>Home's recent files shown as "every file in" (Thumbnails at Home), or null.</summary>
        public List<string>? ZapRecent;
        public string ZapLabel = "";
        public List<string> SelScope = [];
        public bool UseSelScope;
        /// <summary>
        /// Searches look everywhere though a folder is open or folders are selected (Alt+X, the scope chip, Search
        /// options), until another folder is opened; otherwise they look inside the selected folders or the open one.
        /// </summary>
        public bool SearchEverywhere;
        /// <summary>The tab has opened a folder already: the next one opened scopes searches to it.</summary>
        public bool Navigated;
        public (SortKey Key, bool Descending)? SortBeforeZap, SortFromFolder;
        public bool ExplorerStart;
        public string? ExplorerLocation;
        /// <summary>The results may be out of date (the index changed, or the search was cut short): searched again when it shows.</summary>
        public bool Stale, StaleNewQuery;
        // Where the results list was, kept while another tab shows (the list itself belongs to the window).
        public int FocusedId = -1, TopIndex;
        public List<int>? SelectedIds;

        public bool ShowsFolder => Browsing && SearchText.Length == 0 && !ZapActive;

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
        _tabStrip.TabDraggedOut += (i, pointer, hold) => TearOff(_tabs[i], pointer, hold);
        AcceptDropsOnTabs();
    }

    /// <summary>
    /// Files dragged onto a tab, as in File Explorer: held over it a moment, the tab opens, to drop them somewhere in its
    /// folder; dropped on the tab itself, they go into its folder (copied, unless a key says move).
    /// </summary>
    void AcceptDropsOnTabs()
    {
        var hover = new System.Windows.Forms.Timer { Interval = 600 };
        int hovered = -1;
        hover.Tick += (_, _) =>
        {
            hover.Stop();
            // Still over it (the drag may have left the strip, which says nothing here).
            if ((uint)hovered < (uint)_tabs.Count && _tabStrip.TabAtScreen(Cursor.Position) == hovered && _tabs[hovered] != _tab)
                SelectTab(_tabs[hovered]);
        };
        Disposed += (_, _) => hover.Dispose();
        ShellDragDrop.AcceptDrops(_tabStrip, screen =>
        {
            int i = _tabStrip.TabAtScreen(screen);
            if (i != hovered)
            {
                hovered = i;
                hover.Stop();
                if (i >= 0) hover.Start();
            }
            return i >= 0 && _tabs[i].ShowsFolder ? _tabs[i].Explorer.CurrentPath : null;
        });
    }

    /// <summary>Takes a tab into the window (a new one, or one from another window): its Explorer view goes with the other views, hidden until it shows.</summary>
    void AttachTab(FolderTab t)
    {
        t.Window = this;
        t.Explorer.OpenFoldersInPlace = _app.Settings.UseInsteadOfExplorer;
        _split.Panel1.Controls.Add(t.Explorer);
    }

    void OnTabSelectionChanged(FolderTab t)
    {
        if (t != _tab) return;
        QueuePreview();
        QueueStatusUpdate();
        UpdateSearchPlaceholder();
    }

    void OnTabNavigationFailed(FolderTab t, string message)
    {
        if (t != _tab)
        {
            // A tab opened behind (OpenTabBehind) whose folder failed to open later, through the view's events: it never
            // had a location, so it goes, as when its navigation fails at once, rather than stay an empty "New tab".
            if (!t.Explorer.HasLocation && DropTabBehind(t)) _statusRight.Text = message;
            return;
        }
        _statusRight.Text = message;
        if (IsHandleCreated) UpdateNavButtons(ExplorerShown); // a Back or Forward that failed put the history back
    }

    /// <summary>Opens a tab at the end of the strip and shows it: at <paramref name="location"/>, or where Explorer's new tabs open ("Open File Explorer to").</summary>
    public void NewTab(string? location = null) => OpenTab(location, -1, null, null);

    /// <summary>
    /// Opens a folder in a new tab at the end of the strip without showing it, as File Explorer's "Open in new tab" and a
    /// middle-click in its pane do: the tab you are on stays, and the new one loads its folder hidden.
    /// </summary>
    void OpenTabBehind(string location)
    {
        if (string.IsNullOrWhiteSpace(location) || IsDisposed) return;
        var from = _tab;
        var t = new FolderTab
        {
            MatchCase = from.MatchCase, WholeWord = from.WholeWord, MatchPath = from.MatchPath, Diacritics = from.Diacritics, Regex = from.Regex,
            FilterIndex = from.FilterIndex,
            Browsing = true,
        };
        (t.Sort, t.Descending) = from.SortBeforeZap ?? (from.Sort, from.Descending);
        _tabs.Add(t);
        AttachTab(t);
        _ = t.Explorer.Handle; // the shell view is created while hidden
        if (!t.Explorer.Navigate(location))
        {
            if (DropTabBehind(t)) _statusRight.Text = $"Can't open \"{location.Trim()}\"";
            return;
        }
        if (!_tabs.Contains(t)) return; // it failed inside Navigate and is gone already
        _tabStrip.SelectedIndex = _tabs.IndexOf(_tab);
        _tabStrip.TabsChanged();
    }

    /// <summary>Removes a tab that was never shown (opened behind, its folder failed to open). False when it isn't here any more.</summary>
    bool DropTabBehind(FolderTab t)
    {
        if (t == _tab || !_tabs.Remove(t)) return false;
        _split.Panel1.Controls.Remove(t.Explorer);
        t.Window = null;
        BeginInvoke(t.Explorer.Dispose);
        if (IsHandleCreated) { _tabStrip.SelectedIndex = _tabs.IndexOf(_tab); _tabStrip.TabsChanged(); }
        return true;
    }

    /// <summary>
    /// Opens a tab and shows it: at <paramref name="location"/>, or where File Explorer opens, at <paramref name="index"/> (-1 = the end). It
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
        if (location is not { Length: > 0 } || !BrowseTo(location)) GoToStartPlace();
        if (search is { Length: > 0 }) SetSearch(search);
        else RunSearch(SearchReason.NewQuery);
        BeginInvoke(() => { if (t == _tab) FocusTabContent(); }); // after whatever else wants the focus now (showing the window)
    }

    /// <summary>Shows another tab: its view, its search, and its results where it left them.</summary>
    void SelectTab(FolderTab t)
    {
        if (t == _tab || !_tabs.Contains(t)) return;
        bool focusContent = ContainsFocus && !_search.Focused; // typing in the search box carries on there
        LeaveTab(_tab);
        _tab = t;
        ShowTab(t);
        if (focusContent) FocusTabContent();
    }

    /// <summary>
    /// Leaves the active tab, for another tab or another window. A search that hasn't finished is done again when it
    /// shows again, and results nobody sees (its folder view is showing) are let go: they would be searched again anyway
    /// once they are needed.
    /// </summary>
    void LeaveTab(FolderTab old)
    {
        _breadcrumb.EndEdit();
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
    }

    /// <summary>Shows the active tab: its view, its search, and its results where it left them.</summary>
    void ShowTab(FolderTab t)
    {
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
        if (_tabs.Count == 1)
        {
            ClosedToBackground = true; // nothing left open: shown again, it starts where File Explorer opens
            Close();
            return;
        }
        if (t == _tab) SelectTab(_tabs[i + 1 < _tabs.Count ? i + 1 : i - 1]);
        _tabs.RemoveAt(i);
        DisposeListing(t);
        _tabStrip.SelectedIndex = _tabs.IndexOf(_tab);
        _tabStrip.TabsChanged();
        t.Explorer.Visible = false;
        BeginInvoke(t.Explorer.Dispose); // not from inside one of the view's own callbacks
    }

    /// <summary>Its last tab was closed (not the window's ×, nor the hotkey): shown again, it starts afresh (<see cref="StartAfresh"/>).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ClosedToBackground { get; set; }

    /// <summary>As a new window opens: one tab, where File Explorer opens ("Open File Explorer to"), no search.</summary>
    public void StartAfresh()
    {
        if (!_loaded) return;
        CloseTabs(_tabs.Where(t => t != _tab));
        if (_zapActive) ExitZap(rerun: false);
        if (_search.TextLength > 0)
        {
            _suppress = true;
            _search.Clear();
            _suppress = false;
        }
        GoToStartPlace();
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

    /// <summary>
    /// A new window on <paramref name="t"/>'s folder (by default the active tab's; Ctrl+N, a tab's Open in new window):
    /// the folder its search looks in when it shows results, where File Explorer opens when it has none. The keyboard
    /// goes to the new window's files, as in Explorer.
    /// </summary>
    public void OpenInNewWindow(FolderTab? t = null)
    {
        var w = _app.CreateWindow(this);
        w.OpenAsExplorer(TabLocation(t ?? _tab));
        _app.ShowWindow(w);
        w.BeginInvoke(() => { if (!w.IsDisposed) w.FocusTabContent(); }); // after ShowWindow's search box focus
    }

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
        var move = Item("Move tab to new window", () => MoveTabToNewWindow(t));
        others.Enabled = move.Enabled = _tabs.Count > 1;
        right.Enabled = index < _tabs.Count - 1;
        var m = new ContextMenuStrip();
        m.Items.AddRange(
        [
            Item("New tab", () => NewTab(), display: "Ctrl+T"),
            Item("Duplicate tab", () => OpenTab(TabLocation(t), _tabs.IndexOf(t) + 1, t.SearchText, t)),
            Item("Open in new window", () => OpenInNewWindow(t)),
            move,
            new ToolStripSeparator(),
            Item("Close tab", () => CloseTab(t), display: "Ctrl+W"),
            others,
            right,
        ]);
        ShowTransient(m, screen);
    }
}

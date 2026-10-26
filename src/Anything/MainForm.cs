using System.Diagnostics;
using Anything.Core;
using Anything.Core.Indexing;

namespace Anything;

/// <summary>An Everything-style search window: search box (+ optional filter), virtual result list, status bar.</summary>
internal sealed partial class MainForm : Form
{
    enum SearchReason { NewQuery, Resort, IndexChanged, Refresh }

    readonly AnythingApp _app;
    readonly DragPanel _searchBar = new();      // Explorer's address row (its empty space drags the window too)
    readonly Font _uiFont = ExplorerTheme.UiFont(); // 14px Segoe UI Variable, like Explorer's XAML chrome
    readonly TextBox _search = new();
    readonly ComboBox _filter = new();
    readonly FlowLayoutPanel _infoBar = new();
    readonly Label _infoLabel = new();
    readonly LinkLabel _infoAction = new();
    readonly FlowLayoutPanel _updateBar = new();  // a new version is out
    readonly Label _updateText = new() { AutoSize = true, Margin = new Padding(0, 2, 6, 2) };
    readonly LinkLabel _updateNowLink = new() { AutoSize = true, Text = "Update now", Margin = new Padding(0, 2, 12, 2) },
        _updateGetLink = new() { AutoSize = true, Text = "See what's new and get it", Margin = new Padding(0, 2, 12, 2) },
        _updateCancelLink = new() { AutoSize = true, Text = "Cancel", Margin = new Padding(0, 2, 0, 2), Visible = false },
        _updateLaterLink = new() { AutoSize = true, Text = "Not now", Margin = new Padding(0, 2, 0, 2) };
    readonly FlowLayoutPanel _leftOutBar = new(); // a whole drive is on the list of folders to leave out
    readonly Label _leftOutText = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 2, 6, 2) };
    readonly LinkLabel _leftOutInclude = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 2, 12, 2) };
    readonly LinkLabel _leftOutKeep = new() { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
    readonly FlowLayoutPanel _folderBar = new(); // the files shown don't show up in searches, and why
    readonly Label _folderText = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 2, 6, 2) };
    readonly LinkLabel _folderInclude = new() { AutoSize = true, UseMnemonic = false, Text = "Show them in searches", Margin = new Padding(0, 2, 12, 2) };
    readonly LinkLabel _folderDrive = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 2, 12, 2) };
    readonly LinkLabel _folderSettings = new() { AutoSize = true, Text = "Settings", Margin = new Padding(0, 2, 12, 2) };
    readonly System.Windows.Forms.Timer _listingTimer = new() { Interval = 150 };
    readonly ResultListView _list = new();
    readonly ThumbnailGrid _grid = new();
    readonly SplitContainer _split = new();
    readonly Anything.Preview.PreviewPane _preview = new();
    readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 120 };
    readonly StatusStrip _status = new();
    readonly ToolStripStatusLabel _statusLeft = new();
    readonly ToolStripStatusLabel _statusRight = new();
    readonly ToolStripButton _statusDetails = new() { AccessibleName = "Details", ToolTipText = "Display information about each item in the window.", DisplayStyle = ToolStripItemDisplayStyle.Image };
    readonly ToolStripButton _statusLarge = new() { AccessibleName = "Large icons", ToolTipText = "Display items by using large thumbnails.", DisplayStyle = ToolStripItemDisplayStyle.Image };
    readonly System.Windows.Forms.Timer _changeTimer = new();
    readonly System.Windows.Forms.Timer _iconTimer = new() { Interval = 120 };
    readonly Dictionary<string, ColumnSetting> _cols = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, Row> _rows = [];
    readonly TaskScheduler _ui;

    CancellationTokenSource? _cts;
    int _searchSeq;
    long _lastSearchMs;
    bool _pendingNewQuery;
    bool _loaded, _suppress, _statusPending, _infoDismissed;
    bool _searchFirst; // shown by Alt+X: the search box keeps the focus, not the file view
    int _colDpi = 96;
    Rectangle? _initialBounds;
    bool _initialMaximized;
    Font? _listFont, _boldFont, _searchFont;

    internal bool ForceClose;

    /// <summary>
    /// A window like <paramref name="source"/> (columns, search options, size), or as Settings say. With
    /// <paramref name="tab"/>, a tab from another window (<paramref name="source"/>) is its tab, with its own options.
    /// </summary>
    public MainForm(AnythingApp app, MainForm? source, FolderTab? tab = null)
    {
        _app = app;
        _ui = TaskScheduler.FromCurrentSynchronizationContext();
        var s = app.Settings;
        _tab = tab ?? new FolderTab();
        _adopted = tab is not null;

        if (source is not null)
        {
            source.SaveColumnState();
            foreach (var (k, v) in source._cols) _cols[k] = new ColumnSetting { Visible = v.Visible, Width = v.Width, Position = v.Position };
            if (tab is null)
            {
                (_matchCase, _wholeWord, _matchPath, _diacritics, _regex) = (source._matchCase, source._wholeWord, source._matchPath, source._diacritics, source._regex);
                (_filterIndex, _sort, _desc) = (source._filterIndex, source._sort, source._desc);
            }
            else TakeContentState(tab, source);
            var b = source.WindowState == FormWindowState.Normal ? source.Bounds : source.RestoreBounds;
            _initialBounds = new Rectangle(b.X + 24, b.Y + 24, b.Width, b.Height);
            TopMost = source.TopMost;
        }
        else
        {
            foreach (var (k, v) in s.Columns) _cols[k] = new ColumnSetting { Visible = v.Visible, Width = v.Width, Position = v.Position };
            (_matchCase, _wholeWord, _matchPath, _diacritics, _regex) = (s.MatchCase, s.MatchWholeWord, s.MatchPath, s.MatchDiacritics, s.Regex);
            (_filterIndex, _sort, _desc) = (Filters.IndexOf(s.Filter), s.Sort, s.SortDescending);
            if (!s.WindowBounds.IsEmpty && Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(s.WindowBounds)))
            {
                _initialBounds = s.WindowBounds;
                _initialMaximized = s.Maximized;
            }
            else
            {
                // The very first window opens where and as large as File Explorer's would (ExplorerPlacement); its own
                // size is remembered from then on.
                var first = ExplorerPlacement.ForFirstWindow(Screen.FromPoint(Cursor.Position));
                _initialBounds = first.Bounds;
                _initialMaximized = first.Maximized;
            }
            TopMost = s.AlwaysOnTop;
        }

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = app.TitleWithHotkey;
        Icon = app.AppIcon;
        ClientSize = new Size(820, 540);
        MinimumSize = new Size(320, 200);
        StartPosition = _initialBounds is null ? FormStartPosition.CenterScreen : FormStartPosition.Manual;

        BuildMenu();
        BuildBrowseBar();
        BuildNavTree();
        BuildSearchBar();
        BuildInfoBar();
        BuildUpdateBar();
        BuildLeftOutBar();
        BuildFolderBar();
        BuildThumbnailsHint();
        BuildList();
        BuildStatusBar();

        // Dock order (the Fill control first): address row on top, then the command bar, like Windows 11 File Explorer.
        // There is no menu bar: its menus live under the arrow of the command bar's Settings gear (Explorer's "See more").
        BackColor = ExplorerTheme.Content;
        Controls.Add(_body);
        Controls.Add(_infoBar);
        Controls.Add(_folderBar);
        Controls.Add(_leftOutBar);
        Controls.Add(_updateBar);
        Controls.Add(_commands);
        Controls.Add(_searchBar);
        BuildTitleRow();
        Controls.Add(_titleRow);
        Controls.Add(_status);
        ResumeLayout(false);
        PerformLayout();

        _changeTimer.Tick += (_, _) =>
        {
            _changeTimer.Stop();
            if (Visible && WindowState != FormWindowState.Minimized) RunSearch(SearchReason.IndexChanged);
            else _staleWhileHidden = true;
        };
        _listingTimer.Tick += (_, _) =>
        {
            _listingTimer.Stop();
            if (_listing == null) return;
            if (Visible) RunSearch(SearchReason.IndexChanged);
            else _staleWhileHidden = true;
        };
        _iconTimer.Tick += (_, _) =>
        {
            _iconTimer.Stop();
            foreach (var r in _rows.Values) r.Icon = -1;
            _list.Invalidate();
        };
    }

    // ---- layout -----------------------------------------------------------------------------

    /// <summary>Explorer's address row (measured: 48 high, 32px fields): navigation buttons | breadcrumbs | search box.</summary>
    void BuildSearchBar()
    {
        _searchBar.Dock = DockStyle.Top;
        _searchBar.Height = 48;
        _searchBar.BackColor = ExplorerTheme.Chrome;

        _search.AccessibleName = "Search";
        _search.PlaceholderText = "Search everything";
        _search.TextChanged += (_, _) =>
        {
            _tab.SearchText = _search.Text;
            UpdateSearchScope();
            UpdateTitle();
            if (!_suppress) RunSearch(SearchReason.NewQuery);
            UpdateLeftView(); // empty box while browsing = Explorer view; typing = instant results
        };
        _search.KeyDown += OnSearchKeyDown;
        _searchField = new SearchField(_search) { Dock = DockStyle.Right, Width = 300 };
        _searchField.ClearClicked += CloseSearch;
        _searchField.ScopeClicked += ToggleSearchScope;
        _addressGap.BackColor = ExplorerTheme.Chrome;

        _filter.DropDownStyle = ComboBoxStyle.DropDownList;
        _filter.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _filter.Margin = new Padding(4, 0, 0, 0);
        _filter.Width = 140;
        _filter.TabStop = false;
        _filter.AccessibleName = "Filter";
        foreach (var f in Filters.All) _filter.Items.Add(f.Name);
        _filter.SelectedIndex = _filterIndex;
        _filter.SelectedIndexChanged += (_, _) =>
        {
            if (_suppress || _filter.SelectedIndex < 0) return;
            SetFilter(_filter.SelectedIndex);
        };

        // The filter combo and "this folder only" box are not shown: both live in the Search options menu, as in Explorer.
        _searchBar.Controls.Add(_breadcrumb);   // fill
        _searchBar.Controls.Add(_addressGap);   // right
        _searchBar.Controls.Add(_searchField);  // right
        _searchBar.Controls.Add(_nav);          // left
        _searchBar.Resize += (_, _) => LayoutAddressRow();
    }

    void BuildInfoBar()
    {
        _infoBar.Dock = DockStyle.Top;
        _infoBar.AutoSize = true;
        _infoBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _infoBar.WrapContents = true;
        _infoBar.Padding = new Padding(6, 3, 6, 3);
        _infoBar.BackColor = SystemColors.Info;
        _infoBar.ForeColor = SystemColors.InfoText;
        _infoLabel.AutoSize = true;
        _infoLabel.Margin = new Padding(0, 2, 6, 2);
        _infoAction.AutoSize = true;
        _infoAction.Margin = new Padding(0, 2, 12, 2);
        _infoAction.LinkClicked += (_, _) => _app.TurnOnInstantIndexing(this);
        var dismiss = new LinkLabel { AutoSize = true, Text = "Dismiss", Margin = new Padding(0, 2, 0, 2) };
        dismiss.LinkClicked += (_, _) => { _infoDismissed = true; UpdateInfoBar(); };
        _infoBar.Controls.AddRange([_infoLabel, _infoAction, dismiss]);
        _infoBar.Visible = false;
    }

    void BuildUpdateBar()
    {
        _updateBar.Dock = DockStyle.Top;
        _updateBar.AutoSize = true;
        _updateBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _updateBar.WrapContents = true;
        _updateBar.Padding = new Padding(6, 3, 6, 3);
        _updateBar.BackColor = SystemColors.Info;
        _updateBar.ForeColor = SystemColors.InfoText;
        _updateNowLink.LinkClicked += (_, _) => _app.UpdateNow();
        _updateGetLink.LinkClicked += (_, _) => _app.OpenReleasePage();
        _updateCancelLink.LinkClicked += (_, _) => _app.CancelUpdate();
        _updateLaterLink.LinkClicked += (_, _) => _app.HideUpdateNotice();
        _updateBar.Controls.AddRange([_updateText, _updateNowLink, _updateGetLink, _updateCancelLink, _updateLaterLink]);
        // The sentence wraps in a narrow window rather than run past its edge (a reason why updating didn't work can be long).
        _updateBar.SizeChanged += (_, _) =>
        {
            var max = new Size(Math.Max(1, _updateBar.ClientSize.Width - _updateBar.Padding.Horizontal - _updateText.Margin.Horizontal), 0);
            if (_updateText.MaximumSize != max) _updateText.MaximumSize = max;
        };
        _updateBar.Visible = false;
        UpdateUpdateBar();
    }

    /// <summary>
    /// Shows the new-version bar while a newer Rock Preview is out (until "Not now"): Update now for the installed copy
    /// (else the release page), how far its download is, or why it didn't work.
    /// </summary>
    internal void UpdateUpdateBar()
    {
        // An Update now running or that didn't work shows even after "Not now" hid the notice (it was asked for since).
        bool show = _app.NewVersion is not null && (_app.ShowUpdateNotice || _app.UpdateProgress is not null || _app.UpdateError is not null);
        if (show && _app.NewVersion is { } v)
        {
            string version = v.ToString(3);
            bool inApp = _app.CanUpdateInApp, downloading = _app.UpdateProgress is not null;
            _updateText.Text = _app.UpdateProgress is { } p ? $"Downloading Rock Preview {version}... {(int)(p * 100)}%"
                : _app.UpdateError is { } error ? $"Rock Preview {version} is available, but updating to it didn't work. {error}"
                : $"Rock Preview {version} is available. You have {AnythingApp.CurrentVersion.ToString(3)}.";
            _updateNowLink.Text = _app.UpdateError is null ? "Update now" : "Try again";
            _updateNowLink.Visible = inApp && !downloading;
            _updateGetLink.Text = inApp ? "What's new" : "See what's new and get it";
            _updateGetLink.Visible = !downloading;
            _updateCancelLink.Visible = downloading;
            _updateLaterLink.Visible = !downloading;
        }
        if (_updateBar.Visible != show) _updateBar.Visible = show;
    }

    void BuildLeftOutBar()
    {
        _leftOutBar.Dock = DockStyle.Top;
        _leftOutBar.AutoSize = true;
        _leftOutBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _leftOutBar.WrapContents = true;
        _leftOutBar.Padding = new Padding(6, 3, 6, 3);
        _leftOutBar.BackColor = SystemColors.Info;
        _leftOutBar.ForeColor = SystemColors.InfoText;
        _leftOutInclude.LinkClicked += (_, _) =>
        {
            if (!_app.IncludeLeftOutDrives(_app.LeftOutDrives)) _app.ShowSettings(this, "Folders");
        };
        var settings = new LinkLabel { AutoSize = true, Text = "Settings", Margin = new Padding(0, 2, 12, 2) };
        settings.LinkClicked += (_, _) => _app.ShowSettings(this, "Folders");
        _leftOutKeep.LinkClicked += (_, _) => _app.KeepDrivesLeftOut(_app.LeftOutDrives);
        _leftOutBar.Controls.AddRange([_leftOutText, _leftOutInclude, settings, _leftOutKeep]);
        _leftOutBar.Visible = false;
    }

    /// <summary>
    /// Shows the bar while a whole drive is on Settings › Folders › Leave these folders out, so that nothing (or only
    /// the folders added from it) shows up in searches; "Keep it left out" stops it asking.
    /// </summary>
    internal void UpdateLeftOutBar()
    {
        var roots = _app.LeftOutDrives;
        bool show = roots.Count > 0 && !_folderBarShown && !FolderBarDismissedForLeftOutDrive();
        if (show)
        {
            var s = _app.Settings;
            var names = roots.Select(r => r.Length == 3 && r[1] == ':' ? r[..2] : r.TrimEnd('\\')).ToList();
            string drives = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
            bool someFolders = s.Index.Folders.Any(f => roots.Any(r => AppSettings.IsUnder(f, r)));
            string what = someFolders ? $"Only the folders you added from {drives} show up in searches" : $"Nothing on {drives} shows up in searches";
            string why = roots.Count == 1
                ? $"{s.ExclusionsHidingDrive(roots[0]).FirstOrDefault() ?? roots[0]} is on the list of folders to leave out."
                : roots.All(r => s.ExclusionsHidingDrive(r).Any(f => AppSettings.SamePath(f, r)))
                    ? "they're on the list of folders to leave out."
                    : "they, or folders they're mounted in, are on the list of folders to leave out.";
            _leftOutText.Text = $"{what}: {why}";
            _leftOutInclude.Text = roots.Count == 1 ? $"Show {names[0]} in searches" : "Show them in searches";
            _leftOutKeep.Text = roots.Count == 1 ? "Keep it left out" : "Keep them left out";
        }
        if (_leftOutBar.Visible != show) _leftOutBar.Visible = show;
    }

    void BuildList()
    {
        _list.Dock = DockStyle.Fill;
        _list.AccessibleName = "Results";
        _list.RetrieveVirtualItem += OnRetrieveVirtualItem;
        _list.CacheVirtualItems += OnCacheVirtualItems;
        _list.DrawColumnHeader += OnDrawColumnHeader;
        _list.DrawItem += OnDrawItem;
        _list.DrawSubItem += OnDrawSubItem;
        _list.IsOnRowContent = IsOnRowContent;
        _list.ColumnClick += (_, e) => SortByColumn(e.Column);
        _list.ColumnReordered += (_, _) => BeginInvoke(SaveColumnState);
        _list.SelectedIndexChanged += (_, _) => QueueStatusUpdate();
        _list.VirtualItemsSelectionRangeChanged += (_, _) => QueueStatusUpdate();
        _list.KeyDown += OnListKeyDown;
        _list.KeyPress += OnListKeyPress;
        _list.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && _list.HitTest(e.Location).Item is not null) OpenSelected();
        };
        _list.ItemDrag += (_, _) => DragSelection(_list);
        ShellDragDrop.AcceptDrops(_list, screen => DropFolderAt(_list, screen));
        _list.HeaderContextMenu += ShowHeaderMenu;
        _list.ItemContextMenu += ShowItemMenu;
        _list.SelectedIndexChanged += (_, _) => { QueuePreview(); if (ThumbnailMode) _grid.Invalidate(); };
        _list.VirtualItemsSelectionRangeChanged += (_, _) => { QueuePreview(); if (ThumbnailMode) _grid.Invalidate(); };

        BuildGrid();
        _preview.Dock = DockStyle.Fill;
        _split.Dock = DockStyle.Fill;
        _split.FixedPanel = FixedPanel.Panel2;
        _body.Panel2.Controls.Add(_split);
        _split.Panel1.Controls.Add(_list);
        _split.Panel1.Controls.Add(_grid);
        AttachTab(_tab);
        _split.Panel2.Controls.Add(_preview);
        _split.Panel2Collapsed = !_app.Settings.PreviewVisible;
        _split.BackColor = ExplorerTheme.Content;
        _split.Paint += (_, e) => PaintSplitter(_split, e.Graphics);
        _split.SplitterMoved += (_, _) =>
        {
            if (_loaded && !_split.Panel2Collapsed)
                _app.Settings.PreviewWidth = Math.Max(150, (_split.Width - _split.SplitterDistance) * 96 / Math.Max(96, DeviceDpi));
        };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            UpdatePreviewNow();
        };
    }

    void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.Visible = false;
        _grid.AccessibleName = "Results (thumbnails)";
        _grid.PathAt = i => (uint)i < (uint)_ids.Length ? PathOf(_ids[i]) : null;
        _grid.NameAt = i =>
        {
            try { return ResultsCurrent && (uint)i < (uint)_ids.Length ? _resultIndex!.Name(_ids[i]) : ""; }
            catch (IndexOutOfRangeException) { return ""; }
        };
        _grid.IsSelected = i => _list.IsItemSelected(i);
        _grid.FocusedIndex = FocusedIndex;
        _grid.ItemClicked += (i, mods) => SelectFromGrid(i, (mods & Keys.Shift) != 0, (mods & Keys.Control) != 0, toggle: true);
        _grid.Navigate += (i, shift, ctrl) => SelectFromGrid(i, shift, ctrl, toggle: false);
        _grid.ItemActivated += _ => OpenSelected();
        _grid.ItemContextMenu += pt => ShowSelectionMenu(pt, _grid);
        _grid.ItemDrag += () => DragSelection(_grid);
        ShellDragDrop.AcceptDrops(_grid, screen => DropFolderAt(_grid, screen));
        _grid.ZoomRequested += notches => SetViewMode(Math.Clamp(Math.Max(48, _app.Settings.ThumbnailSize) + notches * 24, 48, 512));
        _grid.ForwardKeyDown += OnListKeyDown;
        _grid.ForwardKeyPress += OnListKeyPress;
    }

    /// <summary>The files dragged out of the results now (a drop back onto their own folder does nothing).</summary>
    List<string>? _draggingOut;

    /// <summary>
    /// Drags the selected files out as the shell's own data, so any program takes them as it takes Explorer's (Outlook
    /// attaches them); copying unless a key says otherwise (Shift moves).
    /// </summary>
    void DragSelection(Control source)
    {
        var paths = SelectedPaths(20000);
        if (paths.Count == 0) return;
        _draggingOut = paths;
        try
        {
            if (ShellDragDrop.DragOut(source.Handle, paths)) return;
            var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(ShellDragDrop.DROPEFFECT_COPY)));
            source.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally { _draggingOut = null; }
    }

    /// <summary>
    /// Where a drop on the results goes: the folder under the pointer, or else the folder they are from ("every file
    /// in" one folder, or a search inside the open folder); nowhere for a search of everything.
    /// </summary>
    string? DropFolderAt(Control view, Point screen)
    {
        var p = view.PointToClient(screen);
        int index = view == _grid ? _grid.IndexAt(p) : _list.HitTest(p).Item?.Index ?? -1;
        string? folder = null;
        if (index >= 0 && index < _ids.Length && ResultsCurrent && _resultIndex!.IsAlive(_ids[index]) && _resultIndex.IsDirectory(_ids[index]))
            folder = _resultIndex.GetFullPath(_ids[index]);
        else if (_zapActive && _zapRoots.Count == 1) folder = _zapRoots[0];
        else folder = SearchFolder;
        // Files dragged from here onto their own folder (or onto themselves) would only be copied next to themselves.
        if (folder != null && _draggingOut is { } own && own.All(f => Setup.SamePath(f, folder)
                || Path.GetDirectoryName(f.TrimEnd('\\')) is { } parent && Setup.SamePath(parent, folder)))
            return null;
        return folder;
    }

    string? PathOf(int id)
    {
        if (!ResultsCurrent) return null;
        var index = _resultIndex!;
        try { return index.IsAlive(id) ? index.GetFullPath(id) : null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Whether the ids can be read: false after their index compacted (renumbered them) until the next results arrive.
    /// Ids are read only against the index they came from (<see cref="_resultIndex"/>): the app's, or a listing's.
    /// </summary>
    bool ResultsCurrent => _resultIndex is { } index && index.Generation == _resultGeneration;

    /// <summary>Applies a grid click/keyboard move to the list's selection (the single source of truth).</summary>
    void SelectFromGrid(int index, bool shift, bool ctrl, bool toggle)
    {
        if ((uint)index >= (uint)_ids.Length) return;
        if (shift && _grid.SelectionAnchor >= 0) _list.SelectRange(_grid.SelectionAnchor, index);
        else if (ctrl && toggle) _list.SetSelected(index, !_list.IsItemSelected(index));
        else if (!ctrl)
        {
            _list.SetAllSelected(false);
            _list.SetSelected(index, true);
        }
        _list.SetFocusedIndex(index);
        _grid.Invalidate();
        QueueStatusUpdate();
        QueuePreview();
    }

    // ---- view mode and preview ------------------------------------------------------------------

    int EffectiveThumbSize => _zapActive && _zapThumbs
        ? (_app.Settings.ThumbnailSize > 0 ? _app.Settings.ThumbnailSize : 160)
        : _app.Settings.ThumbnailSize;

    bool ThumbnailMode => EffectiveThumbSize > 0;

    /// <summary>0 = details list, otherwise thumbnails of that size. Shared by every window.</summary>
    void SetViewMode(int thumbnailSize)
    {
        _app.Settings.ThumbnailSize = thumbnailSize;
        if (thumbnailSize > 0) _lastThumbSize = thumbnailSize;
        ApplyViewMode();
        SyncMenus();
        UpdateCommandStates();
    }

    void ApplyViewMode()
    {
        bool thumbs = ThumbnailMode;
        int focus = FocusedIndex();
        if (thumbs)
        {
            bool shown = _grid.Visible;
            _grid.ThumbSize = EffectiveThumbSize;
            if (_listFont != null) _grid.Font = _listFont;
            _grid.BackColor = _list.BackColor;
            _grid.ForeColor = _list.ForeColor;
            _grid.SetCount(_ids.Length, reset: false);
            // Switching to Thumbnails shows the focused item; zooming keeps the place the grid is at.
            if (focus >= 0 && !shown) _grid.EnsureVisible(focus);
        }
        else if (focus >= 0 && focus < _ids.Length)
        {
            try { _list.EnsureVisible(focus); } catch (ArgumentException) { }
        }
        bool hadFocus = _list.Focused || _grid.Focused;
        bool explorer = ExplorerShown;
        _grid.Visible = thumbs && !explorer;
        _list.Visible = !thumbs && !explorer;
        if (hadFocus) (thumbs ? (Control)_grid : _list).Focus();
    }

    void TogglePreview()
    {
        _app.Settings.PreviewVisible = !_app.Settings.PreviewVisible;
        ApplyPreviewVisibility();
        SyncMenus();
    }

    void ApplyPreviewVisibility()
    {
        bool show = _app.Settings.PreviewVisible;
        if (show == !_split.Panel2Collapsed) return;
        _split.Panel2Collapsed = !show;
        if (show)
        {
            SetPreviewWidth();
            UpdatePreviewNow();
        }
        else ClearPreview();
    }

    void SetPreviewWidth()
    {
        if (_split.Panel2Collapsed || _split.Width <= 0) return;
        int want = _app.Settings.PreviewWidth * DeviceDpi / 96;
        int distance = Math.Clamp(_split.Width - want - _split.SplitterWidth, 120, Math.Max(120, _split.Width - 150));
        try { _split.SplitterDistance = distance; } catch (InvalidOperationException) { }
    }

    void QueuePreview()
    {
        if (!_loaded || _split.Panel2Collapsed) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    void UpdatePreviewNow()
    {
        if (!_loaded || _split.Panel2Collapsed) return;
        string? path = null;
        int fi = FocusedIndex();
        if (ExplorerShown) path = _explorer.SelectedPath;
        else if (fi >= 0 && fi < _ids.Length && _list.IsItemSelected(fi)) path = PathOf(_ids[fi]);
        else
        {
            var sel = _list.GetSelectedIndices(1);
            if (sel.Length > 0 && sel[0] < _ids.Length) path = PathOf(_ids[sel[0]]);
        }
        if (path is null) { ClearPreview(); return; }
        ShowPreview(path);
    }

    /// <summary>Explorer's status bar: item count on the left, the Details / Large icons view buttons on the right.</summary>
    void BuildStatusBar()
    {
        _status.RenderMode = ToolStripRenderMode.ManagerRenderMode;
        _status.SizingGrip = false;
        _status.BackColor = ExplorerTheme.StatusBack;
        _statusLeft.Spring = true;
        _statusLeft.TextAlign = ContentAlignment.MiddleLeft;
        _statusRight.TextAlign = ContentAlignment.MiddleRight;
        _statusDetails.Click += (_, _) => { if (ExplorerShown) _explorer.SetViewMode(Explorer.ShellNative.FVM_DETAILS, 16); else SetViewMode(0); QueueStatusUpdate(); };
        _statusLarge.Click += (_, _) => { if (ExplorerShown) _explorer.SetViewMode(Explorer.ShellNative.FVM_ICON, 96); else SetViewMode(160); QueueStatusUpdate(); };
        _status.Items.Add(_statusLeft);
        _status.Items.Add(_statusRight);
        _status.Items.Add(_statusDetails);
        _status.Items.Add(_statusLarge);
        _status.ShowItemToolTips = true;
        _status.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowStatusMenu(Cursor.Position); };
    }

    /// <summary>Status bar (measured from Explorer): 25 high, text 12 from the edge, 25px view buttons at the right.</summary>
    void ApplyStatusMetrics()
    {
        int S(int v) => LogicalToDeviceUnits(v);
        _status.AutoSize = false;
        _status.Height = S(25);
        _status.Padding = new Padding(S(12), 0, S(4), 0);
        _statusLeft.Margin = _statusRight.Margin = new Padding(0, 0, S(8), 0);
        foreach (var (b, svg) in new[] { (_statusDetails, ExplorerIcons.IconsDetails), (_statusLarge, ExplorerIcons.IconsLarge) })
        {
            b.AutoSize = false;
            b.Size = new Size(S(25), S(25));
            b.Margin = Padding.Empty;
            b.ImageScaling = ToolStripItemImageScaling.None;
            var old = b.Image;
            b.Image = ExplorerTheme.Icon16(svg, ExplorerTheme.GDetails, S(16));
            old?.Dispose();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ExplorerTheme.ApplyFrame(this);
        RemoveTitleBar();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyChromeMetrics();
        ExplorerTheme.StyleDropDowns(_commands.Items);
        if (_initialBounds is { } b)
        {
            Bounds = b;
            if (_initialMaximized) WindowState = FormWindowState.Maximized;
        }
        BuildColumns();
        ApplyUiSettings();
        SetPreviewWidth();
        ApplyNavPaneVisibility();
        SetNavPaneWidth();
        _loaded = true;
        ApplyViewMode();
        if (_adopted) ShowTab(_tab); // as it was in the window it came from
        else
        {
            OpenStartLocation();
            RunSearch(SearchReason.NewQuery);
        }
        UpdateStatus();
        // Like File Explorer, start in the file view (Ctrl+E / Ctrl+F go to the search box); Alt+X starts in the search box.
        if (_adopted) BeginInvoke(FocusTabContent);
        else if (_browsing && !_searchFirst) BeginInvoke(() => _explorer.FocusView());
        else ActiveControl = _search;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && _staleWhileHidden && _loaded)
        {
            var reason = _tab.StaleNewQuery ? SearchReason.NewQuery : SearchReason.IndexChanged;
            _staleWhileHidden = _tab.StaleNewQuery = false;
            RunSearch(reason);
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (_loaded) BeginInvoke(BuildColumns);
        if (_loaded) BeginInvoke(ApplyChromeMetrics);
        if (_loaded) BeginInvoke(ApplyRowHeight);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel) _app.OnWindowClosing(this, e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts?.Cancel();
        foreach (var t in _tabs) DisposeListing(t);
        _listingTimer.Dispose();
        DisposeThumbnailsHint();
        _changeTimer.Dispose();
        _iconTimer.Dispose();
        _previewTimer.Dispose();
        ClearPreview();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _listFont?.Dispose();
            _boldFont?.Dispose();
            _searchFont?.Dispose();
            _uiFont.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- public API used by the app --------------------------------------------------------

    public void SetSearch(string text)
    {
        _search.Text = text;
        _search.SelectionStart = _search.TextLength;
    }

    /// <summary>
    /// Alt+X: ready for a new search over everything, in the details list, whatever the window was showing (the open
    /// folder, every file in it, thumbnails). Opening a folder scopes searches to it again.
    /// </summary>
    public void SearchEverything()
    {
        if (!_loaded)
        {
            _tab.SearchEverywhere = _searchFirst = true;
            if (_app.Settings.ThumbnailSize > 0) _app.Settings.ThumbnailSize = 0;
            return;
        }
        if (_zapActive) ExitZap(rerun: true);
        bool rescope = SearchFolder != null || _useSelScope;
        _selScope = [];
        _useSelScope = false;
        _tab.SearchEverywhere = true;
        if (_app.Settings.ThumbnailSize > 0) SetViewMode(0);
        UpdateSearchPlaceholder();
        if (rescope && _search.TextLength > 0) RunSearch(SearchReason.NewQuery);
        QueueStatusUpdate();
    }

    public void FocusSearch(bool selectAll)
    {
        ActiveControl = _search;
        _search.Focus();
        if (selectAll) _search.SelectAll();
    }

    public void OnIndexChanged()
    {
        if (!_loaded) return;
        foreach (var t in _tabs) if (t != _tab) t.Stale = true; // searched again when they show
        if (!Visible) { _staleWhileHidden = true; return; }
        if (_changeTimer.Enabled) return;
        _changeTimer.Interval = (int)Math.Clamp(_lastSearchMs * 2, 300, 3000);
        _changeTimer.Start();
    }

    public void OnIndexStateChanged()
    {
        if (!_loaded) return;
        UpdateInfoBar();
        UpdateStatus();
        _miRunAsAdmin.Visible = !_app.IndexManager.IsElevated;
    }

    public void OnIconsResolved()
    {
        if (_loaded && !_iconTimer.Enabled) _iconTimer.Start();
    }

    /// <summary>Dates accessed read from the disk came in (date accessed not indexed): the rows show them now.</summary>
    public void OnDatesResolved()
    {
        if (_loaded && _cols.TryGetValue("date_accessed", out var c) && c.Visible) _list.Invalidate();
    }

    public void SaveStateTo(AppSettings s)
    {
        if (!_loaded || _tabs.Count == 0) return; // a window whose last tab went to another window has nothing to keep
        s.WindowBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        s.Maximized = WindowState == FormWindowState.Maximized;
        s.AlwaysOnTop = TopMost;
        SaveColumnState();
        foreach (var (k, v) in _cols) s.Columns[k] = new ColumnSetting { Visible = v.Visible, Width = v.Width, Position = v.Position };
        s.Sort = _sort;
        s.SortDescending = _desc;
        s.MatchCase = _matchCase;
        s.MatchWholeWord = _wholeWord;
        s.MatchPath = _matchPath;
        s.MatchDiacritics = _diacritics;
        s.Regex = _regex;
        s.Filter = _filterIndex == 0 ? "" : Filters.All[_filterIndex].Name;
    }

    public void ApplyUiSettings()
    {
        var s = _app.Settings;
        foreach (var t in _tabs) t.Explorer.OpenFoldersInPlace = s.UseInsteadOfExplorer; // Windows' "open folder" would start Rock Preview again
        _filter.Visible = s.FiltersVisible;
        _status.Visible = s.StatusBarVisible;
        if (_loaded) ApplyNavPaneVisibility();
        ApplyFont();
        if (_loaded)
        {
            ApplyPreviewVisibility();
            ApplyViewMode();
        }
        UpdateInfoBar();
        UpdateLeftOutBar();
        _miRunAsAdmin.Visible = !_app.IndexManager.IsElevated;
        SyncMenus();
        UpdateStatus();
    }

    public void ApplyFont()
    {
        var s = _app.Settings;
        Font baseFont = Font;
        Font? custom = null;
        if (!string.IsNullOrWhiteSpace(s.FontName))
        {
            try { custom = new Font(s.FontName, s.FontSize > 0 ? s.FontSize : Font.SizeInPoints); }
            catch (ArgumentException) { custom = null; }
        }
        var b = custom ?? baseFont;
        float size = Math.Max(6f, b.SizeInPoints + s.Zoom);
        // Control.Font ignores a value-equal font and keeps its current instance, so a new font is only adopted
        // (and the old one disposed) when it actually differs; otherwise controls would keep a disposed font
        // ("Parameter is not valid" when the thumbnail grid measured text).
        var listFont = new Font(b.FontFamily, size, b.Style);
        if (_listFont != null && _listFont.Equals(listFont)) listFont.Dispose();
        else
        {
            _list.Font = listFont;
            _grid.Font = listFont;
            var bold = new Font(listFont, FontStyle.Bold);
            var oldList = _listFont;
            var oldBold = _boldFont;
            _listFont = listFont;
            _boldFont = bold;
            oldList?.Dispose();
            oldBold?.Dispose();
        }
        if (custom != null && _searchFont != null && _searchFont.Equals(custom)) custom.Dispose();
        else
        {
            _search.Font = custom ?? _uiFont;
            var oldSearch = _searchFont;
            _searchFont = custom;
            oldSearch?.Dispose();
        }
        ApplyRowHeight();
    }

    /// <summary>Instant indexing that is off (turn it on), or a service older than this release (update it: its fixes, and its name).</summary>
    void UpdateInfoBar()
    {
        bool outdated = _app.OutdatedService is not null;
        bool off = !_app.IndexManager.IsElevated && !_app.IndexManager.UsesService && _app.Settings.Index.IndexNtfsVolumes;
        bool show = !_infoDismissed && !_app.IsDemo && (outdated || off); // the demo index isn't real indexing
        if (show)
        {
            string version = _app.OutdatedService is { Major: > 0 } v ? $" ({v.ToString(3)})" : "";
            string text = outdated
                ? $"Rock Preview's background service is an older version{version}. Update it to this one: Windows asks for permission one time."
                : "Instant indexing is off, so Rock Preview is scanning folders (slower). Turn it on once and it stays on: Windows asks for permission one time.";
            string action = outdated ? "Update the service" : "Turn on instant indexing";
            if (_infoLabel.Text != text) _infoLabel.Text = text;
            if (_infoAction.Text != action) _infoAction.Text = action;
        }
        if (_infoBar.Visible != show) _infoBar.Visible = show;
    }

    internal void UpdateTitle()
    {
        string app = _app.TitleWithHotkey;
        if (_zapActive) Text = $"Every file in {_zapLabel}{(_search.TextLength > 0 ? " · " + _search.Text : "")} - {app}";
        else if (_search.TextLength > 0) Text = _search.Text + " - " + app;
        else if (ExplorerShown) Text = (_explorer.CurrentDisplayName ?? "") + " - " + app;
        else Text = app;
        _tabStrip.TabsChanged(); // the tab's name follows the same changes
    }

    // ---- columns ----------------------------------------------------------------------------

    void BuildColumns()
    {
        if (_list.Columns.Count > 0) SaveColumnState();
        _colDpi = DeviceDpi;
        _list.BeginUpdate();
        try
        {
            _list.Columns.Clear();
            foreach (var c in Columns.All)
            {
                var cs = _cols[c.Key];
                if (c.Key == "content" && !ContentColumnShown()) continue;
                if (c.Key != "name" && !cs.Visible) continue;
                _list.Columns.Add(new ColumnHeader
                {
                    Text = c.Title,
                    Tag = c,
                    TextAlign = c.Key == "name" ? HorizontalAlignment.Left : c.Align,
                    Width = Math.Max(20, cs.Width * _colDpi / 96),
                });
            }
            var ordered = _list.Columns.Cast<ColumnHeader>()
                .OrderBy(ch => _cols[((ColumnDef)ch.Tag!).Key].Position).ThenBy(ch => ch.Index).ToList();
            for (int i = 0; i < ordered.Count; i++) ordered[i].DisplayIndex = i;
        }
        finally { _list.EndUpdate(); }
        _rows.Clear();
        UpdateSortIndicator();
        _list.IndentHeader();
        _list.Invalidate();
    }

    void SaveColumnState()
    {
        foreach (ColumnHeader ch in _list.Columns)
        {
            if (ch.Tag is not ColumnDef def) continue;
            var cs = _cols[def.Key];
            cs.Visible = true;
            cs.Width = Math.Max(20, ch.Width * 96 / Math.Max(1, _colDpi));
            cs.Position = ch.DisplayIndex;
        }
    }

    void ToggleColumn(string key)
    {
        if (key == "name") return;
        SaveColumnState();
        var cs = _cols[key];
        cs.Visible = !cs.Visible;
        if (cs.Visible) cs.Position = _list.Columns.Count;
        BuildColumns();
    }

    void UpdateSortIndicator()
    {
        int idx = -1;
        for (int i = 0; i < _list.Columns.Count; i++)
            if (_list.Columns[i].Tag is ColumnDef d && d.Sortable && d.Sort == _sort) { idx = i; break; }
        _list.SetSortIndicator(idx, _desc);
    }

    void SortByColumn(int column)
    {
        if (column < 0 || column >= _list.Columns.Count || _list.Columns[column].Tag is not ColumnDef def) return;
        if (!def.Sortable) return;
        if (def.Sort == _sort) SetSort(_sort, !_desc);
        else SetSort(def.Sort, false);
    }

    void SetSort(SortKey key, bool descending)
    {
        _sort = key;
        _desc = descending;
        _app.Tried(GettingStarted.Sort);
        UpdateSortIndicator();
        SyncMenus();
        RunSearch(SearchReason.Resort);
    }

    // ---- searching --------------------------------------------------------------------------

    SearchOptions CurrentOptions() => new()
    {
        MatchContent = ContentMatchOn(),
        MatchCase = _matchCase,
        MatchWholeWord = _wholeWord,
        MatchPath = _matchPath,
        MatchDiacritics = _diacritics,
        Regex = _regex,
        Filter = CombineFilters(Filters.All[_filterIndex].Search, BrowseFilter()),
    };

    static string? CombineFilters(string? a, string? b)
    {
        bool ha = !string.IsNullOrEmpty(a), hb = !string.IsNullOrEmpty(b);
        return ha && hb ? a + " " + b : ha ? a : hb ? b : null;
    }

    void RunSearch(SearchReason reason)
    {
        if (!_loaded || IsDisposed) return;
        if (reason == SearchReason.NewQuery) _pendingNewQuery = true;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        int seq = ++_searchSeq;
        string query = _search.Text;
        var options = CurrentOptions();
        var sort = _sort;
        bool desc = _desc;
        // "Every file in" a folder the index doesn't have comes from the tab's own listing, read from the disk.
        var listing = _listing;
        var engine = listing?.Engine ?? _app.SearchEngine;
        var index = listing?.Index ?? _app.IndexManager.Index;
        long generation = index.Generation;
        var sw = Stopwatch.StartNew();
        _searchPending = true;
        // "Every file in" with nothing typed says it is looking until the first results come in; a typed search keeps its text.
        if (reason == SearchReason.NewQuery && _zapActive) UpdateEmptyText();
        Task.Run(() => engine.Search(index, query, options, sort, desc, cts.Token), cts.Token)
            .ContinueWith(t =>
            {
                if (seq != _searchSeq || IsDisposed) return;
                _searchPending = false;
                _lastSearchMs = sw.ElapsedMilliseconds;
                if (t.IsCanceled) return;
                SearchResults results;
                if (t.IsFaulted)
                {
                    var ex = t.Exception!.GetBaseException();
                    if (ex is OperationCanceledException) return;
                    results = new SearchResults { Ids = [], Error = ex.Message };
                }
                else results = t.Result ?? SearchResults.Empty;
                ApplyResults(results, query, options, reason, index, generation);
            }, CancellationToken.None, TaskContinuationOptions.None, _ui);
    }

    void ApplyResults(SearchResults results, string query, SearchOptions options, SearchReason reason, FileIndex index, long generation)
    {
        if (_pendingNewQuery) { reason = SearchReason.NewQuery; _pendingNewQuery = false; }
        // A selection rectangle being drawn: a new search or order makes it meaningless (otherwise it follows below).
        if (reason is SearchReason.NewQuery or SearchReason.Resort) _list.CancelMarquee();
        else _list.RowsReplacing(); // the selection kept below is the one from before the rectangle

        // Remember focus/selection by id so they survive the refresh (only meaningful within one id generation).
        int focusedId = -1;
        List<int>? selectedIds = null;
        if (_ids.Length > 0 && ReferenceEquals(index, _resultIndex) && generation == _resultGeneration)
        {
            int fi = FocusedIndex();
            if (fi >= 0 && fi < _ids.Length) focusedId = _ids[fi];
            int selCount = _list.SelectedIndices.Count;
            if (selCount > 0 && selCount <= 5000)
            {
                selectedIds = [];
                foreach (int i in _list.GetSelectedIndices(5000)) if (i < _ids.Length) selectedIds.Add(_ids[i]);
            }
        }

        _ids = results.Ids ?? [];
        _resultIndex = index;
        _resultGeneration = generation;
        _rows.Clear();
        _resultQuery = query;
        _resultOptions = options;
        _searchError = results.Error;
        OnContentResults(results);

        _suppress = true;
        _list.BeginUpdate();
        int newFocus = -1;
        try
        {
            if (_list.SelectedIndices.Count > 0) _list.SetAllSelected(false);
            _list.VirtualListSize = _ids.Length;
            newFocus = SelectIds(focusedId, selectedIds);
            if (_ids.Length > 0)
            {
                if (reason == SearchReason.NewQuery) _list.EnsureVisible(0);
                else if (reason == SearchReason.Resort) _list.EnsureVisible(newFocus >= 0 ? newFocus : 0);
            }
        }
        catch (ArgumentException) { }
        finally
        {
            _list.EndUpdate();
            _suppress = false;
        }
        _list.Invalidate();
        _list.RowsReplaced();
        if (ThumbnailMode)
        {
            _grid.SetCount(_ids.Length, reset: reason == SearchReason.NewQuery);
            // A new sort order moves the focused file: the grid follows it, as the list does.
            if (reason == SearchReason.Resort && newFocus >= 0) _grid.EnsureVisible(newFocus);
        }
        UpdateStatus();
        QueuePreview();
        UpdateFolderBar(); // a search inside a folder the index doesn't have says so, and so does the empty text
        if (_ids.Length == 0) CheckDiskForMissedFiles();
    }

    int FocusedIndex()
    {
        try { return _list.FocusedItem?.Index ?? -1; }
        catch (ArgumentException) { return -1; }
    }

    // ---- virtual list -------------------------------------------------------------------------

    Row? RowAt(int index)
    {
        if ((uint)index >= (uint)_ids.Length) return null;
        if (!ResultsCurrent) return null; // renumbered by a compaction: show blanks until the re-search lands
        if (_rows.TryGetValue(index, out var r)) return r;
        if (_rows.Count > 3000) _rows.Clear();
        r = Row.Build(_resultIndex!, _ids[index]);
        _rows[index] = r;
        return r;
    }

    void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        var row = RowAt(e.ItemIndex);
        int n = Math.Max(1, _list.Columns.Count);
        if (row is null)
        {
            var blank = new ListViewItem("");
            for (int c = 1; c < n; c++) blank.SubItems.Add("");
            e.Item = blank;
            return;
        }
        if (row.Item is null || row.Item.SubItems.Count != n)
        {
            var item = new ListViewItem(row.Name);
            for (int c = 1; c < n; c++)
                item.SubItems.Add(_list.Columns[c].Tag is ColumnDef d ? row.Text(d) : "");
            row.Item = item;
        }
        SetContentToolTip(row.Item, row);
        e.Item = row.Item;
    }

    void OnCacheVirtualItems(object? sender, CacheVirtualItemsEventArgs e)
    {
        int end = Math.Min(e.EndIndex, _ids.Length - 1);
        for (int i = Math.Max(0, e.StartIndex); i <= end; i++) RowAt(i);
    }

    // ---- status bar ---------------------------------------------------------------------------

    void QueueStatusUpdate()
    {
        if (_statusPending || _suppress) return;
        _statusPending = true;
        BeginInvoke(() =>
        {
            _statusPending = false;
            UpdateStatus();
        });
    }

    void UpdateStatus()
    {
        if (!_loaded) return;
        UpdateCommandStates();
        _statusDetails.Checked = ExplorerShown ? _explorer.ViewMode == Explorer.ShellNative.FVM_DETAILS : !ThumbnailMode;
        _statusLarge.Checked = ExplorerShown ? _explorer.ViewMode == Explorer.ShellNative.FVM_ICON : ThumbnailMode;
        if (ExplorerShown)
        {
            // Explorer's wording: "16 items  |  1 item selected  2.34 KB".
            int es = _explorer.SelectedCount, all = _explorer.ItemCount;
            string text = $"{all:N0} item{(all == 1 ? "" : "s")}    |";
            if (es > 0)
            {
                text += $"    {es:N0} item{(es == 1 ? "" : "s")} selected";
                if (es == 1 && _explorer.SelectedPath is { } sp && File.Exists(sp))
                {
                    try { text += "  " + FileOps.FormatByteSize(new FileInfo(sp).Length); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            _statusLeft.Text = text;
            UpdateStatusRight();
            return;
        }
        int n = _ids.Length;
        string left = _zapActive ? ZapStatus() : n == 1 ? "1 object" : $"{n:N0} objects";
        if (!_zapActive && _search.TextLength > 0 && ScopeLabel() is { } where) left += " in " + where;
        int sel = _list.SelectedIndices.Count;
        if (sel > 0)
        {
            left += $", {sel:N0} selected";
            if (sel <= 100_000 && ResultsCurrent)
            {
                var index = _resultIndex!;
                long total = 0;
                bool any = false;
                foreach (int i in _list.GetSelectedIndices(100_000))
                {
                    if (i >= _ids.Length) continue;
                    int id = _ids[i];
                    try
                    {
                        if (index.IsDirectory(id)) continue;
                        long size = index.Size(id);
                        if (size > 0) total += size;
                        any = true;
                    }
                    catch (IndexOutOfRangeException) { }
                }
                if (any) left += $" ({Row.FormatSize(total)})";
            }
        }
        _statusLeft.Text = left;
        UpdateStatusRight();
    }

    void UpdateStatusRight()
    {
        var im = _app.IndexManager;
        // The folder view shows no search: an error or hint left from results no longer on screen isn't about it.
        bool folderView = ExplorerShown;
        string? searchError = folderView ? null : _searchError;
        string? listingLine = null;
        if (_zapActive && _listing is { State: ListingState.Reading } reading)
            listingLine = $"Reading {_zapLabel} ({reading.Found:N0} objects)"; // every file and folder read, before any filter
        else if (_zapActive && _listing is { Problem: { } problem } listing && string.IsNullOrEmpty(searchError))
            // Said even with files on screen: read before a drive was removed or a share went away, they may be gone.
            // Not over a search's own error (a bad regex, say), which nothing else would say.
            listingLine = listing.State == ListingState.Unavailable ? $"{_zapLabel} can't be read: {problem}" : $"Some of the folders can't be read: {problem}";
        string right;
        if (listingLine != null)
            right = listingLine;
        else if (im.State is IndexState.Loading or IndexState.Scanning or IndexState.Error && !string.IsNullOrEmpty(im.StatusText))
            right = im.StatusText;
        else if (!string.IsNullOrEmpty(searchError))
            right = searchError;
        else
        {
            var parts = new List<string>();
            if (_matchCase) parts.Add("CASE");
            if (_wholeWord) parts.Add("WHOLE WORD");
            if (_matchPath) parts.Add("PATH");
            if (_diacritics) parts.Add("DIACRITICS");
            if (_regex) parts.Add("REGEX");
            if (_filterIndex > 0) parts.Add(Filters.All[_filterIndex].Name);
            if (_app.HotkeyError is { } he) parts.Add(he);
            ContentStatusParts(parts, folderView);
            right = string.Join("   ", parts);
        }
        if (!folderView) right = ContentStatusRight(right, listingLine != null);
        _statusRight.Text = right;
        _statusRight.ToolTipText = right;
    }
}

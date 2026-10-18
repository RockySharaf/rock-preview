using Anything.Core;
using Anything.Interop;
using Anything.Explorer;

namespace Anything;

/// <summary>
/// Browsing: the real Windows Explorer view (ExplorerBrowser) merged with Rock Preview's instant search.
/// Empty search box + a folder open = Explorer; typing = instant results (scoped to the open folder unless
/// "In this folder" is off); ZAP = every file under the folder, straight from the index.
/// </summary>
internal sealed partial class MainForm
{
    readonly ExplorerHost _explorer = new();
    readonly ExplorerToolStrip _commands = new(ExplorerBarKind.Command) { BottomDivider = true }; // Explorer's command bar
    readonly ExplorerToolStrip _nav = new(ExplorerBarKind.Navigation); // back / forward / up / refresh
    readonly ToolStripButton _navBack = new(), _navForward = new(), _navUp = new(), _navRefresh = new();
    readonly TextBox _address = new();         // the address bar's edit mode
    BreadcrumbBar _breadcrumb = null!;
    readonly ToolStripDropDownButton _cmdNew = new("New"), _cmdSort = new("Sort"), _cmdView = new("View"),
                                     _cmdSearchOptions = new("Search options"), _cmdMore = new();
    readonly ToolStripButton _cmdCut = new(), _cmdCopy = new(), _cmdPaste = new(), _cmdRename = new(), _cmdShare = new(),
                             _cmdDelete = new(), _cmdCloseSearch = new("Close search"), _cmdPreview = new("Preview");
    readonly ToolStripSplitButton _zapButton = new("ZAP");
    readonly ToolStripMenuItem _zapListItem = new("Show as list"), _zapThumbItem = new("Show as thumbnails"),
                               _zapDeepItem = new("Include subfolders");
    readonly ToolStripSeparator _sepSearch = new();
    readonly CheckBox _scope = new();          // not shown: "search this folder only" state (Search options menu)
    readonly SplitContainer _body = new();     // navigation pane | files + preview
    readonly Panel _navPanel = new();
    readonly List<(ToolStripItem Item, string? Svg, char Glyph)> _glyphItems = [];
    readonly Panel _addressGap = new() { Dock = DockStyle.Right };
    SearchField _searchField = null!;

    bool _browsing;                 // a location is open in the Explorer view
    bool _zapActive, _zapThumbs;
    List<string> _zapRoots = [];
    string _zapLabel = "";

    /// <summary>Windows 11 "Home" (falls back to This PC where it doesn't exist).</summary>
    const string HomeLocation = "shell:::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
    const string ThisPcLocation = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    // ---- layout ---------------------------------------------------------------------------------

    void BuildBrowseBar()
    {
        // Command bar, like Windows 11 File Explorer's: New | Cut Copy Paste Rename Share Delete | Sort View | ZAP | ...
        _commands.Dock = DockStyle.Top;
        _commands.BackColor = ExplorerTheme.Content;
        _commands.CanOverflow = true;

        // Icons are Explorer's own (the SVGs in this PC's File Explorer package); the glyph is the fallback.
        void Cmd(ToolStripItem b, string? svg, char glyph, string? text, string tip, Action? click)
        {
            _glyphItems.Add((b, svg, glyph));
            b.ToolTipText = tip;
            b.AutoToolTip = false;
            b.DisplayStyle = text is null ? ToolStripItemDisplayStyle.Image : ToolStripItemDisplayStyle.ImageAndText;
            if (text is not null) b.Text = text;
            b.ImageScaling = ToolStripItemImageScaling.None;
            if (click is not null) b.Click += (_, _) => click();
        }
        Cmd(_cmdNew, ExplorerIcons.NewItem, ExplorerTheme.GNew, "New", "Create a new item", null);
        Cmd(_cmdCut, ExplorerIcons.Cut, ExplorerTheme.GCut, null, "Cut (Ctrl+X)", () => CmdClipboard(cut: true));
        Cmd(_cmdCopy, ExplorerIcons.Copy, ExplorerTheme.GCopy, null, "Copy (Ctrl+C)", () => CmdClipboard(cut: false));
        Cmd(_cmdPaste, ExplorerIcons.Paste, ExplorerTheme.GPaste, null, "Paste (Ctrl+V)", CmdPaste);
        Cmd(_cmdRename, ExplorerIcons.Rename, ExplorerTheme.GRename, null, "Rename (F2)", CmdRename);
        Cmd(_cmdShare, ExplorerIcons.Share, ExplorerTheme.GShare, null, "Share", CmdShare);
        Cmd(_cmdDelete, ExplorerIcons.Delete, ExplorerTheme.GDelete, null, "Delete (Del)", CmdDelete);
        Cmd(_cmdSort, ExplorerIcons.Sort, ExplorerTheme.GSort, "Sort", "Sort the items", null);
        Cmd(_cmdView, ExplorerIcons.View, ExplorerTheme.GView, "View", "Change the view", null);
        Cmd(_cmdSearchOptions, ExplorerIcons.SearchOptions, ExplorerTheme.GFilter, "Search options", "Where and how to search", null);
        Cmd(_cmdCloseSearch, ExplorerIcons.CloseSearch, ExplorerTheme.GClose, "Close search", "Close search (Esc)", CloseSearch);
        Cmd(_zapButton, null, ExplorerTheme.GZap, "ZAP", "ZAP: every file in this folder at once (Ctrl+Shift+Z)", null);
        Cmd(_cmdMore, null, ExplorerTheme.GMore, null, "See more", null);
        Cmd(_cmdPreview, ExplorerIcons.DetailsPane, ExplorerTheme.GPane, "Preview", "Show or hide the preview pane (Alt+P)", TogglePreview);
        _cmdPreview.Alignment = ToolStripItemAlignment.Right;
        _cmdMore.ShowDropDownArrow = false;
        _cmdMore.AccessibleName = "See more";

        // "New": Explorer's own New menu (Folder, Shortcut, Text Document...) for the open folder.
        _cmdNew.DropDownItems.Add(new ToolStripMenuItem("(new)"));
        _cmdNew.DropDown.Opening += (_, e) => { e.Cancel = true; BeginInvoke(ShowNewMenu); };

        _cmdSort.DropDownOpening += (_, _) => BuildSortMenu();
        _cmdView.DropDownOpening += (_, _) => BuildViewMenu();
        _cmdSearchOptions.DropDownOpening += (_, _) => BuildSearchOptionsMenu();
        _cmdSort.DropDownItems.Add(new ToolStripMenuItem("(sort)")); // placeholders so the arrow shows
        _cmdView.DropDownItems.Add(new ToolStripMenuItem("(view)"));
        _cmdSearchOptions.DropDownItems.Add(new ToolStripMenuItem("(search)"));

        // ZAP: click = every file in the folder (again = back to the folder); the arrow picks list/thumbnails and depth.
        _zapButton.ButtonClick += (_, _) => { if (_zapActive) ExitZap(rerun: true); else ToggleZap(_app.Settings.ZapThumbnails); };
        _zapListItem.Click += (_, _) => SetZapThumbnails(false);
        _zapThumbItem.Click += (_, _) => SetZapThumbnails(true);
        _zapDeepItem.Click += (_, _) => ToggleZapDepth();
        _zapListItem.ShortcutKeyDisplayString = "Ctrl+Shift+Z";
        _zapThumbItem.ShortcutKeyDisplayString = "Ctrl+Shift+X";
        _zapButton.DropDownItems.AddRange([_zapListItem, _zapThumbItem, new ToolStripSeparator(), _zapDeepItem]);
        _zapButton.DropDownOpening += (_, _) =>
        {
            _zapListItem.Checked = !_app.Settings.ZapThumbnails;
            _zapThumbItem.Checked = _app.Settings.ZapThumbnails;
            _zapDeepItem.Checked = _app.Settings.ZapIncludeSubfolders;
        };

        _commands.Items.AddRange([_cmdNew, new ToolStripSeparator(), _cmdCut, _cmdCopy, _cmdPaste, _cmdRename, _cmdShare, _cmdDelete,
                                  new ToolStripSeparator(), _cmdSort, _cmdView, _sepSearch, _cmdSearchOptions, _cmdCloseSearch,
                                  new ToolStripSeparator(), _zapButton, new ToolStripSeparator(), _cmdMore, _cmdPreview]);

        // Navigation buttons left of the address bar.
        _nav.Dock = DockStyle.Left;
        _nav.AutoSize = true;
        _nav.CanOverflow = false;
        _nav.BackColor = ExplorerTheme.Chrome;
        Cmd(_navBack, null, ExplorerTheme.GBack, null, "Back (Alt+Left)", () => _explorer.GoBack());
        Cmd(_navForward, null, ExplorerTheme.GForward, null, "Forward (Alt+Right)", () => _explorer.GoForward());
        Cmd(_navUp, null, ExplorerTheme.GUp, null, "Up to the parent folder (Alt+Up)", NavigateUp);
        Cmd(_navRefresh, null, ExplorerTheme.GRefresh, null, "Refresh (F5)", RefreshResults);
        _nav.Items.AddRange([_navBack, _navForward, _navUp, _navRefresh]);

        // Address bar: breadcrumbs; click empty space (or Ctrl+L / Alt+D / F4) to type a path.
        _address.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _address.AutoCompleteSource = AutoCompleteSource.FileSystemDirectories;
        _address.AccessibleName = "Address";
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = e.SuppressKeyPress = true;
                string target = _address.Text;
                _breadcrumb.EndEdit();
                if (BrowseTo(target)) FocusList();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = e.SuppressKeyPress = true;
                _breadcrumb.EndEdit();
                SyncAddress();
                FocusList();
            }
        };
        _breadcrumb = new BreadcrumbBar(_address) { Dock = DockStyle.Fill, MenuRenderer = ToolStripManager.Renderer };
        _breadcrumb.NavigateRequested += location =>
        {
            if (_zapActive) ExitZap(rerun: false);
            if (_search.TextLength > 0) { _suppress = true; _search.Clear(); _suppress = false; }
            BrowseTo(location);
        };

        _scope.CheckedChanged += (_, _) =>
        {
            if (_suppress) return;
            _app.Settings.ScopeSearchToFolder = _scope.Checked;
            UpdateSearchPlaceholder();
            RunSearch(SearchReason.NewQuery);
        };

        // Navigation pane | files + preview.
        _navPanel.Dock = DockStyle.Fill;
        _navPanel.BackColor = ExplorerTheme.Content;
        _body.Dock = DockStyle.Fill;
        _body.FixedPanel = FixedPanel.Panel1;
        _body.BackColor = ExplorerTheme.Content;
        _body.Panel1.Controls.Add(_navPanel);
        _body.Paint += (_, e) => PaintSplitter(_body, e.Graphics);
        _body.SplitterMoved += (_, _) =>
        {
            if (_loaded && !_body.Panel1Collapsed)
                _app.Settings.NavPaneWidth = Math.Max(120, _body.SplitterDistance * 96 / Math.Max(96, DeviceDpi));
        };

        _explorer.Dock = DockStyle.Fill;
        _explorer.Visible = false;
        _explorer.Navigated += OnExplorerNavigated;
        _explorer.SelectionChanged += () => { QueuePreview(); QueueStatusUpdate(); };
        _explorer.NavigationFailed += msg => _statusRight.Text = msg;
    }

    /// <summary>A thin Explorer-style divider in the middle of a splitter (instead of the grey bar).</summary>
    static void PaintSplitter(SplitContainer split, Graphics g)
    {
        var r = split.SplitterRectangle;
        using (var bg = new SolidBrush(ExplorerTheme.Content)) g.FillRectangle(bg, r);
        using var pen = new Pen(ExplorerTheme.Divider);
        if (split.Orientation == Orientation.Vertical)
        {
            int x = r.Left + r.Width / 2;
            g.DrawLine(pen, x, r.Top, x, r.Bottom);
        }
        else
        {
            int y = r.Top + r.Height / 2;
            g.DrawLine(pen, r.Left, y, r.Right, y);
        }
    }

    /// <summary>
    /// Sizes the address row and command bar like Explorer's for the current DPI: 32px-high fields and buttons,
    /// 40px-wide icon buttons, 16px icons. Called once the form has scaled (OnLoad) and on DPI changes.
    /// </summary>
    void ApplyChromeMetrics()
    {
        int S(int v) => LogicalToDeviceUnits(v);
        int icon = S(16);
        foreach (var (item, svg, glyph) in _glyphItems)
        {
            var old = item.Image;
            item.Image = glyph == ExplorerTheme.GZap ? ExplorerTheme.Glyph(glyph, icon, ExplorerTheme.Accent) // ZAP: Rock Preview's own, in the accent like Explorer's two-tone icons
                       : item.Owner == _nav ? ExplorerTheme.Glyph(glyph, icon, ExplorerTheme.Icon, 0.9f)
                       : ExplorerTheme.Icon16(svg, glyph, icon);
            old?.Dispose();
        }
        _commands.ImageScalingSize = _nav.ImageScalingSize = new Size(icon, icon);
        // Measured from Explorer at 125%: buttons 43 high in a 46 row, first button 6 from the edge, 8 between buttons.
        _commands.Padding = new Padding(S(2), S(1), S(6), S(2));
        _commands.LayoutExplorerItems(_uiFont);
        _nav.Padding = new Padding(S(11), 0, 0, 0);
        _nav.LayoutExplorerItems(_uiFont);
        for (int i = 0; i < _nav.Items.Count; i++) _nav.Items[i].Margin = new Padding(0, 0, i == _nav.Items.Count - 1 ? S(14) : S(16), 0);
        _searchBar.Padding = new Padding(0, S(8), S(11), S(8));
        _searchBar.Height = S(48);
        _addressGap.Width = S(8);
        LayoutAddressRow();
        _body.SplitterWidth = Math.Max(3, S(4));
        _split.SplitterWidth = Math.Max(3, S(4));
        ApplyStatusMetrics();
    }

    void LayoutAddressRow()
    {
        // Explorer's search box takes about a quarter of the row, within limits.
        int w = _searchBar.ClientSize.Width - _searchBar.Padding.Horizontal;
        _searchField.Width = Math.Clamp(w / 4, LogicalToDeviceUnits(180), LogicalToDeviceUnits(378));
    }

    void SetNavPaneWidth()
    {
        if (_body.Panel1Collapsed || _body.Width <= 0) return;
        int want = _app.Settings.NavPaneWidth * DeviceDpi / 96;
        try { _body.SplitterDistance = Math.Clamp(want, 80, Math.Max(80, _body.Width - 300)); } catch (InvalidOperationException) { }
    }

    void ApplyNavPaneVisibility()
    {
        bool show = _app.Settings.ExplorerNavPane;
        if (_body.Panel1Collapsed == !show) return;
        _body.Panel1Collapsed = !show;
        if (show) SetNavPaneWidth();
    }

    void UpdateSearchPlaceholder()
    {
        bool scoped = _browsing && _scope.Checked && _explorer.CurrentPath != null;
        _search.PlaceholderText = scoped ? $"Search {_explorer.CurrentDisplayName}" : "Search everything";
    }

    // ---- command bar actions ------------------------------------------------------------------

    void CmdNewFolder()
    {
        if (!ExplorerShown || _explorer.CurrentPath is not { } folder) return;
        _explorer.CreateNewFolder(folder);
    }

    /// <summary>Explorer's own "New" menu (Folder, Shortcut, Text Document...) under the New button.</summary>
    void ShowNewMenu()
    {
        if (!ExplorerShown || _explorer.CurrentPath is not { } folder) return;
        var b = _cmdNew.Bounds;
        var pt = _commands.PointToScreen(new Point(b.Left, b.Bottom));
        object? view = _explorer.GetShellViewObject();
        try
        {
            if (!ShellContextMenu.ShowNewMenu(this, folder, view, pt)) CmdNewFolder();
        }
        finally
        {
            if (view != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(view);
        }
    }

    void CmdClipboard(bool cut)
    {
        if (ExplorerShown) { if (!_explorer.InvokeVerbOnSelection(cut ? "cut" : "copy")) _explorer.SendToView(cut ? "^x" : "^c"); }
        else CopyFiles(cut);
    }

    void CmdPaste()
    {
        if (ExplorerShown) _explorer.SendToView("^v");
    }

    void CmdRename()
    {
        if (ExplorerShown) _explorer.RenameSelection();
        else RenameFocused();
    }

    void CmdDelete()
    {
        if (ExplorerShown) { if (!_explorer.InvokeVerbOnSelection("delete")) _explorer.SendToView("{DELETE}"); }
        else DeleteSelected(false);
    }

    /// <summary>The Windows share sheet for the selection (the "Windows.ModernShare" verb Explorer's Share button runs).</summary>
    void CmdShare()
    {
        if (ExplorerShown) _explorer.InvokeVerbOnSelection("Windows.ModernShare");
        else ShellContextMenu.InvokeVerb(this, SelectedPaths(500), "Windows.ModernShare");
    }

    void CloseSearch()
    {
        if (_search.TextLength > 0) _search.Clear();
        if (ExplorerShown) _explorer.FocusView();
    }

    void SetZapThumbnails(bool thumbs)
    {
        _app.Settings.ZapThumbnails = thumbs;
        if (!_zapActive || _zapThumbs != thumbs) ToggleZap(thumbs);
    }

    /// <summary>Search options (Explorer shows this button while searching): where to look, kind of file, matching.</summary>
    void BuildSearchOptionsMenu()
    {
        var items = _cmdSearchOptions.DropDownItems;
        items.Clear();
        bool canScope = _browsing && _explorer.CurrentPath != null;
        string here = _explorer.CurrentDisplayName ?? "this folder";
        items.Add(new ToolStripMenuItem($"Search in {here.Replace("&", "&&")}", null, (_, _) => _scope.Checked = true) { Checked = canScope && _scope.Checked, Enabled = canScope });
        items.Add(new ToolStripMenuItem("Search everywhere", null, (_, _) => _scope.Checked = false) { Checked = !canScope || !_scope.Checked });
        items.Add(new ToolStripSeparator());
        var kind = new ToolStripMenuItem("Kind", MenuIcon("windows.searchfilterkind"));
        for (int i = 0; i < Filters.All.Length; i++)
        {
            int fi = i;
            kind.DropDownItems.Add(new ToolStripMenuItem(Filters.All[i].Name.Replace("&", "&&"), null, (_, _) => SetFilter(fi)) { Checked = i == _filterIndex });
        }
        items.Add(kind);
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Match case", null, (_, _) => Toggle(() => _matchCase = !_matchCase)) { Checked = _matchCase, ShortcutKeyDisplayString = "Ctrl+I" });
        items.Add(new ToolStripMenuItem("Match whole word", null, (_, _) => Toggle(() => _wholeWord = !_wholeWord)) { Checked = _wholeWord, ShortcutKeyDisplayString = "Ctrl+B" });
        items.Add(new ToolStripMenuItem("Match path", null, (_, _) => Toggle(() => _matchPath = !_matchPath)) { Checked = _matchPath, ShortcutKeyDisplayString = "Ctrl+U" });
        items.Add(new ToolStripMenuItem("Match diacritics", null, (_, _) => Toggle(() => _diacritics = !_diacritics)) { Checked = _diacritics, ShortcutKeyDisplayString = "Ctrl+M" });
        items.Add(new ToolStripMenuItem("Regular expressions", null, (_, _) => Toggle(() => _regex = !_regex)) { Checked = _regex, ShortcutKeyDisplayString = "Ctrl+R" });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Search syntax", null, (_, _) => SearchSyntaxForm.ShowSyntax(this)) { ShortcutKeyDisplayString = "F1" });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    static uint s_cfIdList;

    /// <summary>Enables the command bar like Explorer: Cut/Copy/Share/Delete need a selection, Paste needs files on the clipboard.</summary>
    void UpdateCommandStates()
    {
        if (!IsHandleCreated) return;
        bool explorer = ExplorerShown;
        int sel = explorer ? _explorer.SelectedCount : _list.SelectedIndices.Count;
        _cmdCut.Enabled = _cmdCopy.Enabled = _cmdDelete.Enabled = _cmdShare.Enabled = sel > 0;
        _cmdRename.Enabled = explorer ? sel > 0 : sel == 1;
        bool folderOpen = explorer && _explorer.CurrentPath != null;
        _cmdNew.Enabled = folderOpen;
        if (s_cfIdList == 0) s_cfIdList = RegisterClipboardFormat("Shell IDList Array");
        _cmdPaste.Enabled = folderOpen && (IsClipboardFormatAvailable(15 /* CF_HDROP */) || IsClipboardFormatAvailable(s_cfIdList));
        bool searching = _search.TextLength > 0;
        if (_cmdSearchOptions.Visible != searching)
            _sepSearch.Visible = _cmdSearchOptions.Visible = _cmdCloseSearch.Visible = searching;
        _zapButton.Tag = _zapActive;
        _zapButton.Invalidate();
        _cmdPreview.Checked = _app.Settings.PreviewVisible;
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        UpdateCommandStates(); // the clipboard may have changed while we were in the background
    }

    protected override void WndProc(ref Message m)
    {
        if (ShellContextMenu.HandleMenuMessage(ref m)) return; // Explorer's New menu fills its submenu on WM_INITMENUPOPUP
        base.WndProc(ref m);
    }

    /// <summary>An Explorer icon sized for menus (cached per name and DPI).</summary>
    Image? MenuIcon(string svg)
    {
        string key = svg + "@" + DeviceDpi;
        if (!_menuIcons.TryGetValue(key, out var img)) _menuIcons[key] = img = ExplorerIcons.Render(svg, LogicalToDeviceUnits(16), ExplorerTheme.Dark);
        return img;
    }

    readonly Dictionary<string, Image?> _menuIcons = [];

    void BuildSortMenu()
    {
        _cmdSort.DropDownItems.Clear();
        if (ExplorerShown)
        {
            foreach (var (title, column) in new (string, ExplorerSortColumn)[]
                     { ("Name", ExplorerSortColumn.Name), ("Date modified", ExplorerSortColumn.DateModified), ("Type", ExplorerSortColumn.Type), ("Size", ExplorerSortColumn.Size) })
            {
                var c = column;
                _cmdSort.DropDownItems.Add(new ToolStripMenuItem(title, null, (_, _) => _explorer.SortBy(c, descending: false)));
            }
            _cmdSort.DropDownItems.Add(new ToolStripSeparator());
            _cmdSort.DropDownItems.Add(new ToolStripMenuItem("Ascending", null, (_, _) => _explorer.SetSortDirection(descending: false)));
            _cmdSort.DropDownItems.Add(new ToolStripMenuItem("Descending", null, (_, _) => _explorer.SetSortDirection(descending: true)));
            return;
        }
        foreach (var key in Enum.GetValues<SortKey>())
        {
            var k = key;
            _cmdSort.DropDownItems.Add(new ToolStripMenuItem(Columns.SortTitle(k), null, (_, _) => SetSort(k, k == _sort ? _desc : false)) { Checked = k == _sort });
        }
        _cmdSort.DropDownItems.Add(new ToolStripSeparator());
        _cmdSort.DropDownItems.Add(new ToolStripMenuItem("Ascending", null, (_, _) => SetSort(_sort, false)) { Checked = !_desc });
        _cmdSort.DropDownItems.Add(new ToolStripMenuItem("Descending", null, (_, _) => SetSort(_sort, true)) { Checked = _desc });
    }

    void BuildViewMenu()
    {
        _cmdView.DropDownItems.Clear();
        if (ExplorerShown)
        {
            foreach (var (title, mode, size, icon) in new (string, int, int, string)[]
                     {
                         ("Extra large icons", ShellNative.FVM_ICON, 256, ExplorerIcons.IconsExtraLarge), ("Large icons", ShellNative.FVM_ICON, 96, ExplorerIcons.IconsLarge),
                         ("Medium icons", ShellNative.FVM_ICON, 48, ExplorerIcons.IconsMedium), ("Small icons", ShellNative.FVM_SMALLICON, 16, ExplorerIcons.IconsSmall),
                         ("List", ShellNative.FVM_LIST, 16, ExplorerIcons.IconsList), ("Details", ShellNative.FVM_DETAILS, 16, ExplorerIcons.IconsDetails),
                         ("Tiles", ShellNative.FVM_TILE, 48, ExplorerIcons.IconsTiles), ("Content", ShellNative.FVM_CONTENT, 32, ExplorerIcons.IconsContent),
                     })
            {
                int m = mode, sz = size;
                _cmdView.DropDownItems.Add(new ToolStripMenuItem(title, MenuIcon(icon), (_, _) => _explorer.SetViewMode(m, sz)));
            }
        }
        else
        {
            foreach (var (title, size) in new (string, int)[] { ("Extra large thumbnails", 256), ("Large thumbnails", 160), ("Medium thumbnails", 96), ("Details", 0) })
            {
                int sz = size;
                _cmdView.DropDownItems.Add(new ToolStripMenuItem(title, null, (_, _) => SetViewMode(sz)) { Checked = _app.Settings.ThumbnailSize == sz });
            }
        }
        _cmdView.DropDownItems.Add(new ToolStripSeparator());
        _cmdView.DropDownItems.Add(new ToolStripMenuItem("Navigation pane", MenuIcon(ExplorerIcons.NavPane), (_, _) => ToggleNavPane()) { Checked = _app.Settings.ExplorerNavPane });
        _cmdView.DropDownItems.Add(new ToolStripMenuItem("Preview pane", MenuIcon(ExplorerIcons.DetailsPane), (_, _) => TogglePreview()) { Checked = _app.Settings.PreviewVisible });
    }

    void ToggleNavPane()
    {
        _app.Settings.ExplorerNavPane = !_app.Settings.ExplorerNavPane;
        ApplyNavPaneVisibility();
        SyncMenus();
    }

    // ---- what the left side shows ------------------------------------------------------------------

    /// <summary>The Explorer view is shown while browsing with an empty search box and no ZAP.</summary>
    bool ExplorerShown => _browsing && _search.TextLength == 0 && !_zapActive;

    void UpdateLeftView()
    {
        if (!IsHandleCreated) return;
        bool explorer = ExplorerShown;
        if (_explorer.Visible != explorer)
        {
            bool hadFocus = _list.Focused || _grid.Focused || _explorer.ContainsFocus;
            _explorer.Visible = explorer;
            if (explorer) _explorer.BringToFront();
            ApplyViewMode();
            if (hadFocus) { if (explorer) _explorer.FocusView(); else FocusList(); }
        }
        if (explorer) { _list.Visible = false; _grid.Visible = false; }
        _navBack.Enabled = _browsing && _explorer.CanGoBack;
        _navForward.Enabled = _browsing && _explorer.CanGoForward;
        _navUp.Enabled = _browsing;
        UpdateSearchPlaceholder();
        UpdateCommandStates();
        UpdateTitle();
        QueuePreview();
        QueueStatusUpdate();
    }

    void SyncAddress()
    {
        _address.Text = _browsing ? _explorer.CurrentEditingName ?? "" : "";
        _breadcrumb.EditText = _address.Text;
        _breadcrumb.SetLocation(_browsing ? _explorer.CurrentParsingName : null);
    }

    // ---- navigation ---------------------------------------------------------------------------

    /// <summary>Opens a folder (or "shell:"/virtual location) in the Explorer view; optionally selects an item in it.</summary>
    public bool BrowseTo(string location, string? select = null)
    {
        if (string.IsNullOrWhiteSpace(location)) { GoHome(); return true; }
        if (!_explorer.IsHandleCreated) _ = _explorer.Handle; // create the shell view even while hidden
        if (!_explorer.Navigate(location, select))
        {
            _statusRight.Text = $"Can't open \"{location.Trim()}\"";
            return false;
        }
        return true;
    }

    void OnExplorerNavigated()
    {
        bool wasBrowsing = _browsing;
        _browsing = true;
        if (_zapActive) ExitZap(rerun: false);
        if (_search.TextLength > 0 && !wasBrowsing)
        {
            _suppress = true;
            _search.Clear();
            _suppress = false;
        }
        _app.Settings.LastLocation = _explorer.CurrentParsingName ?? "";
        SyncAddress();
        SyncNavTree();
        UpdateLeftView();
        if (_search.TextLength > 0) RunSearch(SearchReason.NewQuery); // re-scope a typed search to the new folder
    }

    void NavigateUp()
    {
        if (!_browsing) return;
        _explorer.GoUp();
    }

    void GoHome()
    {
        if (_zapActive) ExitZap(rerun: false);
        if (_search.TextLength > 0)
        {
            _suppress = true;
            _search.Clear();
            _suppress = false;
        }
        if (!BrowseTo(HomeLocation)) BrowseTo(ThisPcLocation);
    }

    /// <summary>Opens the last folder (or Home) when the window starts, like File Explorer.</summary>
    void OpenStartLocation()
    {
        string last = _app.Settings.LastLocation;
        if (last.Length > 0 && BrowseTo(last)) return;
        GoHome();
    }

    partial void SyncNavTreeCore(string location);

    void SyncNavTree()
    {
        if (_explorer.CurrentParsingName is { } location) SyncNavTreeCore(location);
    }

    void FocusAddress()
    {
        _breadcrumb.EditText = _browsing ? _explorer.CurrentEditingName ?? "" : "";
        _breadcrumb.BeginEdit();
    }

    /// <summary>Folders open inside Rock Preview; files open with their default app.</summary>
    void OpenSelectedOrBrowse()
    {
        var paths = SelectedPaths(1000);
        if (paths.Count == 1 && Directory.Exists(paths[0]))
        {
            BrowseTo(paths[0]);
            return;
        }
        FileOps.Open(this, paths);
    }

    /// <summary>Shows the item in its folder, inside Rock Preview, selected.</summary>
    void BrowseToSelectedItemFolder()
    {
        var paths = SelectedPaths(1);
        if (paths.Count == 0) return;
        string p = paths[0].TrimEnd('\\');
        string? parent = Path.GetDirectoryName(p);
        if (parent == null) { BrowseTo(paths[0]); return; }
        if (_search.TextLength > 0)
        {
            _suppress = true;
            _search.Clear();
            _suppress = false;
        }
        if (_zapActive) ExitZap(rerun: false);
        BrowseTo(parent, select: p);
    }

    // ---- search scope -------------------------------------------------------------------------

    /// <summary>Extra filter ANDed with the query: the ZAP set, or the open folder when searching inside it.</summary>
    string? BrowseFilter()
    {
        if (_zapActive) return ZapFilter();
        if (_browsing && _scope.Checked && _explorer.CurrentPath is { Length: > 0 } folder)
            return Quote(WithSlash(folder));
        return null;
    }

    static string WithSlash(string folder) => folder.EndsWith('\\') ? folder : folder + "\\";
    static string Quote(string s) => "\"" + s + "\"";

    // ---- ZAP -------------------------------------------------------------------------------

    void ToggleZap(bool thumbs)
    {
        if (_zapActive && _zapThumbs == thumbs) { ExitZap(rerun: true); return; }
        if (_zapActive) { _zapThumbs = thumbs; ApplyViewMode(); UpdateLeftView(); RunSearch(SearchReason.NewQuery); return; }

        var roots = new List<string>();
        if (ExplorerShown || (_browsing && _explorer.CurrentPath != null && _search.TextLength == 0))
        {
            if (_explorer.CurrentPath is { } folder) roots.Add(folder);
        }
        else
        {
            foreach (var p in SelectedPaths(64)) if (Directory.Exists(p)) roots.Add(p.TrimEnd('\\').Length == 2 ? p : p.TrimEnd('\\'));
            if (roots.Count == 0 && _browsing && _explorer.CurrentPath is { } folder) roots.Add(folder);
        }
        if (roots.Count == 0)
        {
            MessageBox.Show(this, "Open a folder (or select folders in the results) to ZAP it.\n\nZAP shows every file in the folder at once.",
                "ZAP", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _zapRoots = roots;
        _zapLabel = roots.Count == 1 ? (Path.GetFileName(roots[0].TrimEnd('\\')) is { Length: > 0 } n ? n : roots[0]) : $"{roots.Count} folders";
        _zapActive = true;
        _zapThumbs = thumbs;
        _suppress = true;
        _search.Clear(); // the search box now filters inside the zapped files
        _suppress = false;
        ApplyViewMode();
        UpdateLeftView();
        RunSearch(SearchReason.NewQuery);
        FocusList();
    }

    void ExitZap(bool rerun)
    {
        if (!_zapActive) return;
        _zapActive = false;
        _zapThumbs = false;
        _zapRoots = [];
        if (rerun)
        {
            _suppress = true;
            _search.Clear();
            _suppress = false;
        }
        ApplyViewMode();
        UpdateLeftView();
        if (rerun) RunSearch(SearchReason.NewQuery);
    }

    void ToggleZapDepth()
    {
        _app.Settings.ZapIncludeSubfolders = !_app.Settings.ZapIncludeSubfolders;
        SyncMenus();
        if (_zapActive) RunSearch(SearchReason.NewQuery);
        QueueStatusUpdate();
    }

    /// <summary>Every file under the roots (files only), or only those directly inside them.</summary>
    string ZapFilter()
    {
        bool deep = _app.Settings.ZapIncludeSubfolders;
        var terms = _zapRoots.Select(r =>
        {
            string folder = r.TrimEnd('\\');
            return deep ? Quote(WithSlash(r)) : "parent:" + Quote(folder.Length == 2 ? folder + "\\" : folder);
        }).ToList();
        string scope = terms.Count == 1 ? terms[0] : "<" + string.Join(" | ", terms) + ">";
        return "file: " + scope;
    }

    string ZapStatus()
    {
        string where = _app.Settings.ZapIncludeSubfolders ? "including subfolders" : "this folder only";
        return $"⚡ ZAP · {_ids.Length:N0} files in {_zapLabel} ({where})";
    }
}

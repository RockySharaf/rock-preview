using Anything.Core;
using Anything.Interop;

namespace Anything;

internal sealed partial class MainForm
{
    ToolStripMenuItem _miMatchCase = null!, _miWholeWord = null!, _miMatchPath = null!, _miDiacritics = null!, _miRegex = null!;
    ToolStripMenuItem _miFiltersBar = null!, _miStatusBar = null!, _miOnTop = null!, _miRunAsAdmin = null!, _miBookmarks = null!;
    ToolStripMenuItem _miPreview = null!;
    ToolStripMenuItem _miNavPane = null!, _miZap = null!, _miZapThumbs = null!, _miZapDeep = null!;
    readonly List<(ToolStripMenuItem Item, string Theme)> _miThemes = [];
    readonly List<(ToolStripMenuItem Item, int Size)> _miViewModes = [];
    ToolStripMenuItem _miAscending = null!, _miDescending = null!;
    readonly List<ToolStripMenuItem> _miFilterItems = [];
    readonly List<(ToolStripMenuItem Item, SortKey Key)> _miSortItems = [];
    int _bookmarkFixedItems;

    static ToolStripMenuItem Item(string text, Action onClick, Keys keys = Keys.None, string? display = null)
    {
        var mi = new ToolStripMenuItem(text);
        mi.Click += (_, _) => onClick();
        if (keys != Keys.None) mi.ShortcutKeys = keys;
        if (display is not null) mi.ShortcutKeyDisplayString = display;
        return mi;
    }

    void BuildMenu()
    {
        // File
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.AddRange(
        [
            Item("&New Search Window", () => _app.NewWindow(this), Keys.Control | Keys.N),
            new ToolStripSeparator(),
            Item("&Open", OpenSelected, display: "Enter"),
            Item("Open &Path", OpenSelectedPath, display: "Ctrl+Enter"),
            new ToolStripSeparator(),
            Item("&Export...", Export, Keys.Control | Keys.S),
            new ToolStripSeparator(),
            Item("&Close", Close, Keys.Control | Keys.W),
            Item("E&xit", _app.ExitApp, Keys.Control | Keys.Q),
        ]);

        // Edit
        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.AddRange(
        [
            Item("Cu&t", () => { if (_search.Focused) _search.Cut(); else CopyFiles(true); }, display: "Ctrl+X"),
            Item("&Copy", () => { if (_search.Focused) _search.Copy(); else CopyFiles(false); }, display: "Ctrl+C"),
            Item("&Paste", PasteIntoSearch, display: "Ctrl+V"),
            new ToolStripSeparator(),
            Item("Copy &Full Name to Clipboard", CopyFullName, Keys.Control | Keys.Shift | Keys.C),
            Item("Copy Pat&h to Clipboard", CopyPath),
            new ToolStripSeparator(),
            Item("Select &All", () => { if (_search.Focused) _search.SelectAll(); else SelectAll(); }, display: "Ctrl+A"),
            Item("&Invert Selection", InvertSelection),
        ]);

        // View
        var view = new ToolStripMenuItem("&View");
        _miFiltersBar = Item("&Filters", () => { _app.Settings.FiltersVisible = !_app.Settings.FiltersVisible; ApplyBarsEverywhere(); });
        _miStatusBar = Item("Status &Bar", () => { _app.Settings.StatusBarVisible = !_app.Settings.StatusBarVisible; ApplyBarsEverywhere(); });
        var sortBy = new ToolStripMenuItem("S&ort by");
        int n = 1;
        foreach (var key in Enum.GetValues<SortKey>())
        {
            var k = key;
            var mi = Item("&" + Columns.SortTitle(k), () => SetSort(k, k == _sort ? _desc : false), n <= 9 ? Keys.Control | (Keys.D0 + n) : Keys.None);
            n++;
            _miSortItems.Add((mi, k));
            sortBy.DropDownItems.Add(mi);
        }
        sortBy.DropDownItems.Add(new ToolStripSeparator());
        _miAscending = Item("&Ascending", () => SetSort(_sort, false));
        _miDescending = Item("&Descending", () => SetSort(_sort, true));
        sortBy.DropDownItems.AddRange([_miAscending, _miDescending]);
        var zoom = new ToolStripMenuItem("&Zoom");
        zoom.DropDownItems.AddRange(
        [
            Item("Zoom &In", () => Zoom(1), display: "Ctrl++"),
            Item("Zoom &Out", () => Zoom(-1), display: "Ctrl+-"),
            Item("&Reset", () => Zoom(0), display: "Ctrl+0"),
        ]);
        _miOnTop = Item("Always on &Top", () => { TopMost = !TopMost; _app.Settings.AlwaysOnTop = TopMost; SyncMenus(); });
        var theme = new ToolStripMenuItem("T&heme");
        _miThemes.Clear();
        foreach (var (title, value) in new[] { ("&Light", "light"), ("&Dark", "dark"), ("&Follow Windows", "system") })
        {
            string v = value;
            var mi = Item(title, () => SetTheme(v));
            _miThemes.Add((mi, v));
            theme.DropDownItems.Add(mi);
        }
        _miPreview = Item("&Preview", TogglePreview, Keys.Alt | Keys.P, "Alt+P");
        _miViewModes.Clear();
        foreach (var (title, size, key) in new (string, int, Keys)[]
        {
            ("E&xtra Large Thumbnails", 256, Keys.D1), ("&Large Thumbnails", 160, Keys.D2),
            ("&Medium Thumbnails", 96, Keys.D3), ("&Details", 0, Keys.D6),
        })
        {
            int s = size;
            _miViewModes.Add((Item(title, () => SetViewMode(s), Keys.Control | Keys.Shift | key), s));
        }
        view.DropDownItems.AddRange(
        [
            .. _miViewModes.Select(v => (ToolStripItem)v.Item), new ToolStripSeparator(),
            _miPreview, _miFiltersBar, _miStatusBar, theme, new ToolStripSeparator(),
            sortBy, zoom, new ToolStripSeparator(),
            Item("&Refresh", RefreshResults, Keys.F5), new ToolStripSeparator(),
            _miOnTop,
        ]);

        // Browse
        var browse = new ToolStripMenuItem("B&rowse");
        _miNavPane = Item("&Navigation Pane", ToggleNavPane);
        _miZapThumbs = Item("&Thumbnails", ToggleThumbnails, Keys.Control | Keys.Shift | Keys.X);
        _miZap = Item("Every File Here as a &List", () => ToggleZap(thumbs: false), Keys.Control | Keys.Shift | Keys.Z);
        _miZapDeep = Item("&Include Subfolders", ToggleZapDepth);
        browse.DropDownItems.AddRange(
        [
            Item("&Back", GoBack, Keys.Alt | Keys.Left, "Alt+Left"),
            Item("&Forward", GoForward, Keys.Alt | Keys.Right, "Alt+Right"),
            Item("&Up", NavigateUp, Keys.Alt | Keys.Up, "Alt+Up"),
            Item("&Home", GoHome, Keys.Alt | Keys.Home, "Alt+Home"),
            Item("&New Folder", CmdNewFolder, Keys.Control | Keys.Shift | Keys.N),
            Item("&Last Folder", () => OpenFolder(_app.Settings.LastLocation is { Length: > 0 } l ? l : ThisPcLocation)),
            Item("This &PC", () => OpenFolder(ThisPcLocation)),
            Item("&Address Bar", FocusAddress, Keys.Control | Keys.L, "Ctrl+L"),
            new ToolStripSeparator(),
            _miZapThumbs, _miZap, _miZapDeep,
            new ToolStripSeparator(),
            _miNavPane,
        ]);

        // Search
        var search = new ToolStripMenuItem("&Search");
        _miMatchCase = Item("Match &Case", () => Toggle(() => _matchCase = !_matchCase), Keys.Control | Keys.I);
        _miWholeWord = Item("Match &Whole Word", () => Toggle(() => _wholeWord = !_wholeWord), Keys.Control | Keys.B);
        _miMatchPath = Item("Match &Path", () => Toggle(() => _matchPath = !_matchPath), Keys.Control | Keys.U);
        _miDiacritics = Item("Match &Diacritics", () => Toggle(() => _diacritics = !_diacritics), Keys.Control | Keys.M);
        _miRegex = Item("Enable &Regex", () => Toggle(() => _regex = !_regex), Keys.Control | Keys.R);
        search.DropDownItems.AddRange([_miMatchCase, _miWholeWord, _miMatchPath, _miDiacritics, _miRegex, new ToolStripSeparator()]);
        for (int i = 0; i < Filters.All.Length; i++)
        {
            int fi = i;
            var mi = Item(Filters.All[i].Name.Replace("&", "&&"), () => SetFilter(fi));
            _miFilterItems.Add(mi);
            search.DropDownItems.Add(mi);
        }

        // Bookmarks
        _miBookmarks = new ToolStripMenuItem("&Bookmarks");
        _miBookmarks.DropDownItems.AddRange(
        [
            Item("&Add to Bookmarks...", AddBookmark, Keys.Control | Keys.D),
            Item("&Organize Bookmarks...", OrganizeBookmarks, Keys.Control | Keys.Shift | Keys.B),
        ]);
        _bookmarkFixedItems = _miBookmarks.DropDownItems.Count;
        _miBookmarks.DropDownOpening += (_, _) => RebuildBookmarksMenu();
        RebuildBookmarksMenu();

        // Tools
        var tools = new ToolStripMenuItem("&Tools");
        _miRunAsAdmin = Item("Run as &Administrator...", _app.RestartElevated);
        tools.DropDownItems.AddRange(
        [
            Item("&Options...", () => _app.ShowOptions(this), Keys.Control | Keys.P),
            new ToolStripSeparator(),
            Item("Re&build Index", () =>
            {
                if (MessageBox.Show(this, "Discard the index and rescan everything now?", "Rebuild Index", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                    _app.RebuildIndex();
            }),
            _miRunAsAdmin,
        ]);

        // Help
        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.AddRange(
        [
            Item("&Search Syntax", () => SearchSyntaxForm.ShowSyntax(this), Keys.F1),
            new ToolStripSeparator(),
            Item("Check for &Updates...", CheckForUpdates),
            Item("&About Rock Preview...", ShowAbout),
        ]);

        // Explorer has no menu bar: its "See more" (...) menu holds the selection commands, Properties and Options;
        // Rock Preview's full menus follow underneath.
        var selectAll = Item("Select all", SelectAllAny, display: "Ctrl+A");
        var selectNone = Item("Select none", SelectNoneAny);
        var invert = Item("Invert selection", InvertSelectionAny);
        var properties = Item("Properties", PropertiesAny, display: "Alt+Enter");
        var options = Item("Options", () => _app.ShowOptions(this), display: "Ctrl+P");
        // The way to the real File Explorer (useful when Rock Preview replaces it): this location, in a File Explorer window.
        var inExplorer = Item("Open in File Explorer", OpenInFileExplorer);
        selectAll.Image = MenuIcon(ExplorerIcons.SelectAll);
        selectNone.Image = MenuIcon(ExplorerIcons.SelectNone);
        invert.Image = MenuIcon(ExplorerIcons.InvertSelection);
        properties.Image = MenuIcon(ExplorerIcons.Properties);
        options.Image = MenuIcon(ExplorerIcons.Options);
        inExplorer.Image = MenuIcon(ExplorerIcons.OpenNewWindow);
        _miFiltersBar.Visible = false; // the filter now lives in Search options
        _cmdMore.DropDownItems.AddRange([selectAll, selectNone, invert, new ToolStripSeparator(), properties, inExplorer, options, new ToolStripSeparator(),
                                         file, edit, view, browse, search, _miBookmarks, tools, help]);
        SyncMenus();
    }

    void RebuildBookmarksMenu()
    {
        var items = _miBookmarks.DropDownItems;
        while (items.Count > _bookmarkFixedItems) items.RemoveAt(items.Count - 1);
        var bookmarks = _app.Settings.Bookmarks;
        if (bookmarks.Count == 0) return;
        items.Add(new ToolStripSeparator());
        foreach (var b in bookmarks)
        {
            var bm = b;
            var mi = Item(b.Name.Replace("&", "&&"), () => ApplyBookmark(bm));
            mi.ToolTipText = b.Query;
            items.Add(mi);
        }
    }

    void SyncMenus()
    {
        if (_miMatchCase is null) return;
        _miMatchCase.Checked = _matchCase;
        _miWholeWord.Checked = _wholeWord;
        _miMatchPath.Checked = _matchPath;
        _miDiacritics.Checked = _diacritics;
        _miRegex.Checked = _regex;
        for (int i = 0; i < _miFilterItems.Count; i++) _miFilterItems[i].Checked = i == _filterIndex;
        foreach (var (item, key) in _miSortItems) item.Checked = key == _sort;
        _miAscending.Checked = !_desc;
        _miDescending.Checked = _desc;
        _miFiltersBar.Checked = _app.Settings.FiltersVisible;
        _miStatusBar.Checked = _app.Settings.StatusBarVisible;
        _miOnTop.Checked = TopMost;
        _miPreview.Checked = _app.Settings.PreviewVisible;
        _miNavPane.Checked = _app.Settings.ExplorerNavPane;
        _miZap.Checked = _zapActive && !_zapThumbs;
        _miZapThumbs.Checked = !ExplorerShown && ThumbnailMode;
        _miZapDeep.Checked = _app.Settings.ZapIncludeSubfolders;
        foreach (var (item, t) in _miThemes) item.Checked = t == _app.Settings.Theme;
        int thumb = _app.Settings.ThumbnailSize;
        foreach (var (item, size) in _miViewModes) item.Checked = size == thumb || (size == 160 && thumb > 0 && !_miViewModes.Any(v => v.Size == thumb));
        if (_filter.Items.Count > _filterIndex && _filter.SelectedIndex != _filterIndex)
        {
            bool old = _suppress;
            _suppress = true;
            _filter.SelectedIndex = _filterIndex;
            _suppress = old;
        }
    }

    void SetTheme(string theme)
    {
        if (theme == _app.Settings.Theme) return;
        _app.Settings.Theme = theme;
        _app.Settings.Save();
        SyncMenus();
        string name = theme switch { "dark" => "dark", "system" => "Windows", _ => "light" };
        if (MessageBox.Show(this, $"Restart Rock Preview now to switch to the {name} theme?", "Theme",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            _app.Restart();
    }

    void Toggle(Action change)
    {
        change();
        SyncMenus();
        RunSearch(SearchReason.NewQuery);
        UpdateStatus();
    }

    void SetFilter(int index)
    {
        if (index < 0 || index >= Filters.All.Length) return;
        _filterIndex = index;
        SyncMenus();
        RunSearch(SearchReason.NewQuery);
        UpdateStatus();
    }

    void ApplyBarsEverywhere()
    {
        foreach (var w in _app.Windows) w.ApplyUiSettings();
        _app.Settings.Save();
    }

    void Zoom(int delta)
    {
        var s = _app.Settings;
        s.Zoom = delta == 0 ? 0 : Math.Clamp(s.Zoom + delta, -6, 20);
        foreach (var w in _app.Windows) w.ApplyFont();
    }

    void RefreshResults()
    {
        if (ExplorerShown) { _explorer.RefreshView(); return; }
        _rows.Clear();
        _list.Invalidate();
        RunSearch(SearchReason.Refresh);
    }

    // ---- keyboard -----------------------------------------------------------------------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                if (_filter.DroppedDown) break;
                HandleEscape();
                return true;
            case Keys.Alt | Keys.Enter:
                PropertiesAny();
                return true;
            case Keys.Alt | Keys.D:
            case Keys.F4:
                FocusAddress(); // Explorer's address bar keys (Ctrl+L is on the Browse menu)
                return true;
            case Keys.Control | Keys.E:
            case Keys.Control | Keys.F:
                FocusSearch(true);
                return true;
            case Keys.Control | Keys.Oemplus:
            case Keys.Control | Keys.Add:
                Zoom(1);
                return true;
            case Keys.Control | Keys.OemMinus:
            case Keys.Control | Keys.Subtract:
                Zoom(-1);
                return true;
            case Keys.Control | Keys.D0:
            case Keys.Control | Keys.NumPad0:
                Zoom(0);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void HandleEscape()
    {
        if (_zapActive && _search.TextLength == 0)
        {
            ExitZap(rerun: true);
            if (ExplorerShown) _explorer.FocusView();
        }
        else if (_search.TextLength > 0)
        {
            _search.Clear();
            FocusSearch(false);
        }
        else Close();
    }

    void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyData)
        {
            case Keys.Down:
            case Keys.PageDown:
            case Keys.Enter:
                FocusList();
                break;
            case Keys.Control | Keys.Enter:
                FocusList();
                OpenSelectedPath();
                break;
            case Keys.Control | Keys.Back:
                DeletePreviousWord();
                break;
            default:
                return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    void DeletePreviousWord()
    {
        int caret = _search.SelectionStart;
        if (_search.SelectionLength > 0) { _search.SelectedText = ""; return; }
        string t = _search.Text;
        int i = caret;
        while (i > 0 && char.IsWhiteSpace(t[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(t[i - 1])) i--;
        _search.Text = t[..i] + t[caret..];
        _search.SelectionStart = i;
    }

    void FocusList()
    {
        if (ExplorerShown) { _explorer.FocusView(); return; }
        if (ThumbnailMode) _grid.Focus();
        else _list.Focus();
        if (_ids.Length == 0) return;
        if (_list.SelectedIndices.Count == 0)
        {
            _list.SetSelected(0, true);
            _list.Items[0].Focused = true;
            _list.EnsureVisible(0);
        }
    }

    void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyData)
        {
            case Keys.Enter: OpenSelected(); break;
            case Keys.Control | Keys.Enter: OpenSelectedPath(); break;
            case Keys.Delete: DeleteSelected(false); break;
            case Keys.Shift | Keys.Delete: DeleteSelected(true); break;
            case Keys.F2: RenameFocused(); break;
            case Keys.Control | Keys.A: SelectAll(); break;
            case Keys.Control | Keys.C:
            case Keys.Control | Keys.Insert: CopyFiles(false); break;
            case Keys.Control | Keys.X: CopyFiles(true); break;
            case Keys.Control | Keys.V:
            case Keys.Shift | Keys.Insert: PasteIntoSearch(); break;
            case Keys.Back:
                _search.Focus();
                if (_search.TextLength > 0)
                {
                    _search.Text = _search.Text[..^1];
                    _search.SelectionStart = _search.TextLength;
                }
                break;
            case Keys.Up when FocusedIndex() <= 0 && _ids.Length > 0 && _list.SelectedIndices.Count <= 1:
                FocusSearch(true);
                break;
            default:
                return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    void OnListKeyPress(object? sender, KeyPressEventArgs e)
    {
        // Typing in the result list goes to the search box.
        if (char.IsControl(e.KeyChar)) return;
        _search.Focus();
        _search.SelectionStart = _search.TextLength;
        _search.SelectionLength = 0;
        _search.SelectedText = e.KeyChar.ToString();
        e.Handled = true;
    }

    // ---- item actions -------------------------------------------------------------------------

    List<string> SelectedPaths(int max)
    {
        var index = _app.IndexManager.Index;
        var list = new List<string>();
        if (!ResultsCurrent) return list; // ids were renumbered by a compaction; a refreshed search is on its way
        foreach (int i in _list.GetSelectedIndices(max))
        {
            if (i >= _ids.Length) continue;
            try
            {
                int id = _ids[i];
                if (index.IsAlive(id)) list.Add(index.GetFullPath(id));
            }
            catch (IndexOutOfRangeException) { }
        }
        return list;
    }

    void OpenSelected() => OpenSelectedOrBrowse();
    void OpenSelectedPath() => BrowseToSelectedItemFolder();
    void ShowInWindowsExplorer() => FileOps.OpenPath(this, SelectedPaths(64));
    void Properties() => FileOps.Properties(this, SelectedPaths(8));

    // "See more" commands: act on the Explorer view when it is showing, otherwise on the results.
    void SelectAllAny() { if (ExplorerShown) _explorer.SendToView("^a"); else SelectAll(); }

    void SelectNoneAny()
    {
        if (ExplorerShown) { _explorer.SelectNone(); return; }
        _list.SetAllSelected(false);
        if (ThumbnailMode) _grid.Invalidate();
        QueueStatusUpdate();
        QueuePreview();
    }

    void InvertSelectionAny() { if (ExplorerShown) _explorer.InvertSelection(); else InvertSelection(); }

    void PropertiesAny()
    {
        if (!ExplorerShown) { Properties(); return; }
        if (_explorer.SelectedCount > 0) _explorer.InvokeVerbOnSelection("properties");
        else if (_explorer.CurrentPath is { } folder) FileOps.Properties(this, [folder]);
    }

    void CopyFullName()
    {
        var paths = SelectedPaths(1_000_000);
        if (paths.Count > 0) FileOps.SetClipboardText(string.Join("\r\n", paths));
    }

    void CopyPath()
    {
        var paths = SelectedPaths(1_000_000).Select(p => Path.GetDirectoryName(p.TrimEnd('\\')) ?? p);
        FileOps.SetClipboardText(string.Join("\r\n", paths));
    }

    void CopyFiles(bool cut) => FileOps.SetClipboardFiles(SelectedPaths(100_000), cut);

    void PasteIntoSearch()
    {
        string? text = null;
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
        if (string.IsNullOrEmpty(text)) return;
        text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        _search.Focus();
        _search.SelectedText = text;
    }

    void SelectAll()
    {
        if (_ids.Length == 0) return;
        _list.SetAllSelected(true);
        QueueStatusUpdate();
    }

    void InvertSelection()
    {
        if (_ids.Length == 0) return;
        var selected = new HashSet<int>(_list.GetSelectedIndices());
        _list.BeginUpdate();
        try
        {
            _list.SetAllSelected(false);
            for (int i = 0; i < _ids.Length; i++)
                if (!selected.Contains(i)) _list.SetSelected(i, true);
        }
        finally { _list.EndUpdate(); }
        QueueStatusUpdate();
    }

    void DeleteSelected(bool permanent)
    {
        var paths = SelectedPaths(100_000);
        if (paths.Count == 0) return;
        if (paths.Any(p => p.Length <= 3)) return; // never offer to delete a drive root
        FileOps.Delete(this, paths, permanent);
    }

    void RenameFocused()
    {
        int i = FocusedIndex();
        if (i < 0 || !_list.SelectedIndices.Contains(i))
        {
            var sel = _list.GetSelectedIndices(1);
            i = sel.Length > 0 ? sel[0] : -1;
        }
        var row = RowAt(i);
        if (row is null || row.IsRoot || row.FullPath.Length == 0) return;
        string name = row.Name;
        int selLen = row.IsDir ? name.Length : name.LastIndexOf('.') is > 0 and var dot ? dot : name.Length;
        string? newName = InputDialog.Show(this, "Rename", "New name:", name, selLen);
        if (newName is null || newName == name) return;
        FileOps.Rename(this, row.FullPath, newName.Trim());
    }

    // ---- context menus --------------------------------------------------------------------------

    void ShowItemMenu(Point screen)
    {
        if (screen.X == -1 && screen.Y == -1)
        {
            int fi = FocusedIndex();
            if (fi < 0) { var s = _list.GetSelectedIndices(1); fi = s.Length > 0 ? s[0] : -1; }
            if (fi >= 0)
            {
                var r = _list.GetItemRect(fi, ItemBoundsPortion.Label);
                screen = _list.PointToScreen(new Point(Math.Max(0, r.Left) + 16, r.Bottom));
            }
            else screen = _list.PointToScreen(new Point(16, 16));
        }
        else
        {
            var hit = _list.HitTest(_list.PointToClient(screen));
            if (hit.Item is null) { ShowBackgroundMenu(screen); return; }
        }
        ShowSelectionMenu(screen, _list);
    }

    /// <summary>Explorer context menu for the current selection (shared by the details list and the thumbnail grid).</summary>
    void ShowSelectionMenu(Point screen, IWin32Window owner)
    {
        var paths = SelectedPaths(2000);
        if (paths.Count == 0) { ShowBackgroundMenu(screen); return; }

        var extras = new List<ExtraMenuItem>
        {
            new("Open &Path", OpenSelectedPath),
            new("Show in &Windows Explorer", ShowInWindowsExplorer),
            new("Copy &Full Name to Clipboard", CopyFullName),
            new("Copy Pat&h to Clipboard", CopyPath),
        };
        bool shown;
        try { shown = ShellContextMenu.Show(owner, paths, screen, extras, RenameFocused); }
        catch { shown = false; }
        if (!shown) ShowFallbackMenu(screen);
    }

    void ShowFallbackMenu(Point screen)
    {
        var m = new ContextMenuStrip();
        var open = Item("&Open", OpenSelected);
        open.Font = new Font(open.Font, FontStyle.Bold);
        m.Items.AddRange(
        [
            open,
            Item("Open &Path", OpenSelectedPath),
            new ToolStripSeparator(),
            Item("Copy &Full Name to Clipboard", CopyFullName),
            Item("Copy Pat&h to Clipboard", CopyPath),
            new ToolStripSeparator(),
            Item("Cu&t", () => CopyFiles(true)),
            Item("&Copy", () => CopyFiles(false)),
            new ToolStripSeparator(),
            Item("&Delete", () => DeleteSelected(false)),
            Item("Rena&me", RenameFocused),
            new ToolStripSeparator(),
            Item("P&roperties", Properties),
        ]);
        ShowTransient(m, screen);
    }

    void ShowBackgroundMenu(Point screen)
    {
        var m = new ContextMenuStrip();
        var sortBy = new ToolStripMenuItem("S&ort by");
        foreach (var key in Enum.GetValues<SortKey>())
        {
            var k = key;
            sortBy.DropDownItems.Add(new ToolStripMenuItem(Columns.SortTitle(k), null, (_, _) => SetSort(k, k == _sort ? _desc : false)) { Checked = k == _sort });
        }
        sortBy.DropDownItems.Add(new ToolStripSeparator());
        sortBy.DropDownItems.Add(new ToolStripMenuItem("&Ascending", null, (_, _) => SetSort(_sort, false)) { Checked = !_desc });
        sortBy.DropDownItems.Add(new ToolStripMenuItem("&Descending", null, (_, _) => SetSort(_sort, true)) { Checked = _desc });
        m.Items.AddRange([sortBy, new ToolStripSeparator(), Item("&Refresh", RefreshResults), Item("Select &All", SelectAll)]);
        ShowTransient(m, screen);
    }

    void ShowHeaderMenu(Point screen)
    {
        if (screen.X == -1 && screen.Y == -1) screen = _list.PointToScreen(Point.Empty);
        var m = new ContextMenuStrip();
        foreach (var c in Columns.All)
        {
            if (c.Key == "name") continue;
            string key = c.Key;
            m.Items.Add(new ToolStripMenuItem(c.Title, null, (_, _) => ToggleColumn(key)) { Checked = _cols[key].Visible });
        }
        ShowTransient(m, screen);
    }

    void ShowStatusMenu(Point screen)
    {
        var m = new ContextMenuStrip();
        m.Items.AddRange(
        [
            new ToolStripMenuItem("Match &Case", null, (_, _) => Toggle(() => _matchCase = !_matchCase)) { Checked = _matchCase },
            new ToolStripMenuItem("Match &Whole Word", null, (_, _) => Toggle(() => _wholeWord = !_wholeWord)) { Checked = _wholeWord },
            new ToolStripMenuItem("Match &Path", null, (_, _) => Toggle(() => _matchPath = !_matchPath)) { Checked = _matchPath },
            new ToolStripMenuItem("Match &Diacritics", null, (_, _) => Toggle(() => _diacritics = !_diacritics)) { Checked = _diacritics },
            new ToolStripMenuItem("Enable &Regex", null, (_, _) => Toggle(() => _regex = !_regex)) { Checked = _regex },
            new ToolStripSeparator(),
        ]);
        for (int i = 0; i < Filters.All.Length; i++)
        {
            int fi = i;
            m.Items.Add(new ToolStripMenuItem(Filters.All[i].Name, null, (_, _) => SetFilter(fi)) { Checked = i == _filterIndex });
        }
        ShowTransient(m, screen);
    }

    static void ShowTransient(ContextMenuStrip m, Point screen)
    {
        m.Closed += (_, _) => m.BeginInvoke(m.Dispose);
        m.Show(screen);
    }

    // ---- export / bookmarks / about -----------------------------------------------------------

    void Export()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Export Result List",
            Filter = "Everything File List (*.efu)|*.efu|CSV (Comma delimited) (*.csv)|*.csv|Text (*.txt)|*.txt",
            FilterIndex = Math.Clamp(_app.Settings.LastExportType, 0, 2) + 1,
            FileName = "Untitled",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int type = dlg.FilterIndex - 1;
        _app.Settings.LastExportType = type;
        var columns = _list.Columns.Cast<ColumnHeader>().OrderBy(c => c.DisplayIndex).Select(c => (ColumnDef)c.Tag!).ToList();
        var ids = _ids;
        Cursor = Cursors.WaitCursor;
        try
        {
            ResultExporter.Export(dlg.FileName, (ExportFormat)type, _app.IndexManager.Index, ids, columns);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to export.\n\n{ex.Message}", "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Cursor = Cursors.Default; }
    }

    void AddBookmark()
    {
        string def = _search.TextLength > 0 ? _search.Text : "New Bookmark";
        string? name = InputDialog.Show(this, "Add to Bookmarks", "Name:", def, def.Length);
        if (string.IsNullOrWhiteSpace(name)) return;
        _app.Settings.Bookmarks.Add(new Bookmark
        {
            Name = name.Trim(),
            Query = _search.Text,
            Filter = _filterIndex == 0 ? "" : Filters.All[_filterIndex].Name,
            Sort = _sort,
            Descending = _desc,
            MatchCase = _matchCase,
            MatchWholeWord = _wholeWord,
            MatchPath = _matchPath,
            MatchDiacritics = _diacritics,
            Regex = _regex,
        });
        _app.BookmarksChanged();
    }

    void OrganizeBookmarks()
    {
        using var f = new BookmarksForm(_app.Settings.Bookmarks);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        _app.Settings.Bookmarks.Clear();
        _app.Settings.Bookmarks.AddRange(f.Result);
        _app.BookmarksChanged();
    }

    void ApplyBookmark(Bookmark b)
    {
        (_matchCase, _wholeWord, _matchPath, _diacritics, _regex) = (b.MatchCase, b.MatchWholeWord, b.MatchPath, b.MatchDiacritics, b.Regex);
        _filterIndex = Filters.IndexOf(b.Filter);
        _sort = b.Sort;
        _desc = b.Descending;
        _suppress = true;
        try { SetSearch(b.Query); }
        finally { _suppress = false; }
        SyncMenus();
        UpdateSortIndicator();
        RunSearch(SearchReason.NewQuery);
        UpdateStatus();
        FocusSearch(false);
    }

    /// <summary>Help &gt; Check for Updates: asks GitHub now and says what it found.</summary>
    async void CheckForUpdates()
    {
        var current = AnythingApp.CurrentVersion;
        Version? latest;
        UseWaitCursor = true;
        try { latest = await _app.CheckForUpdatesNow(); }
        catch (Exception)
        {
            if (IsDisposed) return;
            TaskDialog.ShowDialog(this, new TaskDialogPage
            {
                Caption = "Rock Preview",
                Heading = "Couldn't check for a new version",
                Text = "Rock Preview couldn't reach GitHub. Check your internet connection and try again.",
                Icon = TaskDialogIcon.Warning,
            });
            return;
        }
        finally { if (!IsDisposed) UseWaitCursor = false; }
        if (IsDisposed) return;
        if (UpdateCheck.IsNewer(latest, current))
        {
            var get = new TaskDialogButton("See what's new and get it");
            var page = new TaskDialogPage
            {
                Caption = "Rock Preview",
                Heading = $"Rock Preview {latest!.ToString(3)} is available",
                Text = $"You have {current.ToString(3)}. Its page on GitHub has what's new and the installer.",
                Icon = TaskDialogIcon.Information,
                Buttons = { get, new TaskDialogButton("Not now") },
                DefaultButton = get,
            };
            if (TaskDialog.ShowDialog(this, page) == get) _app.OpenReleasePage();
        }
        else
        {
            TaskDialog.ShowDialog(this, new TaskDialogPage
            {
                Caption = "Rock Preview",
                Heading = "Rock Preview is up to date",
                Text = $"You have the latest version, {current.ToString(3)}.",
                Icon = TaskDialogIcon.Information,
            });
        }
    }

    void ShowAbout()
    {
        var v = typeof(MainForm).Assembly.GetName().Version;
        MessageBox.Show(this,
            $"Rock Preview {v?.ToString(3)}\n\nInstant file name search for Windows, in the spirit of voidtools Everything.\n\n" +
            $"Index: {_app.IndexManager.Index.LiveCount:N0} objects\n.NET {Environment.Version}" + (_app.IsDemo ? "\n\nRunning with the in-memory demo index." : ""),
            "About Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

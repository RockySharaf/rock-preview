using Anything.Core;
using Anything.Core.Search;
using Anything.Interop;
using Anything.Explorer;

namespace Anything;

/// <summary>
/// Browsing: the real Windows Explorer view (ExplorerBrowser) merged with Rock Preview's instant search.
/// Empty search box + a folder open = Explorer; typing = instant results, inside the folders selected in the view
/// (or everywhere when none are); Thumbnails on a folder = every file under it, from the index, or from the disk when
/// the index doesn't have the folder (internally "ZAP"; see MainForm.Listing.cs).
/// </summary>
internal sealed partial class MainForm
{
    readonly ExplorerToolStrip _commands = new(ExplorerBarKind.Command) { BottomDivider = true }; // Explorer's command bar
    readonly ExplorerToolStrip _nav = new(ExplorerBarKind.Navigation); // back / forward / up / refresh
    readonly ToolStripButton _navBack = new(), _navForward = new(), _navUp = new(), _navRefresh = new();
    readonly TextBox _address = new();         // the address bar's edit mode
    BreadcrumbBar _breadcrumb = null!;
    readonly ToolStripDropDownButton _cmdNew = new("New"), _cmdSort = new("Sort"), _cmdView = new("View"),
                                     _cmdSearchOptions = new("Search options");
    readonly ToolStripSplitButton _cmdMore = new(); // a gear: Settings; its arrow, the rest (Explorer's "See more")
    readonly ExplorerSplitPart _cmdClearKind = new(); // Search options' other half while a kind is chosen: every kind again
    (int Filter, int Dpi) _searchOptionsLook = (-1, 0); // what Search options' icon was drawn for
    readonly ToolStripButton _cmdCut = new(), _cmdCopy = new(), _cmdPaste = new(), _cmdRename = new(), _cmdShare = new(),
                             _cmdDelete = new(), _cmdCloseSearch = new("Close search"), _cmdPreview = new("Preview");
    readonly ToolStripButton _cmdExtract = new("Extract all"); // shown for an archive, as in Explorer: Windows' own Extract all
    readonly ToolStripSplitButton _thumbsButton = new("Thumbnails");
    readonly ToolStripMenuItem _zapThumbItem = new(), _zapListItem = new(), _zapDeepItem = new("Include subfolders"),
                               _zapEverydayItem = new("Everyday files only"), _zapEverydayChooseItem = new("Choose everyday files...");
    readonly ToolStripSplitButton _cmdEveryday = new("Everyday files"); // shown with Thumbnails' files: hides code and system files; its arrow picks the kinds
    bool _keepEverydayMenu; // a kind was ticked: the menu stays open for the next
    readonly ToolStripSeparator _sepSearch = new();
    readonly SplitContainer _body = new();     // navigation pane | files + preview
    readonly Panel _navPanel = new();
    readonly List<(ToolStripItem Item, string? Svg, char Glyph)> _glyphItems = [];
    readonly DragPanel _addressGap = new() { Dock = DockStyle.Right };
    SearchField _searchField = null!;

    // Per tab (see MainForm.Tabs.cs): _browsing (a location is open in the Explorer view), _zapActive and _zapThumbs
    // ("every file in" and how it shows), _zapRoots, _zapLabel, _selScope (folders that were selected when the search
    // started) and _useSelScope (the search looks only inside them).
    int _lastThumbSize = 160;       // the thumbnail size the Thumbnails button goes back to

    /// <summary>Windows 11 "Home" (falls back to This PC where it doesn't exist).</summary>
    const string HomeLocation = "shell:::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
    const string ThisPcLocation = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    // ---- layout ---------------------------------------------------------------------------------

    void BuildBrowseBar()
    {
        // Command bar, like Windows 11 File Explorer's: New | Cut Copy Paste Rename Share Delete | Sort View | Thumbnails |
        // Settings, whose arrow has Explorer's "See more" (...) commands.
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
        Cmd(_cmdExtract, ExplorerIcons.ExtractAll, ExplorerTheme.GFolder, "Extract all", "Extract all the files in this archive", CmdExtractAll);
        _cmdExtract.Available = false; // until an archive is selected or open (UpdateCommandStates)
        Cmd(_cmdSearchOptions, ExplorerIcons.SearchOptions, ExplorerTheme.GFilter, "Search options", "Where and how to search", null);
        Cmd(_cmdCloseSearch, ExplorerIcons.CloseSearch, ExplorerTheme.GClose, "Close search", "Close search (Esc)", CloseSearch);
        Cmd(_thumbsButton, ExplorerIcons.IconsSmall, ExplorerTheme.GGrid, "Thumbnails", "", null);
        Cmd(_cmdEveryday, ExplorerIcons.AddressGallery, ExplorerTheme.GPhoto, "Everyday files", "", null);
        Cmd(_cmdMore, null, ExplorerTheme.GSettings, null, "Settings (Ctrl+,). More commands under the arrow", null);
        Cmd(_cmdPreview, ExplorerIcons.DetailsPane, ExplorerTheme.GPane, "Preview", "Show or hide the preview pane (Alt+P)", TogglePreview);
        _cmdPreview.Alignment = ToolStripItemAlignment.Right;
        _cmdMore.AccessibleName = "Settings";
        _cmdMore.ButtonClick += (_, _) => _app.ShowSettings(this);

        // "New": Explorer's own New menu (Folder, Shortcut, Text Document...) for the open folder.
        _cmdNew.DropDownItems.Add(new ToolStripMenuItem("(new)"));
        _cmdNew.DropDown.Opening += (_, e) => { e.Cancel = true; BeginInvoke(ShowNewMenu); };

        _cmdSort.DropDownOpening += (_, _) => BuildSortMenu();
        _cmdView.DropDownOpening += (_, _) => BuildViewMenu();
        _cmdSearchOptions.DropDownOpening += (_, _) => BuildSearchOptionsMenu();
        _cmdSort.DropDownItems.Add(new ToolStripMenuItem("(sort)")); // placeholders so the arrow shows
        _cmdView.DropDownItems.Add(new ToolStripMenuItem("(view)"));
        _cmdSearchOptions.DropDownItems.Add(new ToolStripMenuItem("(search)"));

        // While a kind of file is chosen, Search options shows it ("Video") and becomes a split button: its × goes back
        // to every kind (see SyncSearchOptionsButton). Its icon is drawn in ApplyChromeMetrics.
        _cmdClearKind.DisplayStyle = ToolStripItemDisplayStyle.Image;
        _cmdClearKind.ImageScaling = ToolStripItemImageScaling.None;
        _cmdClearKind.AutoToolTip = false;
        _cmdClearKind.AccessibleName = "Clear kind";
        _cmdClearKind.Available = false;
        _cmdClearKind.Click += (_, _) => SetFilter(0);

        // Thumbnails: whatever is on screen as thumbnails (see ToggleThumbnails); again = back. The arrow offers
        // every file under the folder as thumbnails or as a list, and whether subfolders count.
        _thumbsButton.ButtonClick += (_, _) => ToggleThumbnails();
        // Everyday files: the button turns it on and off; its arrow picks which kinds of file count.
        _cmdEveryday.ButtonClick += (_, _) => ToggleEverydayFiles();
        _cmdEveryday.DropDownItems.Add(new ToolStripMenuItem("(kinds)"));
        _cmdEveryday.DropDownOpening += (_, _) => BuildEverydayMenu();
        _cmdEveryday.DropDown.Closing += (_, e) =>
        {
            if (_keepEverydayMenu && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            _keepEverydayMenu = false;
        };
        _zapThumbItem.Click += (_, _) => ToggleZap(thumbs: true);
        _zapListItem.Click += (_, _) => ToggleZap(thumbs: false);
        _zapDeepItem.Click += (_, _) => ToggleZapDepth();
        _zapEverydayItem.Click += (_, _) => ToggleEverydayFiles();
        _zapEverydayChooseItem.Click += (_, _) => _app.ShowSettings(this, "Thumbnails");
        _zapListItem.ShortcutKeyDisplayString = "Ctrl+Shift+Z";
        _thumbsButton.DropDownItems.AddRange([_zapThumbItem, _zapListItem, new ToolStripSeparator(), _zapDeepItem, _zapEverydayItem, _zapEverydayChooseItem]);
        _thumbsButton.DropDownOpening += (_, _) =>
        {
            string where = _zapActive ? _zapLabel : ZapTargets() is { Count: > 0 } roots ? ZapLabel(roots) : "this folder";
            where = where.Replace("&", "&&");
            _zapThumbItem.Text = $"Every file in {where} as thumbnails";
            _zapListItem.Text = $"Every file in {where} as a list";
            _zapThumbItem.Checked = _zapActive && _zapThumbs;
            _zapListItem.Checked = _zapActive && !_zapThumbs;
            _zapDeepItem.Checked = _app.Settings.ZapIncludeSubfolders;
            _zapEverydayItem.Checked = _app.Settings.EverydayFilesOnly;
        };

        _commands.Items.AddRange([_cmdNew, new ToolStripSeparator(), _cmdCut, _cmdCopy, _cmdPaste, _cmdRename, _cmdShare, _cmdDelete,
                                  new ToolStripSeparator(), _cmdSort, _cmdView, _cmdExtract, _sepSearch, _cmdSearchOptions, _cmdClearKind, _cmdCloseSearch,
                                  new ToolStripSeparator(), _thumbsButton, _cmdEveryday, new ToolStripSeparator(), _cmdMore, _cmdPreview]);

        // Navigation buttons left of the address bar.
        _nav.Dock = DockStyle.Left;
        _nav.AutoSize = true;
        _nav.CanOverflow = false;
        _nav.PassEmptySpaceToWindow = true; // the gaps drag the window, like the rest of the top row
        _nav.BackColor = ExplorerTheme.Chrome;
        Cmd(_navBack, null, ExplorerTheme.GBack, null, "Back (Alt+Left)", GoBack);
        Cmd(_navForward, null, ExplorerTheme.GForward, null, "Forward (Alt+Right)", GoForward);
        Cmd(_navUp, null, ExplorerTheme.GUp, null, "Up (Alt+Up)", NavigateUp);
        Cmd(_navRefresh, null, ExplorerTheme.GRefresh, null, "Refresh (F5)", RefreshResults);
        _nav.Items.AddRange([_navBack, _navForward, _navUp, _navRefresh]);

        // Address bar: breadcrumbs; click empty space (or Ctrl+L / Alt+D / F4) to type a path. Suggestions are the
        // folders on the disk and the addresses typed before.
        AddressAutoComplete.Attach(_address, TypedAddressList);
        _address.AccessibleName = "Address";
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = e.SuppressKeyPress = true;
                string target = _address.Text;
                _breadcrumb.EndEdit();
                if (OpenAddress(target)) FocusList();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = e.SuppressKeyPress = true;
                // One that closed the suggestion list (ProcessCmdKey, OpenHistory) ends nothing else.
                if (!_escapeForSuggestions && !AddressAutoComplete.ClosingSuggestions) CancelAddressEdit();
            }
            else if (e.KeyData == (Keys.Alt | Keys.Down))
            {
                e.Handled = e.SuppressKeyPress = true;
                _breadcrumb.OpenHistory(); // the addresses typed before, as Explorer's Alt+Down
            }
        };
        _breadcrumb = new BreadcrumbBar(_address) { Dock = DockStyle.Fill, MenuRenderer = ToolStripManager.Renderer };
        _breadcrumb.NavigateRequested += location => OpenFolder(location);
        _breadcrumb.HistoryProvider = TypedAddressList;
        _breadcrumb.HistoryChosen += address =>
        {
            _breadcrumb.EndEdit();
            if (OpenAddress(address)) FocusList();
        };
        _breadcrumb.DeleteHistoryRequested += () =>
        {
            _app.Settings.TypedAddresses.Clear();
            _app.Settings.Save();
        };
        AcceptDropsOnAddress();

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
            item.Image = item.Owner == _nav ? ExplorerTheme.Glyph(glyph, icon, ExplorerTheme.Icon, 0.9f)
                       : ExplorerTheme.Icon16(svg, glyph, icon);
            old?.Dispose();
        }
        var oldClear = _cmdClearKind.Image;
        _cmdClearKind.Image = ExplorerTheme.Glyph(ExplorerTheme.GClose, icon, ExplorerTheme.Icon, 0.75f);
        oldClear?.Dispose();
        _searchOptionsLook = (-1, 0); // the loop gave it the plain icon
        SyncSearchOptionsButton();
        _commands.ImageScalingSize = _nav.ImageScalingSize = new Size(icon, icon);
        // Measured from Explorer at 125%: buttons 43 high in a 46 row, first button 6 from the edge, 8 between buttons.
        _commands.Padding = new Padding(S(2), S(1), S(6), S(2));
        _commands.LayoutExplorerItems(_uiFont);
        _nav.Padding = new Padding(S(11), 0, 0, 0);
        _nav.LayoutExplorerItems(_uiFont);
        for (int i = 0; i < _nav.Items.Count; i++) _nav.Items[i].Margin = new Padding(0, 0, i == _nav.Items.Count - 1 ? S(14) : S(16), 0);
        _searchBar.Padding = new Padding(0, S(8), S(11), S(8));
        _searchBar.Height = S(48);
        ApplyTitleRowMetrics();
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

    /// <summary>
    /// The search box's scope chip: "Everything" in blue, or where a search looks ("Downloads", "3 folders"), shown
    /// while typing too. Clicking it switches between that folder and everything (<see cref="SearchScope"/>).
    /// </summary>
    void UpdateSearchPlaceholder()
    {
        var scope = ScopeButton();
        _searchField.SetScope(scope.Text, scope.Everything, clickable: scope.Click != SearchScope.Click.None, scope.Tip);
        if (_search.TextLength == 0) _search.PlaceholderText = scope.Everything ? "Search everything" : $"Search {scope.Text}";
    }

    /// <summary>The scope chip as it is now: what it says and what clicking it does.</summary>
    SearchScope.Button ScopeButton()
    {
        var selected = ScopeSelection();
        string? folder = _browsing && _explorer.CurrentPath is { Length: > 0 } ? _explorer.CurrentDisplayName : null;
        return SearchScope.Describe(_tab.SearchEverywhere, selected.Count > 0 ? ZapLabel(selected) : null, folder, _zapActive ? _zapLabel : null);
    }

    /// <summary>
    /// The scope chip was clicked: from a folder (the open one, the selected ones, or every file in one) to everything,
    /// and from everything back to the folder. It always switches, so it asks <see cref="ScopeButton"/> afresh rather
    /// than trusting what the chip last showed.
    /// </summary>
    void ToggleSearchScope()
    {
        var click = ScopeButton().Click;
        if (click == SearchScope.Click.None) return;
        if (click == SearchScope.Click.ToEverything) ScopeChipUsed();
        if (_zapActive)
        {
            // Everything leaves "every file in", keeping what's typed, and its thumbnails if the files showed as them.
            bool thumbs = _zapThumbs && _search.TextLength > 0 && !(_app.Settings.ThumbnailSize > 0);
            ExitZap(rerun: false);
            if (thumbs) SetViewMode(_lastThumbSize);
        }
        SetScope(selection: false, openFolder: click == SearchScope.Click.ToFolder);
        _app.Tried(GettingStarted.Everything);
        if (_search.TextLength == 0 && ExplorerShown) _explorer.FocusView(); else _search.Focus();
    }

    /// <summary>The open folder a search looks inside: the tab's folder, unless Alt+X or the scope chip said everywhere.</summary>
    string? SearchFolder => _browsing && !_tab.SearchEverywhere && _explorer.CurrentPath is { Length: > 0 } folder ? folder : null;

    /// <summary>
    /// A search typed while folders are selected in the view looks only inside them ("Search images"); with none
    /// selected it looks in the open folder. Either way, unless the tab looks everywhere. The selection is taken when
    /// the first letter is typed, since the view is hidden while results show.
    /// </summary>
    void UpdateSearchScope()
    {
        if (_search.TextLength == 0)
        {
            _selScope = [];
            _useSelScope = false;
        }
        else if (_explorer.Visible && !_zapActive && _search.Focused)
        {
            _selScope = SelectedFolders();
            _useSelScope = _selScope.Count > 0 && !_tab.SearchEverywhere;
        }
    }

    /// <summary>
    /// The selected folders a search looks inside, or would with nothing typed yet (the selection in the view), whether
    /// or not the tab looks everywhere instead: the chip goes back to them from "Everything".
    /// </summary>
    List<string> ScopeSelection() => _search.TextLength == 0 ? (ExplorerShown ? SelectedFolders() : []) : _useSelScope ? _selScope : [];

    /// <summary>
    /// The folders selected in the Explorer view when only folders are, at most <see cref="MaxScopeFolders"/>; none when
    /// nothing, a file (a zip too) or more are selected, so a search after Ctrl+A is never limited to part of what was
    /// selected. The placeholder asks at every selection change, so the view's attributes decide, never the disk: on a
    /// slow network share each check was a round trip that froze the window while the selection grew.
    /// </summary>
    List<string> SelectedFolders()
    {
        // The folder Back, Forward or Up selected by itself (the one they came from) isn't chosen: the open folder is.
        if (!_browsing || _explorer.SelectionFromNavigation || _explorer.SelectedCount is 0 or > MaxScopeFolders) return [];
        return _explorer.GetSelectedFolders(MaxScopeFolders);
    }

    const int MaxScopeFolders = 64;

    /// <summary>Where a search looks, for the status bar ("images", "3 folders"); null = everywhere.</summary>
    string? ScopeLabel() => ScopeButton() is { Everything: false } scope ? scope.Text : null;

    /// <summary>
    /// Search options and the scope chip: inside the selected folders, inside the open folder, or everywhere (for this
    /// tab, until it opens another folder; the selection doesn't count then either).
    /// </summary>
    void SetScope(bool selection, bool openFolder)
    {
        _useSelScope = selection && _selScope.Count > 0;
        _tab.SearchEverywhere = !openFolder && !_useSelScope;
        UpdateSearchPlaceholder();
        RunSearch(SearchReason.NewQuery);
        QueueStatusUpdate();
    }

    // ---- command bar actions ------------------------------------------------------------------

    /// <summary>New folder (Ctrl+Shift+N, the New menu's fallback): the view's own, as Explorer makes it. Search results: nothing.</summary>
    void CmdNewFolder()
    {
        if (!ExplorerShown || !CommandBarRules.CanMakeFolderIn(_explorer.CurrentPath, _explorer.CurrentParsingName)) return;
        _explorer.CreateNewFolder(_explorer.CurrentPath);
    }

    /// <summary>Explorer's own "New" menu (Folder, Shortcut, Text Document...) under the New button; a library's too.</summary>
    void ShowNewMenu()
    {
        if (!ExplorerShown || !CommandBarRules.CanMakeFolderIn(_explorer.CurrentPath, _explorer.CurrentParsingName)) return;
        string folder = _explorer.CurrentPath ?? _explorer.CurrentParsingName!;
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

    /// <summary>
    /// Extract all: Windows' own wizard for the selected archive, or for the archive whose contents are open. The verb is
    /// the shell's "extract" (zip's Compressed Folder and, on newer Windows 11, the other archive folders).
    /// </summary>
    void CmdExtractAll()
    {
        if (!ExplorerShown || ExtractTarget() is not { } target) return;
        if (target.Selected && _explorer.InvokeVerbOnSelection("extract")) return;
        // Another archiver may own the file type, and its menu then has no "extract": say so rather than do nothing.
        if (!ShellContextMenu.InvokeVerb(this, [target.Path], "extract")) _statusRight.Text = "Windows can't extract this kind of file here";
    }

    /// <summary>
    /// The archive Extract all would work on: the one selected file when it is an archive, else the archive the view is
    /// inside. From names and the attributes the view already holds (no disk), as the command bar asks at every
    /// selection change: a real folder named like an archive ("site.zip", "backup.tar") is not one.
    /// </summary>
    (string Path, bool Selected)? ExtractTarget()
    {
        if (_explorer.SelectedCount == 1 && _explorer.SelectedPath is { } p && CommandBarRules.CanExtract(Path.GetFileName(p), HasArchiveFolders)
            && _explorer.SelectedItemIsFile())
            return (p, true);
        return _explorer.CurrentArchive is { } archive && CommandBarRules.CanExtract(Path.GetFileName(archive), HasArchiveFolders) ? (archive, false) : null;
    }

    /// <summary>Whether this Windows opens .7z, .rar, .tar and the like itself (Windows 11's archive folders), not only zips.</summary>
    static bool HasArchiveFolders => s_archiveFolders ??= HasArchiveFolderClass();
    static bool? s_archiveFolders;

    static bool HasArchiveFolderClass()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("ArchiveFolder");
            return key != null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return false; }
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

    /// <summary>Search options (Explorer shows this button while searching): where to look, kind of file, matching.</summary>
    void BuildSearchOptionsMenu()
    {
        var items = _cmdSearchOptions.DropDownItems;
        items.Clear();
        if (_selScope.Count > 0)
            items.Add(new ToolStripMenuItem($"Search in {ZapLabel(_selScope).Replace("&", "&&")}", null, (_, _) => SetScope(selection: true, openFolder: true)) { Checked = _useSelScope });
        bool canScope = _browsing && _explorer.CurrentPath != null;
        string here = _explorer.CurrentDisplayName ?? "this folder";
        items.Add(new ToolStripMenuItem($"Search in {here.Replace("&", "&&")}", null, (_, _) => SetScope(selection: false, openFolder: true)) { Checked = !_useSelScope && SearchFolder != null, Enabled = canScope });
        items.Add(new ToolStripMenuItem("Search everywhere", null, (_, _) => SetScope(selection: false, openFolder: false)) { Checked = !_useSelScope && SearchFolder == null });
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

    /// <summary>
    /// Search options shows the tab's kind of file while one is chosen ("Video", with its icon), and its × (the split
    /// button's other half, shown by UpdateCommandStates) goes back to every kind; with none it is Explorer's
    /// "Search options". Called whenever the kind or the tab changes (SyncMenus).
    /// </summary>
    void SyncSearchOptionsButton()
    {
        if (_cmdSearchOptions.Owner is null) return; // the menus are built before the bar
        var kind = Filters.All[_filterIndex];
        bool any = _filterIndex > 0;
        _cmdSearchOptions.ToolTipText = any ? $"Showing only {kind.Shown}. Where and how to search" : "Where and how to search";
        _cmdSearchOptions.AccessibleName = any ? $"Search options: {kind.Name}" : null;
        _cmdClearKind.ToolTipText = any ? $"Show every kind of file, not only {kind.Shown}" : "";
        if (_searchOptionsLook != (_filterIndex, DeviceDpi))
        {
            _searchOptionsLook = (_filterIndex, DeviceDpi);
            int px = LogicalToDeviceUnits(16);
            var old = _cmdSearchOptions.Image;
            _cmdSearchOptions.Image = any ? ExplorerTheme.Glyph(kind.Glyph, px, ExplorerTheme.Icon)
                                          : ExplorerTheme.Icon16(ExplorerIcons.SearchOptions, ExplorerTheme.GFilter, px);
            old?.Dispose();
        }
        string text = any ? kind.Name : "Search options";
        if (_cmdSearchOptions.Text == text) return;
        _cmdSearchOptions.Text = text;
        _commands.LayoutExplorerItems(_uiFont); // its width follows its text
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
        _cmdRename.Enabled = sel > 0; // several results: the focused one, as F2 and the context menu's Rename do
        bool folderOpen = explorer && CommandBarRules.CanMakeFolderIn(_explorer.CurrentPath, _explorer.CurrentParsingName); // a library too
        _cmdNew.Enabled = folderOpen;
        _cmdPaste.Enabled = folderOpen && ClipboardHasPasteableFiles(); // kept current by the clipboard listener
        bool searching = _search.TextLength > 0;
        // Search options also shows while a kind of file narrows a list that isn't a search ("every file in"), so what
        // the list leaves out can be seen and undone. Available, not Visible (see below).
        bool options = searching || _filterIndex > 0 && !explorer;
        if (_cmdSearchOptions.Available != options) _sepSearch.Available = _cmdSearchOptions.Available = options;
        if (_cmdCloseSearch.Available != searching) _cmdCloseSearch.Available = searching;
        bool extract = explorer && ExtractTarget() != null;
        if (_cmdExtract.Available != extract) _cmdExtract.Available = extract;
        if (_cmdClearKind.Available != (options && _filterIndex > 0))
        {
            _cmdClearKind.Available = options && _filterIndex > 0;
            _commands.LayoutExplorerItems(_uiFont); // the halves touch while both show
        }
        bool thumbs = !explorer && ThumbnailMode;
        _thumbsButton.Enabled = !explorer || _explorer.CurrentPath != null || AtHome; // This PC, Network...: no folder of files; Home: its recent files
        _thumbsButton.Tag = thumbs; // drawn pressed while thumbnails are showing
        _thumbsButton.ToolTipText = explorer && AtHome ? "Thumbnails of the files you opened lately (Ctrl+Shift+X)"
            : explorer ? "Thumbnails of every file in this folder and its subfolders (Ctrl+Shift+X)"
            : thumbs ? (_zapActive ? "Back to the folder (Ctrl+Shift+X)" : "Back to the list (Ctrl+Shift+X)")
            : Filters.All[_filterIndex].Search == "folder:" ? "Show these folders as thumbnails (Ctrl+Shift+X)"
            : "Thumbnails of the files found and of every file in the folders found (Ctrl+Shift+X)";
        _thumbsButton.Invalidate();
        // Only where it applies: "every file in", and search results as thumbnails. Available, not Visible: Visible reads
        // false until the bar shows.
        bool everydayApplies = _zapActive || SearchInsideFolders;
        if (_cmdEveryday.Available != everydayApplies) _cmdEveryday.Available = everydayApplies;
        _cmdEveryday.Tag = _app.Settings.EverydayFilesOnly; // drawn pressed while on
        _cmdEveryday.Invalidate();
        _cmdEveryday.ToolTipText = _app.Settings.EverydayFilesOnly
            ? "Showing only everyday files: pictures, videos, music, documents... Click to show every file (code, settings, system files...); the arrow picks the kinds"
            : "Showing every file. Click to show only everyday files: pictures, videos, music, documents...";
        _cmdPreview.Checked = _app.Settings.PreviewVisible;
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _captionButtons.Active = _tabStrip.Active = true;
        UpdateCommandStates(); // the clipboard may have changed while we were in the background
    }

    /// <summary>
    /// Files dropped on a folder in the address bar go into that folder, as in File Explorer (how people move a file up
    /// a level); the segment lights up while the folder can take them. Not onto the folder they are already in (copying
    /// first, that would only make "name - Copy" duplicates), nor a folder into itself: <see cref="DropRules.Refused"/>.
    /// </summary>
    void AcceptDropsOnAddress()
    {
        // The rule's answer for the drag and segment it was last worked out for: worked out again only when the pointer
        // reaches another segment, not on every move (a drag can carry thousands of files).
        IReadOnlyList<string>? ruleDragged = null;
        string? ruleFolder = null;
        bool refused = false;
        ShellDragDrop.AcceptDrops(_breadcrumb, (screen, readDragged) =>
        {
            if (_breadcrumb.FolderAt(screen) is not { } folder) return null;
            var dragged = readDragged();
            if (dragged.Count == 0 && _draggingOut is { } own) dragged = own; // the results' own drag, if its data said nothing
            if (!ReferenceEquals(dragged, ruleDragged) || folder != ruleFolder)
            {
                (ruleDragged, ruleFolder) = (dragged, folder);
                // A library (Documents) puts files in its save folder, which is often where they already are.
                refused = dragged.Count > 0 && DropRules.Refused(dragged, ShellDragDrop.FileSystemFolder(folder) ?? folder);
            }
            return refused ? null : folder;
        },
        dropSpot: spot =>
        {
            if (spot is { } screen) _breadcrumb.ShowDropHighlight(screen);
            else _breadcrumb.ClearDropHighlight(); // the folder (or the rule) says no: no highlight
        },
        dragEnded: () =>
        {
            _breadcrumb.ClearDropHighlight();
            (ruleDragged, ruleFolder) = (null, null); // the paths aren't kept after the drag
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (HandleTitleBarMessage(ref m)) return;
        if (HandleAppCommand(ref m)) return;
        if (ShellContextMenu.HandleMenuMessage(ref m)) return; // Explorer's New menu fills its submenu on WM_INITMENUPOPUP
        if (HandleClipboardMessage(ref m)) return;
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

    /// <summary>
    /// Sort, as in File Explorer: the columns the folder shows (Recycle Bin's, a music folder's...), the one sorted on and
    /// its direction checked, and More... for the view's Choose details. The fixed four when the view can't list them.
    /// </summary>
    void BuildSortMenu()
    {
        _cmdSort.DropDownItems.Clear();
        if (ExplorerShown)
        {
            var sort = _explorer.GetSortColumn();
            var columns = _explorer.GetVisibleColumns(MaxSortColumns);
            if (columns.Count == 0)
                foreach (var (title, column) in new (string, ExplorerSortColumn)[]
                         { ("Name", ExplorerSortColumn.Name), ("Date modified", ExplorerSortColumn.DateModified), ("Type", ExplorerSortColumn.Type), ("Size", ExplorerSortColumn.Size) })
                    columns.Add((ExplorerHost.SortKeyOf(column), title));
            foreach (var (key, name) in columns)
            {
                var k = key;
                bool on = sort is { } s && s.Key.fmtid == k.fmtid && s.Key.pid == k.pid;
                _cmdSort.DropDownItems.Add(new ToolStripMenuItem(name.Replace("&", "&&"), null, (_, _) => _explorer.SortByColumn(k)) { Checked = on });
            }
            _cmdSort.DropDownItems.Add(new ToolStripSeparator());
            _cmdSort.DropDownItems.Add(new ToolStripMenuItem("Ascending", null, (_, _) => _explorer.SetSortDirection(descending: false)) { Checked = sort is { Descending: false } });
            _cmdSort.DropDownItems.Add(new ToolStripMenuItem("Descending", null, (_, _) => _explorer.SetSortDirection(descending: true)) { Checked = sort is { Descending: true } });
            _cmdSort.DropDownItems.Add(new ToolStripSeparator());
            _cmdSort.DropDownItems.Add(new ToolStripMenuItem("More...", null, (_, _) => _explorer.ChooseDetails()));
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

    /// <summary>Most columns the Sort menu lists (a folder can show dozens in Details).</summary>
    const int MaxSortColumns = 30;

    /// <summary>
    /// View: the folder's eight layouts with the one it is in checked and their keys (Ctrl+Shift+1 to 8, handled in
    /// ProcessCmdKey); while results show, their list and thumbnail sizes with theirs (the View menu's shortcuts).
    /// </summary>
    void BuildViewMenu()
    {
        _cmdView.DropDownItems.Clear();
        // Ticks in their own column, as Windows 11's menus show them: drawn where the icon is, the icon hid them.
        if (_cmdView.DropDown is ToolStripDropDownMenu viewMenu) viewMenu.ShowCheckMargin = true;
        if (ExplorerShown)
        {
            int current = _explorer.GetViewModeAndIconSize() is { } v ? CommandBarRules.LayoutIndex(v.Mode, v.IconSize) : -1;
            for (int i = 0; i < ExplorerLayoutNames.Length; i++)
            {
                int layout = i;
                _cmdView.DropDownItems.Add(new ToolStripMenuItem(ExplorerLayoutNames[i].Title, MenuIcon(ExplorerLayoutNames[i].Icon), (_, _) => SetExplorerLayout(layout))
                {
                    Checked = i == current,
                    ShortcutKeyDisplayString = CommandBarRules.LayoutShortcut(i),
                });
            }
        }
        else
        {
            int thumb = EffectiveThumbSize;
            foreach (var (title, size, digit) in new (string, int, int)[] { ("Extra large thumbnails", 256, 1), ("Large thumbnails", 160, 2), ("Medium thumbnails", 96, 3), ("Details", 0, 6) })
            {
                int sz = size;
                // A size zoomed between them (Ctrl+wheel) counts as Large, as the menu bar's View shows it (SyncMenus).
                bool on = thumb == sz || sz == 160 && thumb > 0 && thumb is not (256 or 96);
                _cmdView.DropDownItems.Add(new ToolStripMenuItem(title, null, (_, _) => SetViewMode(sz)) { Checked = on, ShortcutKeyDisplayString = $"Ctrl+Shift+{digit}" });
            }
        }
        _cmdView.DropDownItems.Add(new ToolStripSeparator());
        _cmdView.DropDownItems.Add(new ToolStripMenuItem("Navigation pane", MenuIcon(ExplorerIcons.NavPane), (_, _) => ToggleNavPane()) { Checked = _app.Settings.ExplorerNavPane });
        _cmdView.DropDownItems.Add(new ToolStripMenuItem("Preview pane", MenuIcon(ExplorerIcons.DetailsPane), (_, _) => TogglePreview()) { Checked = _app.Settings.PreviewVisible });
        _cmdView.DropDownItems.Add(new ToolStripSeparator());
        _cmdView.DropDownItems.Add(new ToolStripMenuItem("Hidden items", null, (_, _) => ToggleHiddenItems()) { Checked = ExplorerOptions.ShowHidden });
    }

    /// <summary>Explorer's layouts in View menu order (<see cref="CommandBarRules.Layouts"/> holds the mode and size of each).</summary>
    static readonly (string Title, string Icon)[] ExplorerLayoutNames =
    [
        ("Extra large icons", ExplorerIcons.IconsExtraLarge), ("Large icons", ExplorerIcons.IconsLarge), ("Medium icons", ExplorerIcons.IconsMedium),
        ("Small icons", ExplorerIcons.IconsSmall), ("List", ExplorerIcons.IconsList), ("Details", ExplorerIcons.IconsDetails),
        ("Tiles", ExplorerIcons.IconsTiles), ("Content", ExplorerIcons.IconsContent),
    ];

    /// <summary>One of Explorer's layouts for the folder view (View menu, Ctrl+Shift+1 to 8).</summary>
    void SetExplorerLayout(int index)
    {
        if (index < 0 || index >= CommandBarRules.Layouts.Count) return;
        var (mode, size) = CommandBarRules.Layouts[index];
        _explorer.SetViewMode(mode, size);
        QueueStatusUpdate(); // the status bar's Details / Large icons buttons
    }

    /// <summary>View › Hidden items: File Explorer's own setting, so both apps show the same; every tab's folder follows.</summary>
    void ToggleHiddenItems()
    {
        ExplorerOptions.ShowHidden = !ExplorerOptions.ShowHidden;
        foreach (var w in _app.Windows) w.OnHiddenFilesChanged();
    }

    /// <summary>Hidden items were turned on or off: each tab's folder view shows its files again.</summary>
    internal void OnHiddenFilesChanged()
    {
        foreach (var t in _tabs) if (t.Explorer.IsHandleCreated) t.Explorer.RefreshView();
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
        else if (_cutPathsStale) QueueClipboardRefresh(); // what is cut wasn't read while the folder view showed
        if (!explorer) _tab.ForwardSearch = null; // results showing are a newer page than the search Back closed
        UpdateNavButtons(explorer);
        UpdateSearchPlaceholder();
        UpdateCommandStates();
        UpdateTitle();
        QueuePreview();
        QueueStatusUpdate();
        // Every way in or out of the folder view comes here, also those that clear the search box without a search.
        DropListingResults();
        UpdateFolderBar();
    }

    void UpdateNavButtons(bool explorer)
    {
        _navBack.Enabled = _browsing && (_explorer.CanGoBack || !explorer); // Back also leaves a search
        _navForward.Enabled = _browsing && (_explorer.CanGoForward || explorer && ForwardSearchHere);
        _navUp.Enabled = _browsing && _explorer.CanGoUp;
        UpdateNavToolTips(explorer);
    }

    /// <summary>The Back, Forward and Up tooltips name where they go, as File Explorer's do ("Back to Downloads").</summary>
    void UpdateNavToolTips(bool explorer)
    {
        // Back from results goes to the folder they were searched from.
        string? back = !explorer && _browsing ? _explorer.CurrentDisplayName : _explorer.BackName;
        _navBack.ToolTipText = _navBack.Enabled && back is { Length: > 0 } ? $"Back to {back} (Alt+Left)" : "Back (Alt+Left)";
        string? forward = explorer && ForwardSearchHere && _tab.ForwardSearch is { } search ? $"the search for \"{search.Text}\"" : _explorer.ForwardName;
        _navForward.ToolTipText = _navForward.Enabled && forward is { Length: > 0 } ? $"Forward to {forward} (Alt+Right)" : "Forward (Alt+Right)";
        string? up = _explorer.ParentDisplayName;
        _navUp.ToolTipText = _navUp.Enabled && up is { Length: > 0 } ? $"Up to {up} (Alt+Up)" : "Up (Alt+Up)";
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

    void OnExplorerNavigated(FolderTab tab)
    {
        // An opened folder is where searches look again (Alt+X's "everything" lasts until one is opened, the same one
        // too). A new tab's first folder doesn't count, so a window Alt+X opens still searches everything.
        if (tab.Navigated) tab.SearchEverywhere = false;
        tab.Navigated = true;
        // Another folder opened: like forward history, the search Back closed is gone. Back and Forward keep it, as they
        // keep forward history: it shows again on Forward once the view is back at the place it was closed in.
        if (!tab.Explorer.LastNavigationWasHistory) tab.ForwardSearch = null;
        // Each folder gets a new view, with Windows' own drop target: copying is made its first choice too.
        BeginInvoke(() => { if (tab.Explorer.IsHandleCreated) ShellDragDrop.CopyFirstInside(tab.Explorer.Handle); });
        if (tab != _tab)
        {
            // A tab in the background moved on by itself (its folder was deleted, say): only its name and icon change.
            tab.Browsing = true;
            _tabStrip.TabsChanged();
            return;
        }
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
        // As File Explorer does: a folder opened goes into Windows' recent items (Home, the taskbar's jump list).
        if (_explorer.CurrentPath is { Length: > 0 } opened && !_app.IsDemo)
        {
            AddToRecent(opened, _explorer.CurrentIdList);
            _app.QueueJumpList();
        }
        SyncAddress();
        SyncNavTree();
        UpdateLeftView();
        if (_search.TextLength > 0) RunSearch(SearchReason.NewQuery); // re-scope a typed search to the new folder
        QueueThumbnailsHint();
    }

    /// <summary>
    /// Leaves the search results (and "every file in") so the folder itself shows. File Explorer does the same when
    /// you open a folder from a search; otherwise the old search would keep running and look like the folder.
    /// </summary>
    void LeaveResults()
    {
        if (_zapActive) ExitZap(rerun: false);
        if (_search.TextLength > 0)
        {
            _suppress = true;
            _search.Clear();
            _suppress = false;
        }
    }

    /// <summary>
    /// What was typed in the address bar, as File Explorer takes it: a place opens (a path, "shell:" name, or a folder
    /// inside the open one); a web address opens in the browser; anything else runs as a command in the open folder
    /// ("cmd", "powershell", "wt", "notepad notes.txt"). False when it was none of them.
    /// </summary>
    bool OpenAddress(string text)
    {
        string typed = text.Trim();
        if (typed.Length == 0) { GoHome(); return true; }
        string? folder = _browsing ? _explorer.CurrentPath : null;
        if (AddressCommand.IsWebAddress(typed)) return StartAddress(new AddressCommand.Run(typed, "", folder), typed, quiet: false);
        string expanded = Environment.ExpandEnvironmentVariables(typed.Trim('"'));
        // A folder or file in the open folder, by its name ("Reference", "notes.txt"), before one of the same name on
        // the desktop (the shell takes a bare name as the desktop's).
        if (folder != null && !Path.IsPathRooted(expanded) && expanded.IndexOfAny(Path.GetInvalidPathChars()) < 0 && !expanded.Contains(':'))
        {
            string inside = Path.Combine(folder, expanded);
            if (Directory.Exists(inside)) return RememberTyped(inside, OpenFolder(inside)); // the full path: the name means this folder only here
            if (File.Exists(inside)) return StartAddress(new AddressCommand.Run(inside, "", folder), typed, quiet: false);
        }
        if (ShellNative.CanParse(expanded) || ShellNative.CanParse(expanded + (expanded.Length == 2 && expanded[1] == ':' ? "\\" : "")))
            return RememberTyped(typed, OpenFolder(typed));
        return AddressCommand.Parse(typed, folder) is { } run && StartAddress(run, typed, quiet: false);
    }

    /// <summary>A typed address that opened a place goes to the top of the typed addresses (the drop-down, the suggestions).</summary>
    bool RememberTyped(string address, bool opened)
    {
        if (opened && _app.Settings.TypedAddresses.Add(address)) _app.Settings.Save();
        return opened;
    }

    /// <summary>
    /// The typed addresses offered: Rock Preview's, then File Explorer's own (read, never written). Safe off the UI thread:
    /// the suggestions ask for them on a thread of their own.
    /// </summary>
    IReadOnlyList<string> TypedAddressList()
        // The demo (screenshots, presentations) shows its own list only, never the user's real File Explorer paths.
        => AddressHistory.Combine(_app.Settings.TypedAddresses.Items, _app.IsDemo ? [] : AddressAutoComplete.ExplorerTypedPaths());

    /// <summary>Starts what the address bar was given, as the Run box would (the shell finds "cmd" or "code").</summary>
    bool StartAddress(AddressCommand.Run run, string typed, bool quiet)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(run.File) { UseShellExecute = true, Arguments = run.Arguments };
            if (run.WorkingFolder != null) psi.WorkingDirectory = run.WorkingFolder;
            System.Diagnostics.Process.Start(psi)?.Dispose();
            SyncAddress();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            if (!quiet) _statusRight.Text = $"Can't find \"{typed}\": it isn't a folder here or a program Windows knows.";
            SyncAddress();
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern void SHAddToRecentDocs(uint flags, string path);

    /// <summary>SHARDAPPIDINFOIDLIST (ShlObj_core.h): an item's ID list and the app it was opened in.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SHARDAPPIDINFOIDLIST
    {
        public IntPtr pidl;     // PCIDLIST_ABSOLUTE
        public IntPtr pszAppID; // PCWSTR
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "SHAddToRecentDocs")]
    static extern void SHAddToRecentDocsWithAppId(uint flags, ref SHARDAPPIDINFOIDLIST info);

    const uint SHARD_APPIDINFOIDLIST = 5;
    /// <summary>File Explorer's AppUserModelID: what its Frequent folders and its jump list learn from.</summary>
    const string ExplorerAppId = "Microsoft.Windows.Explorer";

    static void AddToRecent(string path, byte[]? idList)
    {
        try { SHAddToRecentDocs(3 /* SHARD_PATHW */, path); } catch (Exception) { }
        // Also as opened in File Explorer: this process has Rock Preview's own AppUserModelID, so without it the folders
        // browsed here never reach Frequent folders on Explorer's Home and Quick access. Named by the ID list the view
        // already has, so nothing asks the disk (a share that doesn't answer can't hold up the window).
        if (idList is not { Length: > 0 }) return;
        IntPtr pidl = IntPtr.Zero, appId = IntPtr.Zero;
        try
        {
            pidl = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(idList.Length);
            System.Runtime.InteropServices.Marshal.Copy(idList, 0, pidl, idList.Length);
            appId = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni(ExplorerAppId);
            var info = new SHARDAPPIDINFOIDLIST { pidl = pidl, pszAppID = appId };
            SHAddToRecentDocsWithAppId(SHARD_APPIDINFOIDLIST, ref info);
        }
        catch (Exception) { }
        finally
        {
            if (pidl != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pidl);
            if (appId != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(appId);
        }
    }

    /// <summary>A folder the user chose to open (address bar, breadcrumb, results, menus): shown as the folder.</summary>
    bool OpenFolder(string location, string? select = null)
    {
        LeaveResults();
        return BrowseTo(location, select);
    }

    /// <summary>Back: a search (or "every file in") is a page above the folder, like in Explorer, so Back closes it first.</summary>
    void GoBack()
    {
        if (_browsing && !ExplorerShown)
        {
            if (_zapActive) ExitZap(rerun: true);
            else
            {
                // Kept before the box empties, so the Forward button already counts it when the folder shows.
                if (_search.TextLength > 0)
                {
                    _tab.ForwardSearch = (_search.Text, _sort, _desc);
                    _tab.ForwardSearchAt = (_explorer.CurrentParsingName, _explorer.BackCount);
                }
                CloseSearch();
            }
            if (ExplorerShown) _explorer.FocusView();
            return;
        }
        _explorer.GoBack();
    }

    /// <summary>The search Back closed belongs here: the view is at the folder, and the place in the history, it was closed in.</summary>
    bool ForwardSearchHere => _tab.ForwardSearch != null && _tab.ForwardSearchAt == (_explorer.CurrentParsingName, _explorer.BackCount);

    /// <summary>Forward: after Back closed a search, the search comes back first (the page Back left), as in Explorer.</summary>
    void GoForward()
    {
        if (ExplorerShown && ForwardSearchHere && _tab.ForwardSearch is { } search)
        {
            _tab.ForwardSearch = null;
            if ((_sort, _desc) != (search.Sort, search.Descending))
            {
                (_sort, _desc) = (search.Sort, search.Descending);
                UpdateSortIndicator();
                SyncMenus();
            }
            SetSearch(search.Text);
            FocusTabContent();
            return;
        }
        LeaveResults();
        _explorer.GoForward();
    }

    /// <summary>
    /// Opens the current location (or the selected result's folder) in a File Explorer window. explorer.exe with a
    /// location always opens File Explorer itself, even when Rock Preview replaces it.
    /// </summary>
    void OpenInFileExplorer()
    {
        if (!ExplorerShown && SelectedPaths(1) is [var path])
        {
            FileOps.OpenPath(this, [path]); // File Explorer with the item selected
            return;
        }
        string? location = _browsing ? _explorer.CurrentPath ?? _explorer.CurrentParsingName : null;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", location is null ? "" : "\"" + location + "\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to open File Explorer.\n\n{ex.Message}", "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void NavigateUp()
    {
        if (!_browsing) return;
        LeaveResults();
        _explorer.GoUp();
    }

    void GoHome()
    {
        if (!OpenFolder(HomeLocation)) BrowseTo(ThisPcLocation);
    }

    /// <summary>
    /// Opens where File Explorer opens its new windows and tabs (Folder Options' "Open File Explorer to"): Home, This PC,
    /// Downloads or OneDrive. Home when that place doesn't open (no OneDrive on this PC, say).
    /// </summary>
    void GoToStartPlace()
    {
        string? where = ExplorerStartPlace() switch
        {
            StartPlace.ThisPc => ThisPcLocation,
            StartPlace.Downloads => "shell:Downloads",
            StartPlace.OneDrive => "shell:OneDrive",
            _ => null,
        };
        if (where is not null)
        {
            LeaveResults();
            if (!_explorer.IsHandleCreated) _ = _explorer.Handle; // create the shell view even while hidden
            if (_explorer.Navigate(where)) return; // not BrowseTo: a place that isn't there says nothing, Home just opens
        }
        GoHome();
    }

    /// <summary>File Explorer's "Open File Explorer to", read each time (it can change while the app runs); Home when it can't be read.</summary>
    static StartPlace ExplorerStartPlace()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartPlaces.LaunchToKey);
            return StartPlaces.FromLaunchTo(key?.GetValue(StartPlaces.LaunchToValue));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return StartPlace.Home;
        }
    }

    /// <summary>Before the window shows: open at this folder (null = where File Explorer opens) instead of the last folder.</summary>
    public void OpenAsExplorer(string? location)
    {
        _explorerStart = true;
        _explorerLocation = location;
    }

    /// <summary>Opens the last folder (or where File Explorer opens) when the window starts, like File Explorer.</summary>
    void OpenStartLocation()
    {
        if (_explorerStart)
        {
            if (_explorerLocation is { Length: > 0 } where && BrowseTo(where)) return;
            GoToStartPlace();
            return;
        }
        string last = _app.Settings.LastLocation;
        if (last.Length > 0 && BrowseTo(last)) return;
        GoToStartPlace();
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

    /// <summary>
    /// After typing an address was given up (Escape): the keyboard goes back to the window that had it when the typing
    /// began (the folder view, the navigation pane, the results, the search box), if it is still there and shown;
    /// otherwise to the files.
    /// </summary>
    void ReturnFocusFromAddress()
    {
        IntPtr before = _breadcrumb.FocusBeforeEdit;
        try
        {
            if (before != IntPtr.Zero && before != _address.Handle && IsWindow(before) && IsChild(Handle, before)
                && IsWindowVisible(before) && IsWindowEnabled(before))
            {
                if (_explorer.IsHandleCreated && (before == _explorer.Handle || IsChild(_explorer.Handle, before)))
                {
                    _explorer.FocusView();
                    return;
                }
                if (Control.FromHandle(before) is { } control)
                {
                    if (control.CanFocus && control.Focus()) return;
                }
                else if (SetFocus(before) != IntPtr.Zero || GetFocus() == before) return; // a window of Windows' own (the navigation pane's tree)
            }
        }
        catch (Exception) { } // then the files, below
        FocusList();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetFocus();

    /// <summary>Folders open inside Rock Preview; files open with their default app.</summary>
    void OpenSelectedOrBrowse()
    {
        var paths = SelectedPaths(1000);
        if (paths.Count == 1 && Directory.Exists(paths[0]))
        {
            OpenFolder(paths[0]);
            _app.RecordRuns(paths);
            return;
        }
        _app.RecordRuns(FileOps.Open(this, paths));
    }

    /// <summary>Shows the item in its folder, inside Rock Preview, selected.</summary>
    void BrowseToSelectedItemFolder()
    {
        var paths = SelectedPaths(1);
        if (paths.Count == 0) return;
        string p = paths[0].TrimEnd('\\');
        string? parent = Path.GetDirectoryName(p);
        if (parent == null) { OpenFolder(paths[0]); return; }
        OpenFolder(parent, select: p);
    }

    /// <summary>
    /// -select / File Explorer's /select,: the item's folder opens with the item selected and in view; an item that isn't
    /// there opens the nearest folder above it that is. Looked up off the UI thread: a share that doesn't answer can't
    /// freeze the window.
    /// </summary>
    public void ShowItem(string path)
    {
        Task.Run(() =>
        {
            ItemSelection.Target? target = null;
            try { target = ItemSelection.Resolve(path, p => File.Exists(p) || Directory.Exists(p)); }
            catch (Exception) { }
            _app.Post(() =>
            {
                if (IsDisposed) return;
                if (target is { } t) OpenFolder(t.Folder, t.Select);
                else OpenFolder(path); // a "shell:" name opens; anything else says it can't be opened
            });
        });
    }

    // ---- search scope -------------------------------------------------------------------------

    /// <summary>Extra filter ANDed with the query: the ZAP set, the selected folders, or the open folder when searching inside it.</summary>
    string? BrowseFilter()
    {
        if (_zapActive) return ZapFilter();
        if (_useSelScope && _selScope.Count > 0)
            return _selScope.Count == 1 ? Quote(WithSlash(_selScope[0])) : "<" + string.Join(" | ", _selScope.Select(f => Quote(WithSlash(f)))) + ">";
        if (SearchFolder is { } folder)
            return Quote(WithSlash(folder));
        return null;
    }

    static string WithSlash(string folder) => folder.EndsWith('\\') ? folder : folder + "\\";
    static string Quote(string s) => "\"" + s + "\"";

    // ---- Thumbnails and ZAP (every file under a folder) ------------------------------------------

    /// <summary>
    /// The Thumbnails button (Ctrl+Shift+X). On a folder: every file in it and its subfolders as thumbnails, and
    /// again = back to the folder. Anywhere else (search results, or those files): thumbnails on and off; search results
    /// as thumbnails bring in every file inside the folders found (<see cref="SearchInsideFolders"/>).
    /// </summary>
    void ToggleThumbnails()
    {
        ThumbnailsUsed();
        if (ExplorerShown) { ToggleZap(thumbs: true); return; }
        if (_zapActive)
        {
            if (!_zapThumbs) { ToggleZap(thumbs: true); return; }
            ExitZap(rerun: true);
            if (ExplorerShown) _explorer.FocusView();
            return;
        }
        bool on = !ThumbnailMode;
        SetViewMode(on ? _lastThumbSize : 0);
        if (on) _app.Tried(GettingStarted.Thumbnails);
        FocusList();
    }

    /// <summary>The folders "every file in..." covers: the open folder, or the folders selected in the results.</summary>
    List<string> ZapTargets()
    {
        var roots = new List<string>();
        if (ExplorerShown || (_browsing && _explorer.CurrentPath != null && _search.TextLength == 0))
        {
            if (_explorer.CurrentPath is { } folder) roots.Add(folder);
            return roots;
        }
        foreach (var p in SelectedPaths(64)) if (Directory.Exists(p)) roots.Add(p.TrimEnd('\\').Length == 2 ? p : p.TrimEnd('\\'));
        if (roots.Count == 0 && _browsing && _explorer.CurrentPath is { } open) roots.Add(open);
        return roots;
    }

    static string ZapLabel(List<string> roots) =>
        roots.Count == 1 ? (Path.GetFileName(roots[0].TrimEnd('\\')) is { Length: > 0 } n ? n : roots[0]) : $"{roots.Count} folders";

    void ToggleZap(bool thumbs)
    {
        ThumbnailsUsed();
        if (_zapActive && _zapThumbs == thumbs) { ExitZap(rerun: true); return; }
        if (_zapActive)
        {
            _zapThumbs = thumbs;
            if (thumbs) _app.Tried(GettingStarted.Thumbnails);
            ApplyViewMode(); UpdateLeftView(); RunSearch(SearchReason.NewQuery);
            return;
        }

        // Home: the files opened lately, as Home lists them under Recent.
        List<string>? recent = ExplorerShown && AtHome ? ReadRecentFiles() : null;
        var roots = recent != null ? [] : ZapTargets();
        if (roots.Count == 0 && recent == null)
        {
            MessageBox.Show(this, "Open a folder, or select folders in the results, to see every file in it at once.",
                "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _zapRoots = roots;
        _tab.ZapRecent = recent;
        _zapLabel = recent != null ? "Recent files" : ZapLabel(roots);
        // The files come in the order the folder showed them (newest first in Downloads, say), not the last search's.
        _sortBeforeZap = (_sort, _desc);
        _sortFromFolder = ExplorerShown ? _explorer.CurrentSort() : null;
        if (_sortFromFolder is { } folderSort)
        {
            (_sort, _desc) = folderSort;
            UpdateSortIndicator();
            SyncMenus();
        }
        _zapActive = true;
        _zapThumbs = thumbs;
        if (recent == null) ChooseZapSource(roots); // the index, or the disk when the index doesn't have the folders
        if (thumbs) _app.Tried(GettingStarted.Thumbnails);
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
        _tab.ZapRecent = null;
        DisposeListing(_tab);
        _tab.ZapSearchRoots = [];
        _tab.FolderBarDismissedFor = null;
        ResetDiskCheck(_tab);
        // Back to the search's own order, unless it was changed while every file showed.
        if (_sortFromFolder is { } folderSort && (_sort, _desc) == folderSort && _sortBeforeZap is { } before)
        {
            (_sort, _desc) = before;
            UpdateSortIndicator();
            SyncMenus();
        }
        _sortFromFolder = _sortBeforeZap = null;
        if (rerun)
        {
            _suppress = true;
            _search.Clear();
            _suppress = false;
        }
        ApplyViewMode();
        UpdateLeftView(); // and the folder bar and the empty text
        if (rerun) RunSearch(SearchReason.NewQuery);
    }

    /// <summary>
    /// Everyday files' arrow: on or off, and which kinds count (ticking one keeps the menu open for the next), with
    /// extensions of one's own in Settings › Thumbnails.
    /// </summary>
    void BuildEverydayMenu()
    {
        var s = _app.Settings;
        var items = _cmdEveryday.DropDownItems;
        items.Clear();
        items.Add(new ToolStripMenuItem("Everyday files only", null, (_, _) => ToggleEverydayFiles()) { Checked = s.EverydayFilesOnly });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Everyday files are") { Enabled = false });
        var on = new HashSet<string>(s.Everyday.KindsOn, StringComparer.OrdinalIgnoreCase);
        foreach (var kind in EverydayFiles.Kinds)
        {
            var k = kind;
            var item = new ToolStripMenuItem($"{k.Name.Replace("&", "&&")}  ({k.Examples})") { Checked = on.Contains(k.Key) };
            item.Click += (_, _) =>
            {
                _keepEverydayMenu = true;
                item.Checked = !item.Checked;
                SetEverydayKind(k.Key, item.Checked);
            };
            items.Add(item);
        }
        if (s.EverydayAdded.Count > 0)
            items.Add(new ToolStripMenuItem("Also " + string.Join(", ", s.EverydayAdded.Take(8)) + (s.EverydayAdded.Count > 8 ? "..." : "")) { Enabled = false });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Add extensions...", null, (_, _) => _app.ShowSettings(this, "Thumbnails")));
        items.Add(new ToolStripMenuItem("Back to the usual", null, (_, _) =>
        {
            s.EverydayKindsOff = [];
            s.EverydayAdded = [];
            s.EverydayFilesOnly = true;
            s.Save();
            foreach (var w in _app.Windows) w.OnZapOptionsChanged();
        }));
    }

    /// <summary>A kind of everyday file ticked on or off: it counts from now on, in every window (and Everyday files is on).</summary>
    void SetEverydayKind(string key, bool on)
    {
        var s = _app.Settings;
        var off = s.EverydayKindsOff.Where(k => !k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!on) off.Add(key);
        s.EverydayKindsOff = off;
        s.EverydayFilesOnly = true;
        s.Save();
        foreach (var w in _app.Windows) w.OnZapOptionsChanged();
    }

    /// <summary>"Everyday files": Thumbnails' files leave out code, settings, program and system files, or show them again.</summary>
    void ToggleEverydayFiles()
    {
        _app.Settings.EverydayFilesOnly = !_app.Settings.EverydayFilesOnly;
        _app.Settings.Save();
        foreach (var w in _app.Windows) w.OnZapOptionsChanged();
    }

    void ToggleZapDepth()
    {
        _app.Settings.ZapIncludeSubfolders = !_app.Settings.ZapIncludeSubfolders;
        _app.Settings.Save();
        _app.Tried(GettingStarted.Subfolders);
        foreach (var w in _app.Windows) w.OnZapOptionsChanged();
    }

    /// <summary>Every file under the roots (files only), or only those directly inside them.</summary>
    /// <summary>The Explorer view shows Home.</summary>
    bool AtHome => _browsing && _explorer.CurrentPath == null
                   && (_explorer.CurrentParsingName?.Contains("f874310e-b6b7-47dc-bc84-b9e6b38f5903", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>The files opened lately (Windows' Recent folder), newest first.</summary>
    static List<string> ReadRecentFiles() => RecentFiles.Read(Environment.GetFolderPath(Environment.SpecialFolder.Recent),
        lnk => Shortcuts.Read(lnk)?.Path, File.Exists);

    string ZapFilter()
    {
        // Home's recent files: each by its folder and name.
        if (_tab.ZapRecent is { } recent)
            return "file: " + RecentFiles.Filter(recent) + (_app.Settings.EverydayFilesOnly ? " " + _app.Settings.Everyday.Filter : "");
        bool deep = _app.Settings.ZapIncludeSubfolders;
        // The names the index (or the tab's listing) has the folders under: a SUBST drive's folder is under its target.
        var roots = _tab.ZapSearchRoots.Count > 0 ? _tab.ZapSearchRoots : _zapRoots;
        var terms = roots.Select(r =>
        {
            string folder = r.TrimEnd('\\');
            return deep ? Quote(WithSlash(r)) : "parent:" + Quote(folder.Length == 2 ? folder + "\\" : folder);
        }).ToList();
        string scope = terms.Count == 1 ? terms[0] : "<" + string.Join(" | ", terms) + ">";
        return "file: " + scope + (_app.Settings.EverydayFilesOnly ? " " + _app.Settings.Everyday.Filter : "");
    }

    string ZapStatus()
    {
        if (_tab.ZapRecent != null)
            return $"Recent files: {_ids.Length:N0}" + (_app.Settings.EverydayFilesOnly ? " (everyday files only)" : "");
        string where = _app.Settings.ZapIncludeSubfolders ? "including subfolders" : "this folder only";
        if (_app.Settings.EverydayFilesOnly) where += ", everyday files only";
        if (_listing != null) where += ", read from the disk";
        return $"Every file in {_zapLabel}: {_ids.Length:N0} ({where})";
    }
}

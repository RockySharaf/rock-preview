using System.Drawing.Drawing2D;
using Anything.Core;
using Anything.Core.Indexing;
using Anything.Core.Search;
using Anything.Explorer;
using Anything.Interop;

namespace Anything;

/// <summary>
/// Settings, laid out like Windows 11's Settings app: sections on the left; on the right the section's cards, each a
/// setting with a line explaining it and its switch or button. Changes apply as they are made. Changes to what is
/// indexed (drives, folders, what to leave out) go to the index once, when the window closes; it adds or drops only
/// what changed, so searches keep working meanwhile.
/// </summary>
internal sealed partial class SettingsForm : Form
{
    readonly AnythingApp _app;
    readonly ListBox _nav = new() { Dock = DockStyle.Left, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false };
    readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly List<(string Name, char Glyph, PageStack Page)> _sections = [];
    bool _loading, _indexChanged;

    // General
    readonly ToggleSwitch _startWithWindows = new(), _runInBackground = new(), _showTray = new(), _replaceExplorer = new(), _openInTabs = new(), _checkUpdates = new(), _autoUpdate = new();
    readonly ToggleSwitch _rememberSearches = new();
    // Appearance
    readonly ToggleSwitch _filtersBar = new(), _statusBar = new();
    SettingCard? _fontCard;
    // Defaults
    readonly ComboBox _defaultSort = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ToggleSwitch _foldersFirst = new();
    readonly ToggleSwitch _matchCase = new(), _wholeWord = new(), _matchPath = new(), _diacritics = new(), _regex = new();
    readonly ToggleSwitch _expandTree = new(), _hiddenFiles = new(), _usbPrompt = new(), _frequentFolders = new();
    readonly List<(string Key, ToggleSwitch Switch)> _navRoots = [];
    // Thumbnails
    readonly ToggleSwitch _everydayOnly = new();
    readonly List<(EverydayFiles.Kind Kind, ToggleSwitch Switch)> _everydayKinds = [];
    readonly TextBox _everydayAdded = new();
    // Indexing
    readonly ToggleSwitch _ntfs = new(), _removable = new(), _network = new();
    readonly ToggleSwitch _dateAccessed = new(), _folderSizes = new(), _recentChanges = new(), _recentFromJournal = new(), _runHistory = new();
    readonly ComboBox _rescan = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly RowList _driveRows = new();
    readonly NoteLabel _alsoSearched = new();
    readonly List<(DriveEntry Drive, ToggleSwitch Switch, SubRow Row)> _drives = [];
    readonly System.Windows.Forms.Timer _rootsTimer = new() { Interval = 2000 };
    readonly ToolTip _tips = new();
    IReadOnlyList<DriveEntry>? _driveList;     // the drives the rows show; null until listed
    IReadOnlyList<IndexedRootInfo> _roots = []; // what the index holds now, from the last look
    uint _driveMask;                           // the drive letters there were then
    Task? _rootsTask;
    // Folders
    readonly FolderList _include = new(), _exclude = new();
    readonly TextBox _patterns = new();
    readonly ToggleSwitch _exHidden = new(), _exSystem = new();
    // Keyboard
    readonly HotkeyBox _hotkey = new();
    readonly ToggleSwitch _hotkeyWin = new();
    // About
    SettingCard? _uninstallCard;
    Button? _uninstallButton;
    Action? _uninstallAction; // what the button does, from the last look (RefreshUninstallCard)
    int _uninstallLook;       // that look's number: an earlier one that answers later is dropped

    static Color Back => ExplorerTheme.Dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xF3, 0xF3, 0xF3);
    internal static Color CardFill => ExplorerTheme.Dark ? Color.FromArgb(0x2B, 0x2B, 0x2B) : Color.FromArgb(0xFB, 0xFB, 0xFB);
    internal static Color CardBorder => ExplorerTheme.Dark ? Color.FromArgb(0x1D, 0x1D, 0x1D) : Color.FromArgb(0xE5, 0xE5, 0xE5);
    static Color NavSelected => ExplorerTheme.Dark ? Color.FromArgb(0x2D, 0x2D, 0x2D) : Color.FromArgb(0xEA, 0xEA, 0xEA);

    int S(int v) => LogicalToDeviceUnits(v);

    public SettingsForm(AnythingApp app, string? section = null)
    {
        _app = app;
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Text = app.IsDemo ? "Rock Preview Settings (Demo)" : "Rock Preview Settings";
        Icon = app.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = !Application.OpenForms.Cast<Form>().Any(f => f.Visible && f != this);
        MinimizeBox = false;
        KeyPreview = true;
        BackColor = Back;
        ForeColor = ExplorerTheme.Text;
        ApplyFonts();
        ClientSize = new Size(S(940), S(660));
        MinimumSize = new Size(S(640), S(420));

        AddSection("General", '', GeneralPage());
        AddSection("Appearance", '', AppearancePage());
        AddSection("Defaults", '', DefaultsPage());
        AddSection("Thumbnails", ExplorerTheme.GGrid, ThumbnailsPage());
        AddSection("Indexing", '', IndexingPage());
        AddSection("Folders", '', FoldersPage());
        AddSection("Keyboard", '', KeyboardPage());
        AddSection("About", '', AboutPage());

        _nav.BackColor = Back;
        _nav.ForeColor = ExplorerTheme.Text;
        _nav.Width = S(280);
        _nav.ItemHeight = S(40);
        foreach (var (name, _, _) in _sections) _nav.Items.Add(name);
        _nav.DrawItem += DrawNavItem;
        _nav.SelectedIndexChanged += (_, _) => ShowSection(_nav.SelectedIndex);
        _content.BackColor = Back;
        _content.Padding = new Padding(S(8), 0, S(28), S(24));
        _content.Resize += (_, _) => { if (_nav.SelectedIndex >= 0) _sections[_nav.SelectedIndex].Page.PerformLayout(); };
        var navHost = new Panel { Dock = DockStyle.Left, Width = S(296), Padding = new Padding(S(12), S(16), S(4), S(8)), BackColor = Back };
        navHost.Controls.Add(_nav);
        _nav.Dock = DockStyle.Fill;
        Controls.Add(_content);
        Controls.Add(navHost);

        LoadValues();
        ResumeLayout(false);
        int start = section is null ? 0 : Math.Max(0, _sections.FindIndex(s => s.Name.Equals(section, StringComparison.OrdinalIgnoreCase)));
        _nav.SelectedIndex = start;

        _hotkey.Enter += (_, _) => _app.SuspendHotkey();
        _hotkey.Leave += (_, _) => ApplyHotkey();
        _rootsTimer.Tick += (_, _) => { if (IndexingShown) RefreshRoots(); };
        FormClosed += (_, _) =>
        {
            _rootsTimer.Stop();
            ApplyHotkey();
            if (_indexChanged) _app.ApplySettings(indexChanged: true);
            if (_contentChanged) _app.ApplyContentSettings();
            _app.RegisterHotkey();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ExplorerTheme.ApplyFrame(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RefreshRoots(); // the drives and their item counts, also for when Indexing is opened later
        RefreshUninstallCard();
        _rootsTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rootsTimer.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape && !_hotkey.Focused) Close();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyFonts();
        _nav.ItemHeight = S(40);
    }

    void ApplyFonts()
    {
        var old = Font;
        Font = new Font(ExplorerTheme.UiFamily, S(14), FontStyle.Regular, GraphicsUnit.Pixel);
        if (!ReferenceEquals(old, DefaultFont)) old.Dispose();
    }

    // ---- sections -----------------------------------------------------------------------------

    void AddSection(string name, char glyph, PageStack page)
    {
        page.Visible = false;
        _content.Controls.Add(page);
        _sections.Add((name, glyph, page));
    }

    void ShowSection(int index)
    {
        if (index < 0) return;
        _content.SuspendLayout();
        for (int i = 0; i < _sections.Count; i++) _sections[i].Page.Visible = i == index;
        _sections[index].Page.PerformLayout(); // its cards measured while hidden, at another width
        _content.ResumeLayout();
        _content.AutoScrollPosition = Point.Empty;
        if (_sections[index].Name == "Folders") _app.Tried(GettingStarted.Folders);
        if (_sections[index].Name == "About") RefreshUninstallCard();
        if (IndexingShown) RefreshRoots();
        if (ThumbnailsShown) ThumbnailsPageShown();
        else _thumbStoreTimer.Stop();
    }

    bool IndexingShown => _nav.SelectedIndex >= 0 && _sections[_nav.SelectedIndex].Name == "Indexing";

    /// <summary>Goes to the section named <paramref name="name"/> (Settings already open, asked for at a section); unknown names change nothing.</summary>
    internal void ShowSectionNamed(string name)
    {
        int index = _sections.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _nav.SelectedIndex = index;
    }

    void DrawNavItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var g = e.Graphics;
        using (var back = new SolidBrush(Back)) g.FillRectangle(back, e.Bounds);
        bool selected = (e.State & DrawItemState.Selected) != 0;
        var pill = new Rectangle(e.Bounds.X, e.Bounds.Y + S(2), e.Bounds.Width - S(4), e.Bounds.Height - S(4));
        if (selected)
        {
            ExplorerTheme.FillRounded(g, pill, S(4), NavSelected);
            ExplorerTheme.FillRounded(g, new Rectangle(pill.X, pill.Y + pill.Height / 2 - S(8), S(3), S(16)), S(2), ExplorerTheme.Accent);
        }
        var (name, glyph, _) = _sections[e.Index];
        ExplorerTheme.DrawGlyph(g, glyph, new Rectangle(pill.X + S(14), pill.Y, S(18), pill.Height), S(16), ExplorerTheme.Text);
        TextRenderer.DrawText(g, name, Font, new Rectangle(pill.X + S(46), pill.Y, pill.Width - S(50), pill.Height), ExplorerTheme.Text,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    PageStack Page(string title, params Control[] items)
    {
        var page = new PageStack
        {
            Dock = DockStyle.Top,
            BackColor = Back,
            Padding = new Padding(S(16), S(12), S(4), 0),
        };
        page.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(ExplorerTheme.UiFamily, S(28), FontStyle.Bold, GraphicsUnit.Pixel),
            Margin = new Padding(0, 0, 0, S(20)),
            ForeColor = ExplorerTheme.Text,
        });
        foreach (var c in items)
        {
            if (c is SettingCard) c.Margin = new Padding(0, 0, 0, S(4));
            page.Controls.Add(c);
        }
        return page;
    }

    Label Header(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(ExplorerTheme.UiFamily, S(14), FontStyle.Bold, GraphicsUnit.Pixel),
        Margin = new Padding(0, S(18), 0, S(8)),
        ForeColor = ExplorerTheme.Text,
    };

    SettingCard Card(string title, string description, Control? action = null, Control? body = null) => new(title, description, action, body);

    Button CardButton(string text, Action click)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(8), S(2), S(8), S(2)), UseVisualStyleBackColor = true };
        b.Click += (_, _) => click();
        return b;
    }

    // ---- pages --------------------------------------------------------------------------------

    PageStack GeneralPage()
    {
        string key = _app.ActiveHotkey ?? "the hotkey";
        _replaceExplorer.Enabled = _checkUpdates.Enabled = _autoUpdate.Enabled = !_app.IsDemo;
        // The Store's copy leaves File Explorer as it is, and the Store updates it.
        bool store = StorePackage.IsInstalled;
        var cards = new List<Control>
        {
            Card("Start when Windows starts", store
                ? $"So {key} opens Rock Preview any time. Task Manager › Startup apps can turn it off too."
                : $"So {key} opens Rock Preview any time.", _startWithWindows),
            Card("Keep running in the background", $"Closing the window keeps it ready for {key}, with your searches as you left them.", _runInBackground),
            Card("Show the tray icon", "An icon by the clock to open it, or to exit.", _showTray),
        };
        if (!store)
            cards.Add(Card("Use instead of Windows File Explorer",
                "Folders, drives, Win+E, This PC and File Explorer's taskbar button open Rock Preview. " +
                "Turn it off to get File Explorer back exactly as it was.", _replaceExplorer));
        cards.Add(Card("Open folders in tabs", store
            ? "A folder opened from the taskbar button's list, or a command line, opens as a new tab in the window you used last. Off: each opens in a new window."
            : "A folder opened from Windows, or Win+E, opens as a new tab in the window you used last. Off: each opens in a new window.", _openInTabs));
        cards.Add(SearchHistoryCard());
        if (!store)
        {
            cards.Add(Card("Tell me when a new version is out",
                "Once a day it asks GitHub for the latest version. Nothing about you or your files is sent.", _checkUpdates));
            cards.Add(Card("Update automatically",
                "A new version installs by itself every night at the time set, even with Rock Preview open (it opens again " +
                "after); if the PC is off then, the next time it starts. It also installs whenever Rock Preview is closed to " +
                "the background. Your settings and index are kept.", AutoUpdateControls()));
        }
        return Page("General", [.. cards]);
    }

    readonly DateTimePicker _autoUpdateAt = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "h:mm tt", ShowUpDown = true };

    /// <summary>The nightly update's time (11:00 PM unless changed) beside its switch.</summary>
    Control AutoUpdateControls()
    {
        _autoUpdateAt.Width = S(110);
        _autoUpdateAt.AccessibleName = "Update every night at";
        _autoUpdateAt.Margin = new Padding(0, S(2), S(12), 0);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        row.Controls.Add(new Label { Text = "Every night at", AutoSize = true, Margin = new Padding(0, S(6), S(6), 0), BackColor = Color.Transparent });
        row.Controls.Add(_autoUpdateAt);
        row.Controls.Add(_autoUpdate);
        return row;
    }

    /// <summary>Remember searches, with a button that forgets them now.</summary>
    SettingCard SearchHistoryCard()
    {
        var rows = new RowList();
        _clearSearches = CardButton("Clear", ClearSearchHistory);
        _clearSearches.AccessibleName = "Clear search history";
        rows.Controls.Add(new SubRow("Clear search history", _clearSearches, detail: "")); // the words the list's own last line uses
        // The address bar's typed addresses (its right-click menu's Delete history), here too so the keyboard reaches it.
        _clearAddresses = CardButton("Clear", ClearTypedAddresses);
        _clearAddresses.AccessibleName = "Clear typed addresses";
        rows.Controls.Add(new SubRow("Clear typed addresses", _clearAddresses,
            detail: "File Explorer's own typed addresses are kept"));
        UpdateClearSearches();
        return Card("Remember searches",
            "Keep your last 20 searches: pressing Enter or opening a result remembers one. The arrow at the end of the search box, or Alt+Down, lists them.",
            _rememberSearches, rows);
    }

    Button? _clearSearches, _clearAddresses;

    void ClearTypedAddresses()
    {
        _app.Settings.TypedAddresses.Clear();
        _app.Settings.Save();
        UpdateClearSearches();
    }

    /// <summary>Clear is there to press only while searches are remembered and there are some, so pressing it shows it worked.</summary>
    void UpdateClearSearches()
    {
        if (_clearSearches is { IsDisposed: false } b) b.Enabled = _app.Settings.RememberSearches && _app.Settings.SearchHistory.Count > 0;
        if (_clearAddresses is { IsDisposed: false } a) a.Enabled = _app.Settings.TypedAddresses.Items.Count > 0;
    }

    void ClearSearchHistory()
    {
        if (_app.Settings.SearchHistory.Clear()) _app.Settings.Save();
        MainForm.SearchHistoryChanged(_app);
        UpdateClearSearches();
    }

    PageStack AppearancePage()
    {
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        buttons.Controls.Add(CardButton("Choose...", ChooseFont));
        buttons.Controls.Add(CardButton("Default", () => { _app.Settings.FontName = ""; _app.Settings.FontSize = 0; ResultsFontChanged(); }));
        _fontCard = Card("Font for the results", "", buttons);
        return Page("Appearance",
            Card("Filters bar", "A row of filters above the results: audio, pictures, documents and more.", _filtersBar),
            Card("Status bar", "How many items there are, and what is selected, at the bottom of the window.", _statusBar),
            _fontCard);
    }

    /// <summary>
    /// What every new window and tab starts with, in one place. Changed here, every open tab takes it too (Sort and
    /// Search options still change a tab on its own).
    /// </summary>
    PageStack DefaultsPage()
    {
        _defaultSort.Width = S(240);
        _defaultSort.AccessibleName = "Sort results by";
        foreach (var c in SortChoices) _defaultSort.Items.Add(c);
        Control[] cards =
        [
            Header("Search results"),
            Card("Sort by", "How results are ordered when a search starts.", _defaultSort),
            Card("Folders first", "Folders found come before files, each in the order above.", _foldersFirst),
            Card("Match whole words", "\"port\" finds port, not report.", _wholeWord),
            Card("Match case", "\"Report\" finds Report, not report.", _matchCase),
            Card("Match the full path", "Search words can match folder names on the way to a file.", _matchPath),
            Card("Match accents", "\"resume\" finds resume, not résumé.", _diacritics),
            Card("Regular expressions", "Search with patterns like ^IMG_\\d+\\.jpg$.", _regex),
            ContentSearchDefaultCard(),
            Header("Folders"),
            Card("Expand the folder tree to the open folder",
                "Off, the tree on the left stays as you leave it: opening Desktop doesn't unfold This PC down to it.", _expandTree),
            Card("Show hidden files and folders", "The same setting as File Explorer's View › Show › Hidden items.", _hiddenFiles),
            Header("Navigation pane"),
            Card("Folders Windows adds to Quick access",
                "Off, Quick access shows only the folders you pinned. Right-click a pinned folder to unpin it.", _frequentFolders),
            .. NavRootCards(),
            Header("USB drives"),
            Card("Ask to open a USB drive when it's plugged in",
                "Rock Preview asks, with one click to open it, instead of Windows' own AutoPlay.", _usbPrompt),
        ];
        return Page("Defaults", cards);
    }

    /// <summary>A switch for each place the pane can show (also taken out with a right-click on it there).</summary>
    IEnumerable<Control> NavRootCards()
    {
        foreach (var (key, name) in Anything.Explorer.NavigationTree.RemovableRoots)
        {
            var sw = new ToggleSwitch();
            _navRoots.Add((key, sw));
            yield return Card($"Show {name}", key == "linux" ? "Shown when WSL has Linux installed." : $"{name} in the navigation pane. Right-click it there to take it out.", sw);
        }
    }

    /// <summary>The orders Settings › Defaults offers.</summary>
    static readonly SortChoice[] SortChoices =
    [
        new("Date modified, newest first", SortKey.DateModified, true),
        new("Date modified, oldest first", SortKey.DateModified, false),
        new("Name, A to Z", SortKey.Name, false),
        new("Name, Z to A", SortKey.Name, true),
        new("Size, largest first", SortKey.Size, true),
        new("Size, smallest first", SortKey.Size, false),
        new("Type", SortKey.Type, false),
        new("Path", SortKey.Path, false),
        new("Date created, newest first", SortKey.DateCreated, true),
        new("Date accessed, newest first", SortKey.DateAccessed, true),
    ];

    sealed record SortChoice(string Name, SortKey Key, bool Descending)
    {
        public override string ToString() => Name;
    }

    PageStack ThumbnailsPage()
    {
        _everydayAdded.Width = S(260);
        _everydayAdded.PlaceholderText = "md; json";
        _everydayAdded.Leave += (_, _) => EverydayChanged();
        var cards = new List<Control>();
        // First on the page: the one-time bar's "Turn on..." opens Settings here. The demo keeps nothing on disk.
        if (!_app.IsDemo) cards.Add(ThumbStoreCard());
        cards.AddRange(new List<Control>
        {
            Card("Everyday files only",
                "Thumbnails show only the kinds of files switched on below: every file in a folder, and what a search finds. " +
                "Off: every file, code, settings and program files too.", _everydayOnly),
            Header("Everyday files are"),
        });
        foreach (var kind in EverydayFiles.Kinds)
        {
            var on = new ToggleSwitch();
            _everydayKinds.Add((kind, on));
            cards.Add(Card(kind.Name, kind.Examples, on));
        }
        cards.Add(Card("Also these extensions", "Separated by spaces or semicolons. For example md; json", _everydayAdded));
        cards.Add(Card("Back to the usual", "Every kind above on, nothing added.", CardButton("Reset", ResetEveryday)));
        return Page("Thumbnails", [.. cards]);
    }

    PageStack IndexingPage()
    {
        var index = _app.IndexManager;
        bool service = !_app.IsDemo && IndexService.IsInstalled();
        // An older service (setup's update declined, say) still works, but without this release's fixes.
        bool outdated = service && IndexService.NeedsUpdate();
        // The Store's copy never installs or updates the service (a package can't leave one behind; one Setup installed
        // still serves it): run as administrator, it reads the drives itself.
        bool store = StorePackage.IsInstalled;
        string status = index.UsesService && outdated && !store ? "On, through an older version of the Rock Preview Service. Update it to this one for its fixes; Windows asks for permission once."
            : index.UsesService ? "On: the Rock Preview Service reads your drives, so every change shows up at once."
            : index.IsElevated ? "On, because Rock Preview runs as administrator."
            : store ? "Off: folders are scanned instead, which is slower. Run Rock Preview as administrator to read drives instantly; Windows asks for permission each time."
            : service && !index.ScansInsteadOfReading ? "Ready: the Rock Preview Service is installed. None of the drives searched now is one it reads (a local NTFS drive); network, FAT32 and exFAT drives are scanned."
            : service && outdated ? "The Rock Preview Service isn't answering. Updating it usually brings it back; Windows asks for permission once."
            : service ? "The Rock Preview Service isn't answering. Restarting the PC usually brings it back."
            : "Off: folders are scanned instead, which is slower. Windows asks for permission once to turn it on.";
        bool offer = !_app.IsDemo && (store ? !index.UsesService && !index.IsElevated : !service || (outdated && (index.UsesService || !index.IsElevated)));
        Control? turnOn = !offer ? null : CardButton(store ? "Run as administrator" : service ? "Update" : "Turn on", () => { Close(); _app.Post(() => _app.TurnOnInstantIndexing(null)); });

        // The drives the app listed last show at once; the look in the background (RefreshRoots) brings them up to date.
        if (_app.KnownDrives.Count > 0) ShowDrives(_app.KnownDrives);
        else _driveRows.Controls.Add(new NoteLabel { Text = "Looking for drives..." });

        _rescan.Width = S(200);
        _rescan.AccessibleName = "Check network drives again";
        foreach (int minutes in RescanChoices) _rescan.Items.Add(new RescanChoice(minutes));
        var rescanRow = new RowList();
        rescanRow.Controls.Add(new SubRow("Check them again", _rescan, detail: "Network drives and folders don't always say when files change."));

        var journalRow = new RowList();
        journalRow.Controls.Add(new SubRow("Reach back with the change journal", _recentFromJournal,
            detail: "At startup, NTFS drives tell what changed in the days before."));
        var runRows = new RowList();
        var import = CardButton("Import", ImportRunHistory);
        var clear = CardButton("Clear", ClearRunHistory);
        import.Enabled = clear.Enabled = _runHistory.Enabled = !_app.IsDemo;
        runRows.Controls.Add(new SubRow("Bring in what Everything counted", import, detail: "Everything's Run History.csv, if it's on this PC."));
        runRows.Controls.Add(new SubRow("Forget every count", clear, detail: ""));

        return Page("Indexing", [
            Card("Instant indexing", status, turnOn),
            Card("Index whole drives", "Search every drive. NTFS drives are read in seconds and kept up to date as files change; others are scanned.", _ntfs, _driveRows),
            Card("USB drives and memory cards",
                "Search them, and CDs and DVDs, while they're plugged in. NTFS drives are read instantly; others are scanned. They can still be safely removed.", _removable),
            Card("Network drives",
                "Mapped network drives (like Z:) are scanned and kept up to date. Their files stay searchable while the network is away. " +
                "They're searched by name; to search inside their files too, switch on Network drives under Search inside files.", _network, rescanRow),
            Header("Information about each file"),
            Card("Date accessed",
                "Keep when each file was last opened, for instant da: searches and sorting. It takes 8 bytes per file and folder (27 MB for 3.2 million). " +
                "Off: the Date Accessed column reads it from the disk, which is slower.", _dateAccessed),
            Card("Folder sizes", "Folders show the size of everything in them, kept up to date as files change. size: finds folders too.", _folderSizes),
            Card("Recent changes",
                "Remember files created, changed, moved or renamed: the Date Recently Changed column and rc:today.", _recentChanges, journalRow),
            Card("Run history",
                "Count the files you open from the results: the Run Count and Date Run columns, runcount: and dr:.", _runHistory, runRows),
            Card("Rebuild the index", "Scans everything again from the start.", CardButton("Rebuild now", () => { _indexChanged = false; _app.ApplySettings(indexChanged: true, rebuildIndex: true); })),
            .. ContentCards()]);
    }

    void ImportRunHistory()
    {
        int n = _app.ImportEverythingRunHistory();
        string text = n < 0 ? "Everything's run history isn't on this PC."
            : n == 0 ? "Nothing new: every file Everything counted is counted here already."
            : n == 1 ? "1 file's count came in from Everything." : $"{n:N0} files' counts came in from Everything.";
        MessageBox.Show(this, text, "Run history", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void ClearRunHistory()
    {
        var history = _app.IndexManager.Index.RunHistory;
        if (history.Count == 0)
        {
            MessageBox.Show(this, "No file has been counted yet.", "Run history", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"Forget how often {history.Count:N0} files were opened?", "Run history", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            history.Clear();
    }

    /// <summary>
    /// One row per drive: local drives, then USB drives and discs, then network drives. SUBST drives repeat another
    /// drive's files (indexed there) and aren't listed.
    /// </summary>
    void ShowDrives(IReadOnlyList<DriveEntry> drives)
    {
        _driveList = drives;
        var ix = _app.Settings.Index;
        _driveRows.SuspendLayout();
        _driveRows.Controls.Remove(_alsoSearched);
        while (_driveRows.Controls.Count > 0) _driveRows.Controls[0].Dispose();
        _drives.Clear();
        var shown = drives.Where(d => d.SubstTarget == null)
            .OrderBy(d => d.Kind switch { DriveKind.Removable or DriveKind.Optical => 1, DriveKind.Network => 2, _ => 0 })
            .ThenBy(d => d.IsDriveLetter ? 0 : 1).ThenBy(d => d.Root, StringComparer.OrdinalIgnoreCase);
        foreach (var d in shown)
        {
            var drive = d;
            var sw = new ToggleSwitch { Checked = !TurnedOff(ix, d), AccessibleName = DriveName(d.Root) };
            sw.CheckedChanged += (_, _) => DrivesChanged();
            var row = new SubRow(DriveLabel(d), sw, CardButton("Include it", () => IncludeDrive(drive)), detail: "");
            _drives.Add((d, sw, row));
            _driveRows.Controls.Add(row);
        }
        if (_drives.Count == 0) _driveRows.Controls.Add(new NoteLabel { Text = "No drives found." });
        _driveRows.Controls.Add(_alsoSearched);
        _driveRows.ResumeLayout();
        UpdateDriveRows();
        _driveRows.Parent?.PerformLayout();
        _driveRows.Parent?.Parent?.PerformLayout();
    }

    /// <summary>"C:  Windows  (NTFS)", "E:  USB Drive  (exFAT)", "Z:  Network Drive  (NTFS)  \\server\share".</summary>
    static string DriveLabel(DriveEntry d)
    {
        string kind = d.Kind switch
        {
            DriveKind.Fixed => "Local Disk",
            DriveKind.Removable => "USB Drive",
            DriveKind.Optical => "CD/DVD Drive",
            DriveKind.Network => "Network Drive",
            DriveKind.Ram => "RAM Disk",
            _ => "Drive",
        };
        string text = $"{DriveName(d.Root)}  {(string.IsNullOrWhiteSpace(d.Label) ? kind : d.Label.Trim())}";
        if (d.FileSystem.Length > 0) text += $"  ({d.FileSystem})";
        if (d.NetworkPath is { } share && d.IsDriveLetter) text += $"  {share}"; // a share listed by itself shows it already
        return text;
    }

    /// <summary>"C:" for a drive letter, "C:\Mount\Data" for a volume mounted in a folder.</summary>
    static string DriveName(string root) => root.Length == 3 && root[1] == ':' ? root[..2] : root.TrimEnd('\\');

    /// <summary>
    /// The drive's own switch is off (or, from 1.4.0's settings, it isn't on the list of drives to index), the way the
    /// index's plan sees it: asked with only the drive switches set, so nothing else (all drives off, a folder left
    /// out) gets in the way.
    /// </summary>
    static bool TurnedOff(IndexSettings ix, DriveEntry d) =>
        IndexPlan.ForDrive(new IndexSettings { SkipDrives = ix.SkipDrives, Volumes = ix.Volumes }, d with { SubstTarget = null }) == DriveIndexing.TurnedOff;

    /// <summary>USB drives (and discs) or network drives are switched off as a group.</summary>
    static bool KindOff(IndexSettings ix, DriveEntry d) => d.Kind switch
    {
        DriveKind.Removable or DriveKind.Optical => !ix.IndexRemovableDrives,
        DriveKind.Network => !ix.IndexNetworkDrives,
        _ => false,
    };

    /// <summary>Each drive's line under its name: what it holds, or why it isn't searched. The same plan the index uses.</summary>
    void UpdateDriveRows()
    {
        var ix = _app.Settings.Index;
        var filter = new ExclusionFilter(ix);
        var plans = _drives.Select(d => IndexPlan.ForDrive(ix, d.Drive, filter)).ToList();
        // Two letters mapped to one share are searched once, under the first.
        var shares = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (d, plan) in _drives.Select((d, i) => (d.Drive, plans[i])).OrderBy(x => x.Drive.IsDriveLetter ? 0 : 1).ThenBy(x => x.Drive.Root, StringComparer.OrdinalIgnoreCase))
            if (plan == DriveIndexing.Indexed && d.NetworkPath is { } share) shares.TryAdd(share, d.Root);

        for (int i = 0; i < _drives.Count; i++)
        {
            var (drive, _, row) = _drives[i];
            var info = _roots.FirstOrDefault(r => AppSettings.SamePath(r.Path, drive.Root));
            // Left out by a folder it is mounted in, but on Also search these folders itself ("Include it"): searched.
            bool asFolder = plans[i] == DriveIndexing.LeftOut && _app.Settings.IsSearchedAsFolder(drive.Root);
            string text = plans[i] switch
            {
                DriveIndexing.Indexed when drive.NetworkPath is { } share && shares.TryGetValue(share, out var first) && first != drive.Root =>
                    $"Searched as {DriveName(first)}, the same share",
                DriveIndexing.Indexed => IndexedStatus(drive, info),
                DriveIndexing.TurnedOff => "Turned off",
                DriveIndexing.KindOff => drive.Kind switch { DriveKind.Network => "Network drives are off", DriveKind.Optical => "CDs and DVDs are off", _ => "USB drives are off" },
                DriveIndexing.LeftOut when asFolder => IndexedStatus(drive, info),
                DriveIndexing.LeftOut => LeftOutStatus(drive),
                DriveIndexing.NotReady => "Locked or empty",
                _ => "", // all drives are off: the main switch says so
            };
            row.SetDetail(text);
            if (row.DetailLabel is { } detail) _tips.SetToolTip(detail, info?.Detail);
            row.ShowExtra(plans[i] == DriveIndexing.LeftOut && !asFolder && _app.Settings.ExclusionsHidingDrive(drive.Root).Count > 0);
        }
        UpdateAlsoSearched();
        UpdateDrivesEnabled();
    }

    /// <summary>For a drive that is searched: how many items it holds, or where it stands.</summary>
    string IndexedStatus(DriveEntry drive, IndexedRootInfo? info)
    {
        if (info is null)
        {
            if (!drive.IsReady) return "Offline: searched once it's back"; // a network drive that is away
            if (_roots.Count == 0) return ""; // nothing to tell yet (or the demo)
            return _indexChanged ? "Added when you close Settings" : "Waiting...";
        }
        return info.Status switch
        {
            RootStatus.Waiting => "Waiting...",
            RootStatus.Scanning => (info.Method == "scan" ? "Scanning..." : "Reading...") + (info.Items > 1 ? $" {Items(info.Items)} so far" : ""),
            RootStatus.Offline => info.Items > 1 ? $"Offline: searches show the {Items(info.Items)} it had" : "Offline: searched once it's back",
            _ => Items(info.Items),
        };
    }

    static string Items(int n) => n == 1 ? "1 item" : $"{n:N0} items";

    string LeftOutStatus(DriveEntry drive)
    {
        var hiding = _app.Settings.ExclusionsHidingDrive(drive.Root);
        if (hiding.Count == 0) return "Left out: its folder matches Folders › Leave out files named like"; // a volume mounted in a folder
        return hiding.Any(h => AppSettings.SamePath(h, drive.Root))
            ? "Left out: it's on Folders › Leave these folders out"
            : $"Left out: {hiding[0].Trim()} is on Folders › Leave these folders out";
    }

    /// <summary>Under the drives: the folders searched besides them (Folders › Also search these folders).</summary>
    void UpdateAlsoSearched()
    {
        var folders = _roots.Where(r => !_drives.Any(d => AppSettings.SamePath(d.Drive.Root, r.Path)
                                                          || (d.Drive.NetworkPath is { } share && AppSettings.SamePath(share, r.Path)))).ToList();
        string text = folders.Count == 0 ? "" : "Also searched: " + string.Join("  ·  ", folders.Select(r => r.Path.TrimEnd('\\') + " (" + (r.Status switch
        {
            RootStatus.Waiting => "waiting",
            RootStatus.Scanning => "scanning",
            RootStatus.Offline => "offline",
            _ => Items(r.Items),
        }) + ")"));
        if (_alsoSearched.Text == text) return;
        var width = new Size(_driveRows.ClientSize.Width, 0);
        int height = _alsoSearched.GetPreferredSize(width).Height;
        _alsoSearched.Text = text;
        if (_alsoSearched.GetPreferredSize(width).Height == height) return;
        _driveRows.PerformLayout();
        _driveRows.Parent?.PerformLayout();
        _driveRows.Parent?.Parent?.PerformLayout();
    }

    /// <summary>
    /// "Include it": takes the drive off Folders › Leave these folders out, or, when a folder it's mounted in is what's
    /// left out, adds it to Also search these folders (the rest of that folder stays left out).
    /// </summary>
    void IncludeDrive(DriveEntry drive)
    {
        if (!_app.Settings.IncludeDrive(drive.Root)) return;
        _exclude.Folders = _app.Settings.Index.ExcludeFolders;
        _include.Folders = _app.Settings.Index.Folders;
        IndexChanged();
    }

    /// <summary>
    /// Asks the index what each drive and folder holds now (it walks the whole index, so in the background), and lists
    /// the drives again when a drive letter came or went. One look at a time: every 2 seconds while Indexing shows.
    /// </summary>
    void RefreshRoots()
    {
        RefreshContent();
        if (_rootsTask is { IsCompleted: false } || IsDisposed || !IsHandleCreated) return;
        var index = _app.IndexManager;
        uint mask = Drives.LetterMask();
        bool listDrives = _driveList is null || mask != _driveMask;
        _rootsTask = Task.Run(() =>
        {
            IReadOnlyList<IndexedRootInfo> roots;
            try { roots = index.GetRoots(); }
            catch (Exception) { roots = []; }
            IReadOnlyList<DriveEntry>? drives = null;
            if (listDrives)
            {
                try { drives = Drives.List(); }
                catch (Exception) { }
            }
            try { BeginInvoke(() => ShowRoots(roots, drives, mask)); }
            catch (InvalidOperationException) { } // the window closed meanwhile
        });
    }

    void ShowRoots(IReadOnlyList<IndexedRootInfo> roots, IReadOnlyList<DriveEntry>? drives, uint mask)
    {
        if (IsDisposed) return;
        _roots = roots;
        if (drives is not null)
        {
            _driveMask = mask;
            if (_driveList is null || !drives.SequenceEqual(_driveList))
            {
                ShowDrives(drives); // also updates the rows
                return;
            }
        }
        UpdateDriveRows();
    }

    PageStack FoldersPage()
    {
        _include.Changed += () => { _app.Settings.Index.Folders = _include.Folders; IndexChanged(); };
        _exclude.Changed += () => { _app.Settings.Index.ExcludeFolders = _exclude.Folders; IndexChanged(); };
        _exclude.Confirm = ConfirmLeaveOut;
        _patterns.Width = S(260);
        _patterns.PlaceholderText = "*.tmp; ~$*";
        _patterns.Leave += (_, _) => PatternsChanged();
        var includeButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        includeButtons.Controls.Add(CardButton("Type a path...", AddTypedFolder));
        includeButtons.Controls.Add(CardButton("Add a folder", () => _include.AddWithPicker(this, "Search this folder too")));
        return Page("Folders",
            Card("Also search these folders",
                @"Folders on other drives, USB sticks or the network (\\server\share). They're searched even inside a drive or folder you leave out.",
                includeButtons, _include),
            Card("Leave these folders out", "Nothing inside them shows up in searches. Thumbnails on one of them reads it from the disk.",
                CardButton("Add a folder", () => _exclude.AddWithPicker(this, "Leave this folder out")), _exclude),
            Card("Leave out files named like", "Wildcards, separated by semicolons. For example *.tmp; ~$*", _patterns),
            Card("Leave out hidden files and folders", "Files Windows marks as hidden.", _exHidden),
            Card("Leave out system files and folders", "Windows' own protected files.", _exSystem));
    }

    PageStack KeyboardPage()
    {
        _hotkey.Width = S(180);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        _hotkey.Margin = new Padding(0, S(3), S(8), 0);
        row.Controls.Add(_hotkey);
        row.Controls.Add(CardButton($"Default ({AppSettings.DefaultHotkey})", () =>
        {
            Hotkey.TryParse(AppSettings.DefaultHotkey, out uint mods, out Keys key);
            _hotkey.Set(mods, key);
            _loading = true; _hotkeyWin.Checked = (mods & Native.MOD_WIN) != 0; _loading = false;
            ApplyHotkey();
        }));
        var cards = new List<Control>
        {
            Card("Open or hide from any app", "Click the box and press the keys you want. Backspace turns it off.", row),
            Card("Use the Windows key", "Adds the Windows logo key to the hotkey, for example Win+Alt+X.", _hotkeyWin),
        };
        if (_app.HotkeyError is { } error) cards.Add(Card("The hotkey isn't working", error));
        return Page("Keyboard", [.. cards]);
    }

    PageStack AboutPage()
    {
        var current = AnythingApp.CurrentVersion.ToString(3);
        return Page("About",
            Card("Rock Preview", $"Version {current}. Free software under the GNU GPL v3."),
            UpdatesCard(),
            Card("Get started", "The short tour: Alt+X, searching everything from a folder, Thumbnails, subfolders, sorting and choosing folders.", CardButton("Show", () => { Close(); _app.ShowGetStarted(); })),
            Card("Source code and releases", "github.com/RockySharaf/rock-preview", CardButton("Open", () => AnythingApp.OpenWebPage("https://github.com/" + Anything.Core.UpdateCheck.Repository))),
            UninstallCard());
    }

    SettingCard? _updatesCard;
    Button? _checkButton;
    readonly ProgressStrip _updateStrip = new();
    string? _updateReport; // what the last check said, while nothing else has happened since

    /// <summary>
    /// Updates, all in one place: Check for updates installs a newer version at once, and a strip under the card's words
    /// runs left to right while it does (Rock Preview then closes and opens again as the new version, and the card says
    /// it was updated). No message boxes, notifications or links: this card is where updating shows. A copy that can't
    /// update itself (not the installed one) gets a Download button to the new version instead.
    /// </summary>
    SettingCard UpdatesCard()
    {
        // The Store updates its copy: its page there shows a new version and installs it.
        if (StorePackage.IsInstalled)
            return Card("Updates", "The Microsoft Store keeps Rock Preview up to date.", CardButton("Open in Store", StorePackage.OpenStorePage));
        _checkButton = CardButton("Check for updates", () =>
        {
            if (_app.NewVersion is not null && !_app.CanUpdateInApp && _app.UpdateProgress is null) { _app.OpenReleasePage(); return; }
            _updateReport = null;
            _app.CheckForUpdatesInteractive(this, text => { _updateReport = text; ShowUpdateState(); });
        });
        _updatesCard = Card("Updates", "", _checkButton, _updateStrip);
        _app.UpdateStateChanged += ShowUpdateState;
        FormClosed += (_, _) => _app.UpdateStateChanged -= ShowUpdateState;
        ShowUpdateState();
        // Help › Check for Updates opens this page to check here.
        if (_app.TakeCheckOnOpen()) Shown += (_, _) => _checkButton.PerformClick();
        return _updatesCard;
    }

    string UpdatesText() => _app.UpdateProgress is { } p && _app.NewVersion is { } v
            ? (p < 1 ? $"Updating to Rock Preview {v.ToString(3)}... {(int)(p * 100)}%" : $"Installing Rock Preview {v.ToString(3)}: Rock Preview opens again in a moment.")
        : _app.UpdateError is { } error ? "Updating didn't work: " + error
        : _updateReport is { } report ? report
        : _app.JustUpdatedFrom is { } from ? $"Updated to Rock Preview {AnythingApp.CurrentVersion.ToString(3)} (from {from.ToString(3)})."
        // Where it updates itself, nothing is announced: it just happens.
        : _app.UpdatesItself ? "Rock Preview keeps itself up to date."
        : _app.NewVersion is { } n ? (_app.CanUpdateInApp
            ? $"Rock Preview {n.ToString(3)} is out: Check for updates installs it now."
            : $"Rock Preview {n.ToString(3)} is out. This copy isn't the installed one, so it can't update itself: Download gets it.")
        : "See if a newer version is out.";

    void ShowUpdateState()
    {
        if (IsDisposed || _updatesCard == null) return;
        _updatesCard.SetDescription(UpdatesText());
        _updateStrip.Value = _app.UpdateProgress;
        if (_checkButton != null)
        {
            _checkButton.Enabled = _app.UpdateProgress is null;
            _checkButton.Text = _app.NewVersion is not null && !_app.CanUpdateInApp ? "Download"
                : _app.UpdateError is not null ? "Try again" : "Check for updates";
        }
        _updatesCard.PerformLayout();
    }

    /// <summary>
    /// Uninstall in one click and one question: the installed copy, or for a copy that isn't installed (portable) Remove
    /// from this PC once it set Windows up for itself, which deleting its file would leave pointing at nothing. Filled in
    /// by <see cref="RefreshUninstallCard"/>.
    /// </summary>
    SettingCard UninstallCard()
    {
        // Windows uninstalls the Store's copy (Setup has nothing to remove).
        if (StorePackage.IsInstalled)
            return Card("Uninstall", "Uninstall Rock Preview in Settings › Apps › Installed apps, or right-click it in the Start menu.",
                CardButton("Open Settings", StorePackage.OpenInstalledApps));
        _uninstallButton = CardButton("Uninstall", () => _uninstallAction?.Invoke());
        _uninstallButton.Enabled = false;
        return _uninstallCard = Card("Uninstall", "", _uninstallButton);
    }

    /// <summary>
    /// Looks (off the UI thread: the registry, the program file, the service manager) at what Uninstall would remove, when
    /// the window opens and each time About is shown, since General and Indexing change what a portable copy set up.
    /// </summary>
    async void RefreshUninstallCard()
    {
        if (_uninstallCard is null || _uninstallButton is null || Environment.ProcessPath is not { } exe
            || Path.GetDirectoryName(exe) is not { Length: > 0 } own) return;
        int look = ++_uninstallLook;
        // The installed copy this one is first (the one in use when both a just-me and an everyone install exist). A copy
        // that isn't installed is looked for by its own file, whatever it is called: its settings point at that file. So
        // is this copy when it isn't the installed one (a portable copy run while an install exists): what it set up for
        // itself is left by the installed copy's uninstall, and the card says so.
        var (installed, footprint) = await Task.Run(() =>
        {
            var inst = Setup.FindInstalled(own) ?? Setup.FindInstalled();
            bool another = inst is not null && !Setup.SamePath(exe, Path.Combine(inst.Folder, Setup.ExeName));
            return (inst, inst is null || another ? Setup.FootprintOf(exe) : null);
        });
        if (IsDisposed || look != _uninstallLook) return;
        bool portable = installed is null && footprint is { Any: true };
        _uninstallButton.Text = portable ? "Remove from this PC" : "Uninstall";
        _uninstallButton.Enabled = installed is not null || portable;
        _uninstallAction = installed is not null ? () => Uninstall(installed.Folder, portable: false)
                         : portable ? () => Uninstall(own, portable: true)
                         : null;
        _uninstallCard.SetDescription(installed is not null
            ? UninstallRules.InstalledCardText(footprint is null ? null : exe, footprint?.Takeover == true, footprint?.StartsWithWindows == true)
            : UninstallRules.NotInstalledCardText(footprint?.Takeover == true, footprint?.StartsWithWindows == true,
                footprint?.Service ?? ServiceFor.NotThisUser, own));
    }

    void Uninstall(string folder, bool portable)
    {
        string what = portable ? "Remove from this PC" : "Uninstall";
        if (Setup.ConfirmUninstall(this, folder, portable) is not bool removeData) return;
        if (_app.IsDemo)
        {
            TaskDialog.ShowDialog(this, new TaskDialogPage { Caption = Text, Heading = portable ? "Nothing was removed" : "Nothing was uninstalled", Text = $"This is the demo: {what} works in the real app.", Icon = TaskDialogIcon.Information });
            return;
        }
        if (!Setup.StartUninstall(folder, removeData, portable))
        {
            TaskDialog.ShowDialog(this, new TaskDialogPage
            {
                Caption = Text,
                Heading = $"{what} couldn't be started",
                Text = portable
                    ? $"Before deleting {Path.GetFileName(Environment.ProcessPath) ?? Setup.ExeName}, turn off Use instead of Windows File Explorer and Start when Windows starts in Settings › General."
                    : "Uninstall Rock Preview from Settings › Apps instead.",
                Icon = TaskDialogIcon.Error,
            });
            return;
        }
        Close(); // the uninstall closes the app next
    }

    // ---- values -------------------------------------------------------------------------------

    void LoadValues()
    {
        _loading = true;
        var s = _app.Settings;
        _startWithWindows.Checked = s.StartWithWindows;
        _runInBackground.Checked = s.RunInBackground;
        _showTray.Checked = s.ShowTrayIcon;
        _replaceExplorer.Checked = s.UseInsteadOfExplorer;
        _openInTabs.Checked = s.OpenFoldersInTabs;
        _rememberSearches.Checked = s.RememberSearches;
        _checkUpdates.Checked = s.CheckForUpdates;
        _autoUpdate.Checked = s.AutoUpdate;
        _autoUpdateAt.Value = DateTime.Today + s.AutoUpdateAt;
        _autoUpdateAt.Enabled = s.AutoUpdate && !_app.IsDemo;
        _filtersBar.Checked = s.FiltersVisible;
        _statusBar.Checked = s.StatusBarVisible;
        UpdateFontCard();
        var sortChoice = SortChoices.FirstOrDefault(c => c.Key == s.Sort && c.Descending == s.SortDescending)
            ?? new SortChoice($"{s.Sort}, {(s.SortDescending ? "descending" : "ascending")}", s.Sort, s.SortDescending);
        if (!_defaultSort.Items.Contains(sortChoice)) _defaultSort.Items.Add(sortChoice);
        _defaultSort.SelectedItem = sortChoice;
        _foldersFirst.Checked = s.FoldersFirst;
        _expandTree.Checked = s.ExpandTreeToFolder;
        _hiddenFiles.Checked = ExplorerOptions.ShowHidden;
        _usbPrompt.Checked = s.UsbPrompt;
        _frequentFolders.Checked = !s.NavPinnedOnly;
        foreach (var (rootKey, sw) in _navRoots) sw.Checked = !s.NavHiddenRoots.Contains(rootKey, StringComparer.OrdinalIgnoreCase);
        _matchCase.Checked = s.MatchCase;
        _wholeWord.Checked = s.MatchWholeWord;
        _matchPath.Checked = s.MatchPath;
        _diacritics.Checked = s.MatchDiacritics;
        _regex.Checked = s.Regex;
        ShowEveryday(s);
        var ix = s.Index;
        _ntfs.Checked = ix.IndexNtfsVolumes;
        _removable.Checked = ix.IndexRemovableDrives;
        _network.Checked = ix.IndexNetworkDrives;
        SelectRescan(ix.NetworkRescanMinutes);
        UpdateDrivesEnabled();
        _include.Folders = ix.Folders;
        _exclude.Folders = ix.ExcludeFolders;
        _patterns.Text = string.Join("; ", ix.ExcludePatterns);
        _exHidden.Checked = ix.ExcludeHidden;
        _exSystem.Checked = ix.ExcludeSystem;
        _dateAccessed.Checked = ix.IndexDateAccessed;
        _folderSizes.Checked = ix.IndexFolderSizes;
        _recentChanges.Checked = ix.IndexRecentChanges;
        _recentFromJournal.Checked = ix.RecentChangesFromJournal;
        _recentFromJournal.Enabled = ix.IndexRecentChanges;
        _runHistory.Checked = s.RunHistoryEnabled;
        Hotkey.TryParse(s.Hotkey, out uint mods, out Keys key);
        _hotkey.Set(mods, key);
        _hotkeyWin.Checked = (mods & Native.MOD_WIN) != 0;
        _loading = false;

        foreach (var sw in new[] { _startWithWindows, _runInBackground, _showTray, _replaceExplorer, _openInTabs, _checkUpdates, _autoUpdate, _filtersBar, _statusBar,
                                   _foldersFirst, _matchCase, _wholeWord, _matchPath, _diacritics, _regex, _expandTree, _usbPrompt })
            sw.CheckedChanged += (_, _) => GeneralChanged();
        _defaultSort.SelectedIndexChanged += (_, _) => GeneralChanged();
        _autoUpdateAt.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            var at = new TimeSpan(_autoUpdateAt.Value.Hour, _autoUpdateAt.Value.Minute, 0);
            if (at == _app.Settings.AutoUpdateAt) return;
            _app.Settings.AutoUpdateAt = at;
            // A new time counts from now: not a night "missed" because the time moved behind the clock.
            _app.Settings.LastScheduledUpdate = AnythingApp.LastNightlySlot(DateTime.UtcNow, at);
            _app.Settings.Save();
            foreach (var w in _app.Windows) w.UpdateUpdateBar();
        };
        foreach (var sw in _navRoots.Select(r => r.Switch).Append(_frequentFolders)) sw.CheckedChanged += (_, _) => NavPaneChanged();
        _hiddenFiles.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            ExplorerOptions.ShowHidden = _hiddenFiles.Checked;
            foreach (var w in _app.Windows) w.OnHiddenFilesChanged();
        };
        _ntfs.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.Index.IndexNtfsVolumes = _ntfs.Checked; UpdateDrivesEnabled(); IndexChanged(); };
        _removable.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.Index.IndexRemovableDrives = _removable.Checked; IndexChanged(); };
        _network.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.Index.IndexNetworkDrives = _network.Checked; IndexChanged(); };
        _rescan.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _rescan.SelectedItem is not RescanChoice choice || choice.Minutes == _app.Settings.Index.NetworkRescanMinutes) return;
            _app.Settings.Index.NetworkRescanMinutes = choice.Minutes;
            IndexChanged();
        };
        _exHidden.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.Index.ExcludeHidden = _exHidden.Checked; IndexChanged(); };
        _exSystem.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.Index.ExcludeSystem = _exSystem.Checked; IndexChanged(); };
        _dateAccessed.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.Index.IndexDateAccessed = _dateAccessed.Checked; IndexChanged(); };
        _folderSizes.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.Index.IndexFolderSizes = _folderSizes.Checked; IndexChanged(); };
        _recentChanges.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _app.Settings.Index.IndexRecentChanges = _recentChanges.Checked;
            _recentFromJournal.Enabled = _recentChanges.Checked;
            IndexChanged();
        };
        _recentFromJournal.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.Index.RecentChangesFromJournal = _recentFromJournal.Checked; IndexChanged(); };
        _runHistory.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.RunHistoryEnabled = _runHistory.Checked; _app.Settings.Save(); };
        Activated += (_, _) => UpdateClearSearches(); // searched in a window while this was open
        _rememberSearches.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            var s = _app.Settings;
            s.RememberSearches = _rememberSearches.Checked;
            if (!s.RememberSearches) s.SearchHistory.Clear(); // off: the saved searches go too
            s.Save();
            MainForm.SearchHistoryChanged(_app);
            UpdateClearSearches();
        };
        _hotkeyWin.CheckedChanged += (_, _) => { if (_loading) return; _hotkey.SetWin(_hotkeyWin.Checked); ApplyHotkey(); };
        _everydayOnly.CheckedChanged += (_, _) => EverydayChanged();
        foreach (var (_, on) in _everydayKinds) on.CheckedChanged += (_, _) => EverydayChanged();
    }

    void ShowEveryday(AppSettings s)
    {
        _everydayOnly.Checked = s.EverydayFilesOnly;
        foreach (var (kind, on) in _everydayKinds) on.Checked = !s.EverydayKindsOff.Contains(kind.Key, StringComparer.OrdinalIgnoreCase);
        _everydayAdded.Text = string.Join("; ", s.EverydayAdded);
        UpdateEverydayEnabled();
    }

    void UpdateEverydayEnabled()
    {
        foreach (var (_, on) in _everydayKinds) on.Enabled = _everydayOnly.Checked;
        _everydayAdded.Enabled = _everydayOnly.Checked;
    }

    /// <summary>Everyday files: saved at once, and every Thumbnails view of files searches again, in every window.</summary>
    void EverydayChanged()
    {
        if (_loading) return;
        var s = _app.Settings;
        var off = _everydayKinds.Where(k => !k.Switch.Checked).Select(k => k.Kind.Key).ToList();
        var added = EverydayFiles.ParseExtensions(_everydayAdded.Text).ToList();
        _everydayAdded.Text = string.Join("; ", added);
        UpdateEverydayEnabled();
        if (s.EverydayFilesOnly == _everydayOnly.Checked && off.SequenceEqual(s.EverydayKindsOff) && added.SequenceEqual(s.EverydayAdded)) return;
        s.EverydayFilesOnly = _everydayOnly.Checked;
        s.EverydayKindsOff = off;
        s.EverydayAdded = added;
        s.Save();
        foreach (var w in _app.Windows) w.OnZapOptionsChanged();
    }

    void ResetEveryday()
    {
        _loading = true;
        try
        {
            foreach (var (_, on) in _everydayKinds) on.Checked = true;
            _everydayAdded.Text = "";
        }
        finally { _loading = false; }
        EverydayChanged();
    }

    /// <summary>Switches that take effect at once.</summary>
    void GeneralChanged()
    {
        if (_loading) return;
        var s = _app.Settings;
        s.StartWithWindows = _startWithWindows.Checked;
        s.RunInBackground = _runInBackground.Checked;
        s.ShowTrayIcon = _showTray.Checked;
        s.UseInsteadOfExplorer = _replaceExplorer.Checked;
        s.OpenFoldersInTabs = _openInTabs.Checked;
        s.CheckForUpdates = _checkUpdates.Checked;
        s.AutoUpdate = _autoUpdate.Checked;
        _autoUpdateAt.Enabled = s.AutoUpdate && !_app.IsDemo;
        s.FiltersVisible = _filtersBar.Checked;
        s.StatusBarVisible = _statusBar.Checked;
        var before = s.Defaults;
        if (_defaultSort.SelectedItem is SortChoice sort) (s.Sort, s.SortDescending) = (sort.Key, sort.Descending);
        s.FoldersFirst = _foldersFirst.Checked;
        s.MatchCase = _matchCase.Checked;
        s.MatchWholeWord = _wholeWord.Checked;
        s.MatchPath = _matchPath.Checked;
        s.MatchDiacritics = _diacritics.Checked;
        s.Regex = _regex.Checked;
        s.ExpandTreeToFolder = _expandTree.Checked;
        s.UsbPrompt = _usbPrompt.Checked;
        _app.ApplySettings(indexChanged: false);
        if (s.Defaults != before) foreach (var w in _app.Windows) w.OnDefaultsChanged(before, s.Defaults);
    }

    /// <summary>The navigation pane's places or Quick access's frequent folders: saved, and every window's pane shows it.</summary>
    void NavPaneChanged()
    {
        if (_loading) return;
        var s = _app.Settings;
        s.NavPinnedOnly = !_frequentFolders.Checked;
        s.NavHiddenRoots = _navRoots.Where(r => !r.Switch.Checked).Select(r => r.Key).ToList();
        s.Save();
        foreach (var w in _app.Windows) w.RefreshNavRoots();
    }

    /// <summary>
    /// What is indexed changed: saved now, handed to the index once when the window closes. Search inside files' cards
    /// follow: what the name index holds is all that is ever read inside.
    /// </summary>
    void IndexChanged()
    {
        if (_loading) return;
        _indexChanged = true;
        _app.Settings.Save();
        UpdateDriveRows();
        UpdateContentCards();
    }

    /// <summary>
    /// A drive's switch: the drives switched off are kept (with those not plugged in now, which stay off). Switched on,
    /// a drive comes off that list, and joins 1.4.0's list of drives to index while there is one: that list stays,
    /// since it still keeps off the drives that aren't plugged in now, and it only ever gains drives here.
    /// </summary>
    void DrivesChanged()
    {
        if (_loading) return;
        var ix = _app.Settings.Index;
        if (_drives.All(d => !d.Switch.Checked || KindOff(ix, d.Drive)))
        {
            // No drive at all means not indexing drives: say so with the main switch, and keep the drives ready for later.
            _loading = true;
            foreach (var d in _drives) if (!KindOff(ix, d.Drive)) d.Switch.Checked = true;
            _ntfs.Checked = false;
            _loading = false;
            ix.IndexNtfsVolumes = false;
        }
        foreach (var (drive, sw, _) in _drives)
        {
            if (sw.Checked != TurnedOff(ix, drive)) continue; // as it was
            if (!sw.Checked)
            {
                ix.SkipDrives = [.. ix.SkipDrives, drive.Root];
                continue;
            }
            ix.SkipDrives = [.. ix.SkipDrives.Where(r => !AppSettings.SamePath(r, drive.Root))];
            if (TurnedOff(ix, drive)) ix.Volumes = [.. ix.Volumes, drive.Root];
        }
        IndexChanged();
    }

    void UpdateDrivesEnabled()
    {
        var ix = _app.Settings.Index;
        foreach (var d in _drives) d.Switch.Enabled = _ntfs.Checked && !KindOff(ix, d.Drive);
        _removable.Enabled = _network.Enabled = _ntfs.Checked;
    }

    static readonly int[] RescanChoices = [15, 60, 360, 1440, 0];

    void SelectRescan(int minutes)
    {
        var choices = _rescan.Items.Cast<RescanChoice>().ToList();
        if (!choices.Any(c => c.Minutes == minutes))
        {
            // A time typed into the settings file: kept, and listed in its place.
            choices = [.. choices.Append(new RescanChoice(minutes)).OrderBy(c => c.Minutes == 0 ? int.MaxValue : c.Minutes)];
            _rescan.Items.Clear();
            foreach (var c in choices) _rescan.Items.Add(c);
        }
        _rescan.SelectedIndex = choices.FindIndex(c => c.Minutes == minutes);
    }

    /// <summary>Asks first when a whole drive is about to be left out: nothing on it would show up any more.</summary>
    bool ConfirmLeaveOut(string path)
    {
        if (!IndexPlan.IsDriveRoot(path)) return true;
        string drive = char.ToUpperInvariant(path.Trim()[0]) + ":";
        var yes = TaskDialogButton.Yes;
        var no = TaskDialogButton.No;
        var page = new TaskDialogPage
        {
            Caption = Text,
            Heading = $"Leave out all of {drive}?",
            Text = $"Nothing on {drive} will show up in searches. To stop indexing a drive, you can also turn it off under Indexing.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { yes, no },
            DefaultButton = no,
        };
        if (TaskDialog.ShowDialog(this, page) != yes) return false;
        _app.Settings.KeepDriveLeftOut(drive + "\\"); // asked already: the window's bar doesn't ask again
        return true;
    }

    /// <summary>"Type a path...": for a folder the folder picker can't always reach, like a share on another PC.</summary>
    async void AddTypedFolder()
    {
        const string title = "Search this folder too";
        string text = "";
        while (true)
        {
            var typed = InputDialog.Show(this, title, @"The folder's path, like D:\Projects or \\server\share:", text, text.Length);
            if (string.IsNullOrWhiteSpace(typed) || IsDisposed) return;
            text = typed.Trim();
            if (FolderPath(text) is not { } path)
            {
                MessageBox.Show(this, @"That isn't a folder's path. Type all of it, like D:\Projects or \\server\share.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                continue;
            }
            bool there = await FolderExists(path);
            if (IsDisposed) return;
            if (!there && MessageBox.Show(this, $"{path} can't be reached right now. Add it anyway? It's searched whenever it can be reached.",
                    title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                continue;
            _include.Add(path);
            return;
        }
    }

    /// <summary>
    /// A typed folder path, tidied ("d:" is "D:\", no backslash at the end), or null when it isn't a whole local path
    /// (D:\Projects) or network path (\\server\share).
    /// </summary>
    internal static string? FolderPath(string text)
    {
        string p = text.Trim().Trim('"').Trim().Replace('/', '\\');
        if (p.Length == 2 && p[1] == ':') p += "\\";
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) return null;
            if (p[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2) return null; // a server alone isn't a folder; \\server\share is
        }
        else if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\') return null;
        if (p.IndexOfAny([.. Path.GetInvalidPathChars(), '*', '?']) >= 0 || p.IndexOf(':', 2) >= 0) return null;
        try { p = Path.GetFullPath(p); }
        catch (Exception) { return null; }
        if (p[1] == ':') p = char.ToUpperInvariant(p[0]) + p[1..];
        return p.Length > 3 ? p.TrimEnd('\\') : p;
    }

    /// <summary>Whether a folder is there, waiting at most a few seconds (a PC that's away can take half a minute to say so).</summary>
    async Task<bool> FolderExists(string path)
    {
        UseWaitCursor = true;
        try
        {
            var check = Task.Run(() => Directory.Exists(path));
            return await Task.WhenAny(check, Task.Delay(3000)) == check && check.Result;
        }
        finally
        {
            if (!IsDisposed) UseWaitCursor = false;
        }
    }

    void PatternsChanged()
    {
        var patterns = _patterns.Text.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (patterns.SequenceEqual(_app.Settings.Index.ExcludePatterns, StringComparer.OrdinalIgnoreCase)) return;
        _app.Settings.Index.ExcludePatterns = patterns;
        IndexChanged();
    }

    void ApplyHotkey()
    {
        if (_loading) return;
        string hotkey = Hotkey.Format(_hotkey.Modifiers, _hotkey.Key);
        if (hotkey == _app.Settings.Hotkey) return;
        _app.Settings.Hotkey = hotkey;
        _app.ApplySettings(indexChanged: false);
    }

    void ChooseFont()
    {
        var s = _app.Settings;
        using var fd = new FontDialog { ShowEffects = false, AllowVerticalFonts = false };
        try { if (s.FontName.Length > 0) fd.Font = new Font(s.FontName, s.FontSize > 0 ? s.FontSize : 9f); } catch (ArgumentException) { }
        if (fd.ShowDialog(this) != DialogResult.OK) return;
        s.FontName = fd.Font.Name;
        s.FontSize = fd.Font.SizeInPoints;
        ResultsFontChanged();
    }

    void ResultsFontChanged()
    {
        UpdateFontCard();
        _app.ApplySettings(indexChanged: false);
    }

    void UpdateFontCard()
    {
        var s = _app.Settings;
        _fontCard?.SetDescription(s.FontName.Length == 0 ? "Windows' font (the default)." : $"{s.FontName}, {s.FontSize:0.#} pt");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        PatternsChanged();
        base.OnFormClosing(e);
    }

    // ---- controls -----------------------------------------------------------------------------

    /// <summary>The smaller text of a card's description, for <paramref name="font"/> (the window's 14px font).</summary>
    static Font SmallFont(Font font) => new(font.FontFamily, font.Size * 12f / 14f, FontStyle.Regular, font.Unit);

    /// <summary>
    /// A labelled row inside a card (a drive and its switch). Optionally a smaller second line under the label (what
    /// the drive holds), and a button before the control that shows only when it's wanted ("Include it").
    /// </summary>
    sealed class SubRow : Panel
    {
        readonly Label _label;
        readonly Label? _detail;
        readonly Control _control;
        readonly Control? _extra;
        readonly bool _wrapDetail;
        bool _extraShown;
        Font? _small;

        /// <param name="detail">The second line; null for a row without one. Rows with one keep its room while it's empty.</param>
        /// <param name="wrapDetail">
        /// The second line wraps onto as many lines as it needs, breaking inside a word (a path with no spaces) when it
        /// must, and the row grows with it, instead of being cut with "...". For a line that must always be read whole
        /// (where the saved thumbnails are kept); the others keep one line, so a long list of rows stays even.
        /// </param>
        public SubRow(string text, Control control, Control? extra = null, string? detail = null, bool wrapDetail = false)
        {
            _label = new Label { Text = text, AutoSize = false, AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
            _control = control;
            _extra = extra;
            _wrapDetail = wrapDetail && detail is not null;
            BackColor = Color.Transparent;
            Controls.Add(_label);
            if (detail is not null)
            {
                _small = SmallFont(Font);
                _detail = _wrapDetail
                    ? new WrappedDetail { Text = detail, Font = _small, AutoSize = false, UseMnemonic = false, BackColor = Color.Transparent, ForeColor = ExplorerTheme.SecondaryText }
                    : new Label { Text = detail, Font = _small, AutoSize = false, AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, ForeColor = ExplorerTheme.SecondaryText };
                Controls.Add(_detail);
            }
            if (extra is not null)
            {
                extra.Visible = false;
                Controls.Add(extra);
            }
            Controls.Add(control);
            Margin = Padding.Empty;
        }

        public Label? DetailLabel => _detail;

        public void SetDetail(string text)
        {
            if (_detail is null || _detail.Text == text) return;
            bool relayout = (_detail.Text.Length == 0) != (text.Length == 0);
            _detail.Text = text;
            if (relayout) PerformLayout();
        }

        public void ShowExtra(bool show)
        {
            if (_extra is null || _extraShown == show) return;
            _extraShown = show;
            _extra.Visible = show;
            PerformLayout();
        }

        static Size SizeOf(Control c) => c is ComboBox box ? box.Size : c.GetPreferredSize(Size.Empty);

        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width is > 0 and < 100_000 ? proposed.Width : Parent?.ClientSize.Width ?? 400;
            int h = Math.Max(LogicalToDeviceUnits(36), SizeOf(_control).Height + LogicalToDeviceUnits(4));
            if (_detail is not null) h = Math.Max(h, _label.Font.Height + DetailHeight(TextWidth(w)) + LogicalToDeviceUnits(12));
            return new Size(w, h);
        }

        /// <summary>The room the label and the second line have in a row <paramref name="rowWidth"/> wide: left of the button(s).</summary>
        int TextWidth(int rowWidth)
        {
            int gap = LogicalToDeviceUnits(12);
            int right = rowWidth - SizeOf(_control).Width;
            if (_extra is not null && _extraShown) right -= gap + SizeOf(_extra).Width;
            return Math.Max(0, right - gap);
        }

        /// <summary>The second line's height: one line, or as many as the wrapped text takes at <paramref name="width"/>.</summary>
        int DetailHeight(int width)
        {
            int one = _detail!.Font.Height + LogicalToDeviceUnits(2);
            if (!_wrapDetail || _detail.Text.Length == 0) return one;
            var text = TextRenderer.MeasureText(_detail.Text, _detail.Font, new Size(Math.Max(1, width), int.MaxValue), WrappedDetail.Flags);
            return Math.Max(one, text.Height + LogicalToDeviceUnits(2));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int gap = LogicalToDeviceUnits(12);
            var cs = SizeOf(_control);
            _control.Bounds = new Rectangle(ClientSize.Width - cs.Width, (ClientSize.Height - cs.Height) / 2, cs.Width, cs.Height);
            int right = _control.Left;
            if (_extra is not null && _extraShown)
            {
                var es = SizeOf(_extra);
                _extra.Bounds = new Rectangle(right - gap - es.Width, (ClientSize.Height - es.Height) / 2, es.Width, es.Height);
                right = _extra.Left;
            }
            int width = Math.Max(0, right - gap);
            if (_detail is null || _detail.Text.Length == 0)
            {
                _label.Bounds = new Rectangle(0, 0, width, ClientSize.Height);
                _detail?.SetBounds(0, 0, 0, 0);
                return;
            }
            int line = _label.Font.Height, small = DetailHeight(width);
            int top = Math.Max(0, (ClientSize.Height - line - small) / 2);
            _label.Bounds = new Rectangle(0, top, width, line);
            _detail.Bounds = new Rectangle(0, top + line, width, small);
        }

        /// <summary>
        /// A second line that wraps: drawn with the same rule it is measured with, so what was measured is what shows.
        /// TextBoxControl lets a word longer than the line (a path with no spaces) break inside itself, as a text box
        /// does, rather than run past the edge.
        /// </summary>
        sealed class WrappedDetail : Label
        {
            public const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;

            protected override void OnPaint(PaintEventArgs e)
                => TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, Flags);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_detail is null) return;
            var old = _small;
            _small = SmallFont(Font);
            _detail.Font = _small;
            old?.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _small?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A line of small text under a card's rows, wrapped to the card's width; it takes no room while empty.</summary>
    sealed class NoteLabel : Label
    {
        Font? _small;

        public NoteLabel()
        {
            AutoSize = false;
            UseMnemonic = false;
            BackColor = Color.Transparent;
            ForeColor = ExplorerTheme.SecondaryText;
            Margin = Padding.Empty;
        }

        const TextFormatFlags Wrap = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
        Font Small => _small ??= SmallFont(Font);

        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width is > 0 and < 100_000 ? proposed.Width : Width;
            if (Text.Length == 0) return new Size(w, 0);
            var text = TextRenderer.MeasureText(Text, Small, new Size(Math.Max(1, w), int.MaxValue), Wrap);
            return new Size(w, text.Height + LogicalToDeviceUnits(14));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int top = LogicalToDeviceUnits(8);
            TextRenderer.DrawText(e.Graphics, Text, Small, new Rectangle(0, top, Width, Math.Max(0, Height - top)), ForeColor, Wrap);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            _small?.Dispose();
            _small = null;
            base.OnFontChanged(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _small?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>An entry of Network drives › Check them again: minutes between rescans (0 = only when rebuilding).</summary>
    sealed record RescanChoice(int Minutes)
    {
        public override string ToString() => Minutes switch
        {
            0 => "Only when I rebuild",
            1 => "Every minute",
            60 => "Every hour",
            1440 => "Once a day",
            < 60 => $"Every {Minutes} minutes",
            _ when Minutes % 1440 == 0 => $"Every {Minutes / 1440} days",
            _ when Minutes % 60 == 0 => $"Every {Minutes / 60} hours",
            _ => $"Every {Minutes} minutes",
        };
    }

    /// <summary>Rows under a card's text, each as wide as the card.</summary>
    class RowList : FlowLayoutPanel
    {
        public RowList()
        {
            FlowDirection = FlowDirection.TopDown;
            WrapContents = false;
            BackColor = Color.Transparent;
            Margin = Padding.Empty;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            foreach (Control c in Controls)
                if (c is SubRow or NoteLabel or ActionNote) c.Size = new Size(ClientSize.Width, c.GetPreferredSize(new Size(ClientSize.Width, 0)).Height);
            base.OnLayout(e);
        }

        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width is > 0 and < 100_000 ? proposed.Width : Width;
            int h = 0;
            foreach (Control c in Controls) h += c.GetPreferredSize(new Size(w, 0)).Height + c.Margin.Vertical;
            return new Size(w, h);
        }
    }

    /// <summary>The folders a card lists, each with a Remove button.</summary>
    sealed class FolderList : RowList
    {
        List<string> _folders = [];
        public event Action? Changed;

        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public List<string> Folders
        {
            get => [.. _folders];
            set { _folders = [.. value]; Rebuild(); }
        }

        /// <summary>Asked before a folder goes on the list; false keeps it off.</summary>
        [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Func<string, bool>? Confirm { get; set; }

        public void AddWithPicker(IWin32Window owner, string title)
        {
            using var fb = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, ShowNewFolderButton = false };
            if (fb.ShowDialog(owner) != DialogResult.OK) return;
            Add(fb.SelectedPath);
        }

        public void Add(string folder)
        {
            if (_folders.Any(f => AppSettings.SamePath(f, folder))) return;
            if (Confirm is { } confirm && !confirm(folder)) return;
            _folders.Add(folder);
            Rebuild();
            Changed?.Invoke();
        }

        void Rebuild()
        {
            SuspendLayout();
            while (Controls.Count > 0) Controls[0].Dispose();
            if (_folders.Count == 0)
                Controls.Add(new Label { Text = "None yet.", AutoSize = true, ForeColor = ExplorerTheme.SecondaryText, BackColor = Color.Transparent, Margin = new Padding(0, LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(4)) });
            foreach (var folder in _folders)
            {
                var path = folder;
                var remove = new Button { Text = "Remove", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, UseVisualStyleBackColor = true };
                remove.Click += (_, _) => { _folders.Remove(path); Rebuild(); Changed?.Invoke(); };
                Controls.Add(new SubRow(path, remove));
            }
            ResumeLayout();
            Parent?.PerformLayout();
            Parent?.Parent?.PerformLayout();
        }

    }

    /// <summary>Captures a key combination for the global hotkey.</summary>
    internal sealed class HotkeyBox : TextBox
    {
        public uint Modifiers { get; private set; }
        public Keys Key { get; private set; }

        public HotkeyBox()
        {
            ReadOnly = true;
            ShortcutsEnabled = false;
            TextAlign = HorizontalAlignment.Center;
        }

        public void Set(uint modifiers, Keys key)
        {
            Modifiers = key == Keys.None ? 0 : modifiers;
            Key = key;
            Text = key == Keys.None ? "None" : Hotkey.Format(Modifiers, key);
        }

        public void SetWin(bool on)
        {
            if (Key == Keys.None) return;
            Set(on ? Modifiers | Native.MOD_WIN : Modifiers & ~Native.MOD_WIN, Key);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            Keys mods = keyData & Keys.Modifiers;
            if ((key == Keys.Tab && (mods == Keys.None || mods == Keys.Shift)) || (mods == Keys.None && key is Keys.Escape or Keys.Enter))
                return base.ProcessCmdKey(ref msg, keyData);
            if (mods == Keys.None && key is Keys.Back or Keys.Delete)
            {
                Set(0, Keys.None);
                return true;
            }
            if (Hotkey.IsModifierKey(key)) return true;
            uint m = 0;
            if ((mods & Keys.Control) != 0) m |= Native.MOD_CONTROL;
            if ((mods & Keys.Alt) != 0) m |= Native.MOD_ALT;
            if ((mods & Keys.Shift) != 0) m |= Native.MOD_SHIFT;
            if (Native.IsKeyDown(Keys.LWin) || Native.IsKeyDown(Keys.RWin) || (Modifiers & Native.MOD_WIN) != 0) m |= Native.MOD_WIN;
            Set(m, key);
            return true;
        }
    }
}

/// <summary>
/// A settings page: its title, headers and cards one under the other, each card as wide as the page. It measures them
/// at its real width, so its height (and the window's scrolling) is exactly what they need.
/// </summary>
internal sealed class PageStack : Panel
{
    int Arrange(int width, bool apply)
    {
        int y = Padding.Top, inner = Math.Max(0, width - Padding.Horizontal);
        foreach (Control c in Controls)
        {
            y += c.Margin.Top;
            Size size = c is Label { AutoSize: true }
                ? c.GetPreferredSize(Size.Empty)
                : new Size(inner - c.Margin.Horizontal, c.GetPreferredSize(new Size(inner - c.Margin.Horizontal, 0)).Height);
            if (apply) c.Bounds = new Rectangle(Padding.Left + c.Margin.Left, y, size.Width, size.Height);
            y += size.Height + c.Margin.Bottom;
        }
        return y + Padding.Bottom;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        int height = Arrange(ClientSize.Width, apply: true);
        if (Height != height) Height = height;
    }

    public override Size GetPreferredSize(Size proposed) =>
        new(proposed.Width, Arrange(proposed.Width is > 0 and < 100_000 ? proposed.Width : Width, apply: false));
}

/// <summary>
/// A thin strip that fills from left to right in the accent colour: how far an update has got. No height while idle.
/// </summary>
internal sealed class ProgressStrip : Control
{
    double? _value;

    public ProgressStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        AccessibleRole = AccessibleRole.ProgressBar;
    }

    /// <summary>0 to 1 while an update runs; null when none does (then it takes no room).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double? Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            AccessibleDescription = value is { } v ? $"{(int)(v * 100)}%" : null;
            Invalidate();
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, _value is null ? 0 : LogicalToDeviceUnits(4));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (_value is not { } v || Width <= 0 || Height <= 0) return;
        float radius = Height / 2f;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        ExplorerTheme.FillRounded(g, new Rectangle(0, 0, Width - 1, Height - 1), radius, ExplorerTheme.Divider);
        int filled = (int)Math.Round((Width - 1) * Math.Clamp(v, 0, 1));
        if (filled > 0) ExplorerTheme.FillRounded(g, new Rectangle(0, 0, Math.Max(Height, filled), Height - 1), radius, ExplorerTheme.Accent);
    }
}

/// <summary>
/// One setting, Windows 11 style: a rounded card with a title and a line explaining it on the left, its control on the
/// right, and optionally more under them (a list of folders, a row per drive).
/// </summary>
internal sealed class SettingCard : Panel
{
    readonly string _title;
    string _description;
    readonly Control? _action, _body;
    Font? _small;

    public SettingCard(string title, string description, Control? action, Control? body)
    {
        _title = title;
        _description = description;
        _action = action;
        _body = body;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = SettingsForm.CardFill;
        ForeColor = ExplorerTheme.Text;
        AccessibleName = title;
        if (action is not null) { Controls.Add(action); action.AccessibleName ??= title; }
        if (body is not null) Controls.Add(body);
    }

    public void SetDescription(string text)
    {
        _description = text;
        Invalidate();
        Parent?.PerformLayout();
    }

    int S(int v) => LogicalToDeviceUnits(v);
    Font Small => _small ??= new Font(Font.FontFamily, Font.Size * 12f / 14f, FontStyle.Regular, Font.Unit);

    protected override void OnFontChanged(EventArgs e)
    {
        _small?.Dispose();
        _small = null;
        base.OnFontChanged(e);
    }

    const TextFormatFlags Wrap = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;

    Size ActionSize => _action switch
    {
        null => Size.Empty,
        TextBox box => new Size(box.Width, box.PreferredHeight),
        _ => _action.GetPreferredSize(Size.Empty),
    };

    /// <summary>The width the page gives the card, when the layout asks without one.</summary>
    int FallbackWidth => Parent is { } p ? Math.Max(S(300), p.ClientSize.Width - p.Padding.Horizontal - Margin.Horizontal) : Math.Max(Width, S(400));

    (Rectangle Title, Rectangle Description, int Header) Measure(int width)
    {
        int padX = S(18), padY = S(14), gap = S(16);
        var action = ActionSize;
        int textWidth = Math.Max(S(80), width - 2 * padX - (_action is null ? 0 : action.Width + gap));
        var title = TextRenderer.MeasureText(_title, Font, new Size(textWidth, int.MaxValue), Wrap);
        var desc = _description.Length == 0 ? Size.Empty : TextRenderer.MeasureText(_description, Small, new Size(textWidth, int.MaxValue), Wrap);
        int textHeight = title.Height + (desc.Height > 0 ? S(2) + desc.Height : 0);
        int header = Math.Max(Math.Max(textHeight, action.Height), S(40));
        // The text is centred on the action beside it. Without one, a title without a description stays where the other
        // cards have theirs (as if a line of description were under it), not lower in the empty header.
        int centred = _action is null ? Math.Max(textHeight, title.Height + S(2) + Small.Height) : textHeight;
        int top = padY + Math.Max(0, (header - centred) / 2);
        return (new Rectangle(padX, top, textWidth, title.Height),
                new Rectangle(padX, top + title.Height + S(2), textWidth, desc.Height), header);
    }

    public override Size GetPreferredSize(Size proposed)
    {
        int width = proposed.Width is > 0 and < 100_000 ? proposed.Width : FallbackWidth;
        var (_, _, header) = Measure(width);
        int height = S(14) + header + S(14);
        if (_body is not null) height += _body.GetPreferredSize(new Size(width - 2 * S(18), 0)).Height + S(10);
        return new Size(width, height);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var (_, _, header) = Measure(ClientSize.Width);
        if (_action is not null)
        {
            var size = ActionSize;
            _action.Bounds = new Rectangle(ClientSize.Width - S(18) - size.Width, S(14) + (header - size.Height) / 2, size.Width, size.Height);
        }
        if (_body is not null)
        {
            int w = ClientSize.Width - 2 * S(18);
            _body.Bounds = new Rectangle(S(18), S(14) + header + S(4), w, _body.GetPreferredSize(new Size(w, 0)).Height);
        }
        int want = GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
        if (want != Height) Height = want;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(Parent?.BackColor ?? BackColor)) g.FillRectangle(back, ClientRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = ExplorerTheme.RoundedRect(r, S(6)))
        {
            using var fill = new SolidBrush(BackColor);
            using var border = new Pen(SettingsForm.CardBorder);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
        g.SmoothingMode = SmoothingMode.Default;
        var (title, desc, _) = Measure(ClientSize.Width);
        TextRenderer.DrawText(g, _title, Font, title, Enabled ? ExplorerTheme.Text : ExplorerTheme.DisabledText, Wrap);
        if (_description.Length > 0) TextRenderer.DrawText(g, _description, Small, desc, ExplorerTheme.SecondaryText, Wrap);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _small?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>An on/off switch like Windows 11's, with "On" / "Off" beside it. A CheckBox underneath: Space toggles it.</summary>
internal sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = false;
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
    }

    int S(int v) => LogicalToDeviceUnits(v);

    public override Size GetPreferredSize(Size proposedSize) => new(S(92), S(32));

    /// <summary>The first solid colour behind it (rows inside a card are see-through), so its text draws crisply.</summary>
    Color Background()
    {
        for (Control? c = Parent; c is not null; c = c.Parent)
            if (c.BackColor.A == 255) return c.BackColor;
        return SystemColors.Control;
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(Background())) g.FillRectangle(back, ClientRectangle);
        int w = S(40), h = S(20);
        var track = new Rectangle(Width - w - S(1), (Height - h) / 2, w, h);
        Color text = Enabled ? ExplorerTheme.Text : ExplorerTheme.DisabledText;
        TextRenderer.DrawText(g, Checked ? "On" : "Off", Font, new Rectangle(0, 0, track.Left - S(12), Height), text,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = ExplorerTheme.RoundedRect(new RectangleF(track.X + 0.5f, track.Y + 0.5f, track.Width - 1, track.Height - 1), h / 2f);
        Color accent = Enabled ? ExplorerTheme.Accent : ExplorerTheme.DisabledText;
        int knob = S(12);
        if (Checked)
        {
            using var fill = new SolidBrush(accent);
            g.FillPath(fill, path);
            using var dot = new SolidBrush(ExplorerTheme.Dark ? Color.Black : Color.White);
            g.FillEllipse(dot, track.Right - S(4) - knob, track.Y + (h - knob) / 2f, knob, knob);
        }
        else
        {
            using var pen = new Pen(Enabled ? ExplorerTheme.SecondaryText : ExplorerTheme.DisabledText, Math.Max(1f, S(1)));
            g.DrawPath(pen, path);
            using var dot = new SolidBrush(Enabled ? ExplorerTheme.SecondaryText : ExplorerTheme.DisabledText);
            g.FillEllipse(dot, track.X + S(4), track.Y + (h - knob) / 2f, knob, knob);
        }
        g.SmoothingMode = SmoothingMode.Default;
        if (Focused && ShowFocusCues)
        {
            var focus = Rectangle.Inflate(track, S(3), S(3));
            using var pen = new Pen(ExplorerTheme.Text, Math.Max(1f, S(2))) { Alignment = PenAlignment.Inset };
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var fp = ExplorerTheme.RoundedRect(focus, focus.Height / 2f);
            g.DrawPath(pen, fp);
            g.SmoothingMode = SmoothingMode.Default;
        }
    }
}

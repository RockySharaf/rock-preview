using System.Drawing.Drawing2D;
using Anything.Interop;

namespace Anything;

/// <summary>
/// Settings, laid out like Windows 11's Settings app: sections on the left; on the right the section's cards, each a
/// setting with a line explaining it and its switch or button. Changes apply as they are made. Changes to what is
/// indexed (drives, folders, what to leave out) rebuild the index once, when the window closes.
/// </summary>
internal sealed class SettingsForm : Form
{
    readonly AnythingApp _app;
    readonly ListBox _nav = new() { Dock = DockStyle.Left, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false };
    readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly List<(string Name, char Glyph, PageStack Page)> _sections = [];
    bool _loading, _indexChanged;

    // General
    readonly ToggleSwitch _startWithWindows = new(), _runInBackground = new(), _showTray = new(), _replaceExplorer = new(), _openInTabs = new(), _checkUpdates = new();
    // Appearance
    readonly ToggleSwitch _filtersBar = new(), _statusBar = new();
    SettingCard? _fontCard;
    // Search
    readonly ToggleSwitch _matchCase = new(), _wholeWord = new(), _matchPath = new(), _diacritics = new(), _regex = new();
    // Indexing
    readonly ToggleSwitch _ntfs = new();
    readonly List<(string Root, ToggleSwitch Switch)> _drives = [];
    // Folders
    readonly FolderList _include = new(), _exclude = new();
    readonly TextBox _patterns = new();
    readonly ToggleSwitch _exHidden = new(), _exSystem = new();
    // Keyboard
    readonly HotkeyBox _hotkey = new();
    readonly ToggleSwitch _hotkeyWin = new();

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
        Text = app.IsDemo ? "Ultimate File Explorer Settings (Demo)" : "Ultimate File Explorer Settings";
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
        AddSection("Search", '', SearchPage());
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
        FormClosed += (_, _) =>
        {
            ApplyHotkey();
            if (_indexChanged) _app.ApplySettings(indexChanged: true);
            _app.RegisterHotkey();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ExplorerTheme.ApplyFrame(this);
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
        _replaceExplorer.Enabled = _checkUpdates.Enabled = !_app.IsDemo;
        return Page("General",
            Card("Start when Windows starts", $"So {key} opens Ultimate File Explorer any time.", _startWithWindows),
            Card("Keep running in the background", $"Closing the window keeps it ready for {key}, with your searches as you left them.", _runInBackground),
            Card("Show the tray icon", "An icon by the clock to open it, or to exit.", _showTray),
            Card("Use instead of Windows File Explorer",
                "Folders, drives, Win+E, This PC and File Explorer's taskbar button open Ultimate File Explorer. " +
                "Turn it off to get File Explorer back exactly as it was.", _replaceExplorer),
            Card("Open folders in tabs",
                "A folder opened from Windows, or Win+E, opens as a new tab in the window you used last. Off: each opens in a new window.", _openInTabs),
            Card("Tell me when a new version is out",
                "Once a day it asks GitHub for the latest version. Nothing about you or your files is sent.", _checkUpdates));
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

    PageStack SearchPage() => Page("Search",
        Header("New searches start with"),
        Card("Match case", "\"Report\" finds Report, not report.", _matchCase),
        Card("Match whole words", "\"port\" finds port, not report.", _wholeWord),
        Card("Match the full path", "Search words can match folder names on the way to a file.", _matchPath),
        Card("Match accents", "\"resume\" finds resume, not résumé.", _diacritics),
        Card("Regular expressions", "Search with patterns like ^IMG_\\d+\\.jpg$.", _regex));

    PageStack IndexingPage()
    {
        var index = _app.IndexManager;
        bool service = !_app.IsDemo && IndexService.IsInstalled();
        string status = index.UsesService ? "On: the Ultimate File Explorer Service reads your drives, so every change shows up at once."
            : index.IsElevated ? "On, because Ultimate File Explorer runs as administrator."
            : service ? "The Ultimate File Explorer Service isn't answering. Restarting the PC usually brings it back."
            : "Off: folders are scanned instead, which is slower. Windows asks for permission once to turn it on.";
        Control? turnOn = service || _app.IsDemo ? null : CardButton("Turn on", () => { Close(); _app.Post(() => _app.TurnOnInstantIndexing(null)); });

        var drives = new RowList();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? $"{d.Name.TrimEnd('\\')}  ({d.DriveFormat})" : $"{d.Name.TrimEnd('\\')}  {d.VolumeLabel}  ({d.DriveFormat})";
                var sw = new ToggleSwitch();
                sw.CheckedChanged += (_, _) => DrivesChanged();
                _drives.Add((d.Name, sw));
                drives.Controls.Add(new SubRow(label, sw));
            }
        }
        catch (IOException) { }

        return Page("Indexing",
            Card("Instant indexing", status, turnOn),
            Card("Index whole drives", "Reads each drive's file table: millions of files in seconds, kept up to date as they change.", _ntfs, drives),
            Card("Rebuild the index", "Scans everything again from the start.", CardButton("Rebuild now", () => { _indexChanged = false; _app.ApplySettings(indexChanged: true); })));
    }

    PageStack FoldersPage()
    {
        _include.Changed += () => { _app.Settings.Index.Folders = _include.Folders; IndexChanged(); };
        _exclude.Changed += () => { _app.Settings.Index.ExcludeFolders = _exclude.Folders; IndexChanged(); };
        _patterns.Width = S(260);
        _patterns.PlaceholderText = "*.tmp; ~$*";
        _patterns.Leave += (_, _) => PatternsChanged();
        return Page("Folders",
            Card("Also search these folders", "Folders on other drives, USB sticks or the network. They are scanned and kept up to date.",
                CardButton("Add a folder", () => _include.AddWithPicker(this, "Search this folder too")), _include),
            Card("Leave these folders out", "Nothing inside them shows up in searches or in Thumbnails' every-file view.",
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
            Card("Ultimate File Explorer", $"Version {current}, formerly Rock Preview. Free software under the GNU GPL v3."),
            Card("Updates", "See if a newer version is out.", CardButton("Check for updates", () => _app.CheckForUpdatesInteractive(this))),
            Card("Get started", "The short tour: Alt+X, Thumbnails, subfolders, sorting and choosing folders.", CardButton("Show", () => { Close(); _app.ShowGetStarted(); })),
            Card("Source code and releases", "github.com/RockySharaf/rock-preview", CardButton("Open", () => AnythingApp.OpenWebPage("https://github.com/" + Anything.Core.UpdateCheck.Repository))));
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
        _checkUpdates.Checked = s.CheckForUpdates;
        _filtersBar.Checked = s.FiltersVisible;
        _statusBar.Checked = s.StatusBarVisible;
        UpdateFontCard();
        _matchCase.Checked = s.MatchCase;
        _wholeWord.Checked = s.MatchWholeWord;
        _matchPath.Checked = s.MatchPath;
        _diacritics.Checked = s.MatchDiacritics;
        _regex.Checked = s.Regex;
        var ix = s.Index;
        _ntfs.Checked = ix.IndexNtfsVolumes;
        foreach (var (root, sw) in _drives)
            sw.Checked = ix.Volumes.Count == 0 || ix.Volumes.Any(v => string.Equals(v.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        UpdateDrivesEnabled();
        _include.Folders = ix.Folders;
        _exclude.Folders = ix.ExcludeFolders;
        _patterns.Text = string.Join("; ", ix.ExcludePatterns);
        _exHidden.Checked = ix.ExcludeHidden;
        _exSystem.Checked = ix.ExcludeSystem;
        Hotkey.TryParse(s.Hotkey, out uint mods, out Keys key);
        _hotkey.Set(mods, key);
        _hotkeyWin.Checked = (mods & Native.MOD_WIN) != 0;
        _loading = false;

        foreach (var sw in new[] { _startWithWindows, _runInBackground, _showTray, _replaceExplorer, _openInTabs, _checkUpdates, _filtersBar, _statusBar,
                                   _matchCase, _wholeWord, _matchPath, _diacritics, _regex })
            sw.CheckedChanged += (_, _) => GeneralChanged();
        _ntfs.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.Index.IndexNtfsVolumes = _ntfs.Checked; UpdateDrivesEnabled(); IndexChanged(); };
        _exHidden.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.Index.ExcludeHidden = _exHidden.Checked; IndexChanged(); };
        _exSystem.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.Index.ExcludeSystem = _exSystem.Checked; IndexChanged(); };
        _hotkeyWin.CheckedChanged += (_, _) => { if (_loading) return; _hotkey.SetWin(_hotkeyWin.Checked); ApplyHotkey(); };
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
        s.FiltersVisible = _filtersBar.Checked;
        s.StatusBarVisible = _statusBar.Checked;
        s.MatchCase = _matchCase.Checked;
        s.MatchWholeWord = _wholeWord.Checked;
        s.MatchPath = _matchPath.Checked;
        s.MatchDiacritics = _diacritics.Checked;
        s.Regex = _regex.Checked;
        _app.ApplySettings(indexChanged: false);
    }

    /// <summary>What is indexed changed: saved now, the index is rebuilt once when the window closes.</summary>
    void IndexChanged()
    {
        if (_loading) return;
        _indexChanged = true;
        _app.Settings.Save();
    }

    void DrivesChanged()
    {
        if (_loading) return;
        var on = _drives.Where(d => d.Switch.Checked).Select(d => d.Root).ToList();
        if (on.Count == 0)
        {
            // No drive at all means not indexing drives: say so with the main switch, and keep the drives ready for later.
            _loading = true;
            foreach (var (_, sw) in _drives) sw.Checked = true;
            _ntfs.Checked = false;
            _loading = false;
            _app.Settings.Index.IndexNtfsVolumes = false;
            _app.Settings.Index.Volumes = [];
            UpdateDrivesEnabled();
        }
        else _app.Settings.Index.Volumes = on.Count == _drives.Count ? [] : on;
        IndexChanged();
    }

    void UpdateDrivesEnabled()
    {
        foreach (var (_, sw) in _drives) sw.Enabled = _ntfs.Checked;
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

    /// <summary>A labelled row inside a card (a drive and its switch).</summary>
    sealed class SubRow : Panel
    {
        readonly Label _label;
        readonly Control _control;

        public SubRow(string text, Control control)
        {
            _label = new Label { Text = text, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
            _control = control;
            BackColor = Color.Transparent;
            Controls.Add(_label);
            Controls.Add(control);
            Margin = Padding.Empty;
        }

        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width is > 0 and < 100_000 ? proposed.Width : Parent?.ClientSize.Width ?? 400;
            return new Size(w, Math.Max(LogicalToDeviceUnits(36), _control.GetPreferredSize(Size.Empty).Height + LogicalToDeviceUnits(4)));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var cs = _control.GetPreferredSize(Size.Empty);
            _control.Bounds = new Rectangle(ClientSize.Width - cs.Width, (ClientSize.Height - cs.Height) / 2, cs.Width, cs.Height);
            _label.Bounds = new Rectangle(0, 0, Math.Max(0, _control.Left - LogicalToDeviceUnits(12)), ClientSize.Height);
        }
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
                if (c is SubRow) c.Size = new Size(ClientSize.Width, c.GetPreferredSize(new Size(ClientSize.Width, 0)).Height);
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

        public void AddWithPicker(IWin32Window owner, string title)
        {
            using var fb = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, ShowNewFolderButton = false };
            if (fb.ShowDialog(owner) != DialogResult.OK) return;
            if (_folders.Contains(fb.SelectedPath, StringComparer.OrdinalIgnoreCase)) return;
            _folders.Add(fb.SelectedPath);
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
        int top = padY + (header - textHeight) / 2;
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

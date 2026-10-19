using Anything.Interop;

namespace Anything;

/// <summary>Tools > Options: a page list on the left (like Everything), the selected page on the right.</summary>
internal sealed class OptionsForm : DialogBase
{
    readonly AnythingApp _app;
    readonly ListBox _pages = new() { Dock = DockStyle.Left, Width = 140, IntegralHeight = false };
    readonly Panel _host = new() { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 8, 4) };
    readonly List<(string Name, Control Page)> _pageList = [];

    // General
    readonly CheckBox _runInBackground = Check("&Run in background (keep running after the last window closes)");
    readonly CheckBox _showTray = Check("Show &tray icon");
    readonly CheckBox _startWithWindows = Check("&Start Ultimate File Explorer when Windows starts");
    readonly CheckBox _replaceExplorer = Check("&Use Ultimate File Explorer instead of Windows File Explorer");
    readonly CheckBox _checkUpdates = Check("Tell me when a new &version is out");
    // UI
    readonly CheckBox _filtersBar = Check("Show &filters bar");
    readonly CheckBox _statusBar = Check("Show &status bar");
    readonly Label _fontLabel = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    string _fontName = "";
    float _fontSize;
    // Search
    readonly CheckBox _matchCase = Check("Match &case");
    readonly CheckBox _wholeWord = Check("Match &whole word");
    readonly CheckBox _matchPath = Check("Match &path");
    readonly CheckBox _diacritics = Check("Match &diacritics");
    readonly CheckBox _regex = Check("Enable &regex");
    // Indexes
    readonly CheckBox _ntfs = Check("Index &NTFS volumes (fast; requires administrator)");
    readonly CheckedListBox _drives = new() { Width = 360, Height = 130, CheckOnClick = true, IntegralHeight = false };
    // Folders
    readonly ListBox _folders = new() { Width = 360, Height = 200, IntegralHeight = false };
    // Exclude
    readonly CheckBox _exHidden = Check("Exclude &hidden files and folders");
    readonly CheckBox _exSystem = Check("Exclude &system files and folders");
    readonly ListBox _exFolders = new() { Width = 360, Height = 120, IntegralHeight = false };
    readonly TextBox _exPatterns = new() { Width = 360 };
    // Keyboard
    readonly HotkeyBox _hotkey = new() { Width = 220 };
    readonly CheckBox _hotkeyWin = Check("&Win key");

    public OptionsForm(AnythingApp app) : base(app.IsDemo ? "Ultimate File Explorer Options (Demo)" : "Ultimate File Explorer Options")
    {
        _app = app;
        SuspendLayout();
        Icon = app.AppIcon;
        ShowInTaskbar = !Application.OpenForms.Cast<Form>().Any(f => f.Visible && f != this);
        StartPosition = FormStartPosition.CenterScreen;

        AddPage("General", Page(_runInBackground, _showTray, _startWithWindows, _replaceExplorer,
            Hint("Opening folders and drives, Win+E, This PC and File Explorer's taskbar button open Ultimate File Explorer. " +
                 "Turn it off any time to get File Explorer back exactly as it was."),
            _checkUpdates,
            Hint("Once a day Ultimate File Explorer asks GitHub for its latest version. Nothing about you or your files is sent."),
            AdminRow()));
        _replaceExplorer.Enabled = _checkUpdates.Enabled = !app.IsDemo;
        AddPage("UI", Page(_filtersBar, _statusBar, FontRow()));
        AddPage("Search", Page(Caption("Default search options for new searches:"), _matchCase, _wholeWord, _matchPath, _diacritics, _regex));
        AddPage("Indexes", Page(_ntfs, Caption("Volumes to index (none checked = all fixed drives):"), _drives, RebuildRow()));
        AddPage("Folders", Page(Caption("Additional folders to index (scanned and monitored):"), _folders, ListButtons(_folders, "Add folder to index")));
        AddPage("Exclude", Page(_exHidden, _exSystem, Caption("Exclude folders:"), _exFolders, ListButtons(_exFolders, "Exclude folder"),
            Caption("Exclude files (wildcards, separated by semicolons, e.g. *.tmp;~$*):"), _exPatterns));
        AddPage("Keyboard", Page(Caption("Hotkey that opens or hides Ultimate File Explorer from any app, while it runs or sits in the tray. " +
                "Click the box and press a key combination; Backspace turns it off."),
            HotkeyRow(), _hotkeyWin, Caption(app.HotkeyError ?? "")));

        foreach (var (name, _) in _pageList) _pages.Items.Add(name);
        _pages.SelectedIndexChanged += (_, _) => ShowPage(_pages.SelectedIndex);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 10, 6, 0) };
        body.Controls.Add(_host);
        body.Controls.Add(_pages);
        Controls.Add(body);
        Controls.Add(Buttons);
        var apply = AddButton("&Apply", DialogResult.None);
        apply.Click += (_, _) => Apply();
        AddButton("Cancel", DialogResult.Cancel);
        var ok = AddButton("OK", DialogResult.OK);
        ok.Click += (_, _) => Apply();
        ClientSize = new Size(640, 440);
        ResumeLayout(false);
        PerformLayout();

        _hotkeyWin.CheckedChanged += (_, _) => _hotkey.SetWin(_hotkeyWin.Checked);
        // The registered hotkey would swallow its own key press, so release it while a new one is recorded.
        _hotkey.Enter += (_, _) => _app.SuspendHotkey();
        _hotkey.Leave += (_, _) => _app.RegisterHotkey();
        FormClosed += (_, _) => _app.RegisterHotkey();
        LoadValues();
        _pages.SelectedIndex = 0;
    }

    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
    static Label Hint(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(21, 0, 3, 3), MaximumSize = new Size(440, 0), ForeColor = SystemColors.GrayText };
    static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 10, 3, 3), MaximumSize = new Size(440, 0) };

    static FlowLayoutPanel Page(params Control[] controls)
    {
        var p = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Visible = false,
        };
        p.Controls.AddRange(controls);
        return p;
    }

    void AddPage(string name, Control page)
    {
        _pageList.Add((name, page));
        _host.Controls.Add(page);
    }

    void ShowPage(int index)
    {
        for (int i = 0; i < _pageList.Count; i++) _pageList[i].Page.Visible = i == index;
    }

    Control AdminRow()
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 12, 0, 0), WrapContents = false };
        var index = _app.IndexManager;
        bool service = !_app.IsDemo && IndexService.IsInstalled();
        row.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new Padding(3, 7, 6, 3),
            Text = index.UsesService ? "Instant indexing: on (Ultimate File Explorer service)."
                 : index.IsElevated ? "Instant indexing: on (running as administrator)."
                 : service ? "Instant indexing: the Ultimate File Explorer service isn't answering."
                 : "Instant indexing: off (scanning folders, slower).",
        });
        if (!service && !_app.IsDemo)
        {
            var b = new Button { Text = "Turn on &instant indexing", AutoSize = true };
            b.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); _app.Post(() => _app.TurnOnInstantIndexing(null)); };
            row.Controls.Add(b);
        }
        return row;
    }

    Control FontRow()
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 12, 0, 0), WrapContents = false };
        var choose = new Button { Text = "Results &font...", AutoSize = true };
        choose.Click += (_, _) =>
        {
            using var fd = new FontDialog { ShowEffects = false, AllowVerticalFonts = false };
            try { if (_fontName.Length > 0) fd.Font = new Font(_fontName, _fontSize > 0 ? _fontSize : 9f); } catch (ArgumentException) { }
            if (fd.ShowDialog(this) != DialogResult.OK) return;
            _fontName = fd.Font.Name;
            _fontSize = fd.Font.SizeInPoints;
            UpdateFontLabel();
        };
        var reset = new Button { Text = "&Default", AutoSize = true };
        reset.Click += (_, _) => { _fontName = ""; _fontSize = 0; UpdateFontLabel(); };
        row.Controls.AddRange([choose, reset, _fontLabel]);
        return row;
    }

    Control HotkeyRow()
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 0), WrapContents = false };
        _hotkey.Margin = new Padding(3, 4, 6, 3);
        var reset = new Button { Text = $"&Default ({AppSettings.DefaultHotkey})", AutoSize = true };
        reset.Click += (_, _) =>
        {
            Hotkey.TryParse(AppSettings.DefaultHotkey, out uint mods, out Keys key);
            _hotkey.Set(mods, key);
            _hotkeyWin.Checked = (mods & Native.MOD_WIN) != 0;
        };
        row.Controls.AddRange([_hotkey, reset]);
        return row;
    }

    void UpdateFontLabel() => _fontLabel.Text = _fontName.Length == 0 ? "System default" : $"{_fontName}, {_fontSize:0.#} pt";

    Control RebuildRow()
    {
        var b = new Button { Text = "&Rebuild now", AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
        b.Click += (_, _) =>
        {
            Apply();
            _app.RebuildIndex();
        };
        return b;
    }

    Control ListButtons(ListBox list, string browseTitle)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var add = new Button { Text = "Add...", AutoSize = true };
        add.Click += (_, _) =>
        {
            using var fb = new FolderBrowserDialog { Description = browseTitle, UseDescriptionForTitle = true, ShowNewFolderButton = false };
            if (fb.ShowDialog(this) == DialogResult.OK && !list.Items.Contains(fb.SelectedPath)) list.Items.Add(fb.SelectedPath);
        };
        var remove = new Button { Text = "Remove", AutoSize = true };
        remove.Click += (_, _) =>
        {
            if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex);
        };
        row.Controls.AddRange([add, remove]);
        return row;
    }

    void LoadValues()
    {
        var s = _app.Settings;
        _runInBackground.Checked = s.RunInBackground;
        _showTray.Checked = s.ShowTrayIcon;
        _startWithWindows.Checked = s.StartWithWindows;
        _replaceExplorer.Checked = s.UseInsteadOfExplorer;
        _checkUpdates.Checked = s.CheckForUpdates;
        _filtersBar.Checked = s.FiltersVisible;
        _statusBar.Checked = s.StatusBarVisible;
        _fontName = s.FontName;
        _fontSize = s.FontSize;
        UpdateFontLabel();
        _matchCase.Checked = s.MatchCase;
        _wholeWord.Checked = s.MatchWholeWord;
        _matchPath.Checked = s.MatchPath;
        _diacritics.Checked = s.MatchDiacritics;
        _regex.Checked = s.Regex;

        var ix = s.Index;
        _ntfs.Checked = ix.IndexNtfsVolumes;
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string label = $"{d.Name} ({d.DriveFormat})" + (string.IsNullOrEmpty(d.VolumeLabel) ? "" : " " + d.VolumeLabel);
                bool on = ix.Volumes.Count == 0 || ix.Volumes.Any(v => string.Equals(v.TrimEnd('\\'), d.Name.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                _drives.Items.Add(new DriveItem(d.Name, label), on);
            }
        }
        catch (IOException) { }
        foreach (var f in ix.Folders) _folders.Items.Add(f);
        _exHidden.Checked = ix.ExcludeHidden;
        _exSystem.Checked = ix.ExcludeSystem;
        foreach (var f in ix.ExcludeFolders) _exFolders.Items.Add(f);
        _exPatterns.Text = string.Join(';', ix.ExcludePatterns);

        Hotkey.TryParse(s.Hotkey, out uint mods, out Keys key);
        _hotkey.Set(mods, key);
        _hotkeyWin.Checked = (mods & Native.MOD_WIN) != 0;
    }

    void Apply()
    {
        var s = _app.Settings;
        s.RunInBackground = _runInBackground.Checked;
        s.ShowTrayIcon = _showTray.Checked;
        s.StartWithWindows = _startWithWindows.Checked;
        s.UseInsteadOfExplorer = _replaceExplorer.Checked;
        s.CheckForUpdates = _checkUpdates.Checked;
        s.FiltersVisible = _filtersBar.Checked;
        s.StatusBarVisible = _statusBar.Checked;
        s.FontName = _fontName;
        s.FontSize = _fontSize;
        s.MatchCase = _matchCase.Checked;
        s.MatchWholeWord = _wholeWord.Checked;
        s.MatchPath = _matchPath.Checked;
        s.MatchDiacritics = _diacritics.Checked;
        s.Regex = _regex.Checked;
        s.Hotkey = Hotkey.Format(_hotkey.Modifiers, _hotkey.Key);

        var ix = s.Index;
        var volumes = new List<string>();
        if (_drives.CheckedItems.Count != _drives.Items.Count)
            foreach (DriveItem d in _drives.CheckedItems) volumes.Add(d.Root);
        var folders = _folders.Items.Cast<string>().ToList();
        var exFolders = _exFolders.Items.Cast<string>().ToList();
        var exPatterns = _exPatterns.Text.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        bool changed = ix.IndexNtfsVolumes != _ntfs.Checked
                       || !ix.Volumes.SequenceEqual(volumes, StringComparer.OrdinalIgnoreCase)
                       || !ix.Folders.SequenceEqual(folders, StringComparer.OrdinalIgnoreCase)
                       || !ix.ExcludeFolders.SequenceEqual(exFolders, StringComparer.OrdinalIgnoreCase)
                       || !ix.ExcludePatterns.SequenceEqual(exPatterns, StringComparer.OrdinalIgnoreCase)
                       || ix.ExcludeHidden != _exHidden.Checked
                       || ix.ExcludeSystem != _exSystem.Checked;
        ix.IndexNtfsVolumes = _ntfs.Checked;
        ix.Volumes = volumes;
        ix.Folders = folders;
        ix.ExcludeFolders = exFolders;
        ix.ExcludePatterns = exPatterns;
        ix.ExcludeHidden = _exHidden.Checked;
        ix.ExcludeSystem = _exSystem.Checked;

        _app.ApplySettings(changed);
    }

    sealed record DriveItem(string Root, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Captures a key combination for the global hotkey.</summary>
    sealed class HotkeyBox : TextBox
    {
        public uint Modifiers { get; private set; }
        public Keys Key { get; private set; }

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
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

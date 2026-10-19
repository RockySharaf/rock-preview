namespace Anything;

/// <summary>The setup window: one page with the choices, then progress and a finish state.</summary>
internal sealed class SetupForm : Form
{
    readonly Setup.Options _o;
    readonly Setup.Installed? _existing;
    readonly Font _ui = ExplorerTheme.UiFont(10f), _bold = ExplorerTheme.UiFont(10f, FontStyle.Bold),
                  _title = ExplorerTheme.UiFont(18f, FontStyle.Bold), _small = ExplorerTheme.UiFont(9f);
    readonly RadioButton _justMe = new() { Text = "Just me", AutoSize = true };
    readonly RadioButton _everyone = new() { Text = "Everyone on this PC (needs administrator)", AutoSize = true };
    readonly TextBox _folder = new();
    readonly Button _browse = new() { Text = "Browse...", AutoSize = true, FlatStyle = FlatStyle.System };
    readonly CheckBox _desktop = new() { Text = "Add a desktop shortcut", AutoSize = true };
    readonly CheckBox _admin = new() { Text = "Always run as administrator (instant NTFS indexing; Windows asks each time)", AutoSize = true };
    readonly CheckBox _startup = new() { Text = "Start Ultimate File Explorer when Windows starts", AutoSize = true };
    readonly CheckBox _replace = new() { Text = "Use it instead of Windows File Explorer (folders, Win+E, This PC, taskbar)", AutoSize = true };
    readonly CheckBox _service = new() { Text = "Instant indexing for all drives (Windows asks for permission once)", AutoSize = true, Checked = true };
    readonly CheckBox _updates = new() { Text = "Tell me when a new version is out (checks GitHub once a day)", AutoSize = true, Checked = true };
    readonly CheckBox _launch = new() { Text = "Open Ultimate File Explorer when setup finishes", AutoSize = true, Checked = true };
    readonly Button _install = new() { Text = "Install", AutoSize = true, FlatStyle = FlatStyle.System };
    readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, FlatStyle = FlatStyle.System };
    readonly Label _status = new() { AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, MarqueeAnimationSpeed = 30 };
    bool _done, _folderEdited;
    readonly string? _hotkey = Setup.ConfiguredHotkey();

    public SetupForm(Setup.Options o)
    {
        _o = o;
        _existing = Setup.FindInstalled();
        // Sizes are scaled here explicitly (fonts scale by themselves): logical px x DPI / 96.
        AutoScaleMode = AutoScaleMode.None;
        int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        int S(int v) => v * dpi / 96;
        Text = "Ultimate File Explorer Setup";
        Font = _ui;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        ClientSize = new Size(S(640), S(592));
        try { Icon = LoadIcon(32); } catch (Exception) { }

        // Header: logo, name, one line about it.
        var header = new Panel { Dock = DockStyle.Top, Height = S(104), BackColor = ExplorerTheme.TitleBar };
        var logo = new PictureBox { Location = new Point(S(28), S(24)), Size = new Size(S(56), S(56)), SizeMode = PictureBoxSizeMode.Zoom };
        try { logo.Image = LoadIcon(256).ToBitmap(); } catch (Exception) { }
        var title = new Label { Text = "Ultimate File Explorer", Font = _title, AutoSize = true, Location = new Point(S(100), S(20)), ForeColor = ExplorerTheme.Text };
        var sub = new Label
        {
            Text = $"Version {Setup.Version}  ·  Search, browse and preview every file instantly",
            AutoSize = true, Location = new Point(S(102), S(64)), ForeColor = ExplorerTheme.SecondaryText, Font = _small,
        };
        header.Controls.AddRange([logo, title, sub]);
        header.Paint += (_, e) => { using var p = new Pen(ExplorerTheme.Divider); e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1); };

        // Choices.
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(S(28), S(14), S(28), S(8)), ColumnCount = 1, AutoSize = false };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Label Heading(string t) => new() { Text = t, AutoSize = true, Font = _bold, Margin = new Padding(0, S(10), 0, S(4)), ForeColor = ExplorerTheme.Text };
        var scope = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        _justMe.Margin = new Padding(0, 0, S(24), 0);
        scope.Controls.AddRange([_justMe, _everyone]);
        var folderRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _folder.Dock = DockStyle.Fill;
        _folder.Margin = new Padding(0, S(2), S(8), 0);
        _browse.Margin = new Padding(0);
        folderRow.Controls.Add(_folder, 0, 0);
        folderRow.Controls.Add(_browse, 1, 0);
        foreach (var c in new Control[] { _service, _desktop, _admin, _startup, _replace, _updates, _launch }) c.Margin = new Padding(0, S(4), 0, S(4));
        body.Controls.Add(Heading("Install for"));
        body.Controls.Add(scope);
        body.Controls.Add(Heading("Folder"));
        body.Controls.Add(folderRow);
        body.Controls.Add(Heading("Options"));
        body.Controls.AddRange([_service, _desktop, _admin, _startup, _replace, _updates, _launch]);

        // Footer: status / progress on the left, buttons on the right.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = S(64), BackColor = ExplorerTheme.TitleBar };
        footer.Paint += (_, e) => { using var p = new Pen(ExplorerTheme.Divider); e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
        _cancel.Anchor = _install.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _status.Location = new Point(S(28), S(14));
        _status.Size = new Size(S(360), S(36));
        _status.ForeColor = ExplorerTheme.SecondaryText;
        _progress.Location = new Point(S(28), S(26));
        _progress.Size = new Size(S(300), S(12));
        _install.MinimumSize = _cancel.MinimumSize = new Size(S(96), S(32));
        footer.Controls.AddRange([_status, _progress, _install, _cancel]);
        footer.Layout += (_, _) =>
        {
            int pad = S(24), gap = S(8);
            _cancel.Location = new Point(footer.ClientSize.Width - pad - _cancel.Width, (footer.Height - _cancel.Height) / 2);
            _install.Location = new Point(_cancel.Left - gap - _install.Width, _cancel.Top);
        };

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);
        AcceptButton = _install;
        CancelButton = _cancel;

        // Initial state: an existing installation is updated in place; otherwise the options passed in.
        bool allUsers = _existing?.AllUsers ?? o.AllUsers;
        (allUsers ? _everyone : _justMe).Checked = true;
        _folder.Text = _existing?.Folder ?? o.TargetFolder;
        _desktop.Checked = o.DesktopShortcut || ShortcutExists(desktop: true, allUsers);
        _admin.Checked = o.RunAsAdmin;
        // The hotkey only works while Rock Preview runs, so a new install starts with Windows unless unticked.
        if (_hotkey is not null) _startup.Text = $"Start Ultimate File Explorer when Windows starts, so {_hotkey} always opens it";
        _startup.Checked = o.Auto ? o.StartWithWindows : _existing is null || Setup.StartsWithWindows();
        _replace.Checked = o.Auto ? o.ReplaceExplorer : ExplorerReplacement.IsEnabled(); // off unless the user chose it
        _updates.Checked = o.Auto ? o.CheckForUpdates ?? true : Setup.ChecksForUpdates();
        if (IndexService.IsInstalled())
        {
            _service.Text = IndexService.NeedsUpdate()
                ? "Instant indexing for all drives (on; its service gets an update, Windows asks once)"
                : "Instant indexing for all drives (already on)";
            _service.Checked = true;
            _service.Enabled = false;
        }
        else if (o.Auto) _service.Checked = o.IndexService;
        if (o.Auto) _launch.Checked = o.Launch;
        if (_existing is not null)
        {
            _install.Text = "Update";
            _status.Text = $"Ultimate File Explorer {_existing.Version} is installed here. Setup will update it.";
            _justMe.Enabled = _everyone.Enabled = _folder.Enabled = _browse.Enabled = false;
        }

        _justMe.CheckedChanged += (_, _) => { if (!_folderEdited && _existing is null) _folder.Text = Setup.DefaultFolder(_everyone.Checked); };
        _folder.TextChanged += (_, _) => { if (_folder.Focused) _folderEdited = true; };
        _browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Install Ultimate File Explorer in", UseDescriptionForTitle = true, InitialDirectory = _folder.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _folder.Text = Path.GetFileName(dlg.SelectedPath.TrimEnd('\\')).Equals(Setup.AppName, StringComparison.OrdinalIgnoreCase)
                    ? dlg.SelectedPath : Path.Combine(dlg.SelectedPath, Setup.AppName);
                _folderEdited = true;
            }
        };
        _install.Click += async (_, _) => await OnInstallClick();
        _cancel.Click += (_, _) => Close();
        Shown += async (_, _) => { if (o.Auto) await OnInstallClick(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ExplorerTheme.ApplyFrame(this); // neutral title bar, like Rock Preview's window
    }

    static Icon LoadIcon(int size)
    {
        using var s = typeof(SetupForm).Assembly.GetManifestResourceStream("RockPreview.ico")!;
        return new Icon(s, size, size);
    }

    static bool ShortcutExists(bool desktop, bool allUsers)
    {
        var folder = desktop
            ? (allUsers ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory)
            : (allUsers ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs);
        return File.Exists(Path.Combine(Environment.GetFolderPath(folder), Setup.AppName + ".lnk"));
    }

    async Task OnInstallClick()
    {
        if (_done)
        {
            if (_launch.Checked) Setup.LaunchInstalled(_o);
            Close();
            return;
        }
        _o.AllUsers = _everyone.Checked;
        _o.Folder = _folder.Text.Trim();
        _o.DesktopShortcut = _desktop.Checked;
        _o.RunAsAdmin = _admin.Checked;
        _o.StartWithWindows = _startup.Checked;
        _o.ReplaceExplorer = _replace.Checked;
        _o.IndexService = _service.Enabled && _service.Checked;
        _o.CheckForUpdates = _updates.Checked;
        _o.Launch = _launch.Checked;
        if (!Path.IsPathFullyQualified(_o.TargetFolder))
        {
            _status.Text = "Choose a full folder path, like C:\\Program Files\\Ultimate File Explorer.";
            return;
        }

        // Installing for everyone needs administrator rights: continue in an elevated copy of this setup.
        if (_o.AllUsers && !Program.IsElevated())
        {
            _o.Auto = true;
            if (Program.RelaunchElevated(_o.ToArgs())) { Close(); return; }
            _o.Auto = false;
            _status.Text = "Administrator permission is needed to install for everyone. Choose \"Just me\" to install without it.";
            return;
        }

        SetBusy(true);
        try
        {
            await Task.Run(() => Setup.Install(_o, s => BeginInvoke(() => _status.Text = s)));
            _done = true;
            _status.Text = _hotkey is not null
                ? $"Ultimate File Explorer is installed. Press {_hotkey} anytime to open it, or find it in the Start menu."
                : "Ultimate File Explorer is installed. You'll find it in the Start menu.";
            _install.Text = "Finish";
            _cancel.Visible = false;
        }
        catch (Exception ex)
        {
            _status.Text = ex is UnauthorizedAccessException
                ? "Windows didn't allow setup to write there. Choose another folder or \"Everyone\" (administrator)."
                : ex.Message;
        }
        finally
        {
            SetBusy(false);
            if (_done)
            {
                foreach (Control c in new Control[] { _justMe, _everyone, _folder, _browse, _service, _desktop, _admin, _startup, _replace, _updates }) c.Enabled = false;
                _launch.Enabled = true;
                _install.Focus();
            }
        }
    }

    void SetBusy(bool busy)
    {
        _progress.Visible = busy;
        _status.Visible = !busy || _status.Text.Length > 0;
        int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        if (busy) { _status.Location = new Point(_status.Left, 2 * dpi / 96); _status.Height = 22 * dpi / 96; }
        else { _status.Location = new Point(_status.Left, 14 * dpi / 96); _status.Height = 36 * dpi / 96; }
        UseWaitCursor = busy;
        foreach (Control c in new Control[] { _install, _cancel, _justMe, _everyone, _folder, _browse, _desktop, _admin, _startup, _replace, _updates, _launch })
            c.Enabled = !busy && (c is not RadioButton && c != _folder && c != _browse || _existing is null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _ui.Dispose(); _bold.Dispose(); _title.Dispose(); _small.Dispose(); }
        base.Dispose(disposing);
    }
}

using Anything.Core.Content;

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
    // Search inside files: three choices, each with a one-line cost under it (worked out while the window is open).
    readonly RadioButton _names = new() { Text = ContentPresentation.OptionTitle(ContentMode.NamesOnly), AutoSize = true };
    readonly RadioButton _documents = new() { Text = ContentPresentation.OptionTitle(ContentMode.Documents), AutoSize = true };
    readonly RadioButton _pictures = new() { Text = ContentPresentation.OptionTitle(ContentMode.DocumentsAndPictures), AutoSize = true };
    readonly Label _namesDetail = new(), _documentsDetail = new(), _picturesDetail = new(), _adminNote = new(), _contentFoot = new();
    readonly ToolTip _tips = new() { AutoPopDelay = 20_000 };
    readonly CheckBox _desktop = new() { Text = "Add a desktop shortcut", AutoSize = true };
    readonly CheckBox _admin = new() { Text = "Always run as administrator (not needed with instant indexing; Windows asks each time)", AutoSize = true };
    readonly CheckBox _startup = new() { Text = "Start Ultimate File Explorer when Windows starts", AutoSize = true };
    readonly CheckBox _replace = new() { Text = "Use it instead of Windows File Explorer (folders, Win+E, This PC, taskbar)", AutoSize = true };
    readonly CheckBox _service = new() { Text = "Instant indexing for all drives (Windows asks for permission once)", AutoSize = true, Checked = true };
    readonly CheckBox _updates = new() { Text = "Tell me when a new version is out (checks GitHub once a day)", AutoSize = true, Checked = true };
    readonly CheckBox _launch = new() { Text = "Open Ultimate File Explorer when setup finishes", AutoSize = true, Checked = true };
    readonly Button _install = new() { Text = "Install", AutoSize = true, FlatStyle = FlatStyle.System };
    readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, FlatStyle = FlatStyle.System };
    readonly Label _status = new() { AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, MarqueeAnimationSpeed = 30 };
    readonly CancellationTokenSource _estimateCts = new();
    readonly ContentMode? _userContent; // the choice made before (in the user's settings, installed or not), null when never made
    long _indexBytes = -1;              // the user's text index on disk now (0: none; -1: not looked at yet)
    string? _contentWaiting;            // stands in for the content choices' costs until they are counted
    string _networkNote = "";           // network folders setup doesn't count
    IReadOnlyList<string> _notByName = []; // folders searched inside that the user's name index doesn't hold: never read inside
    readonly bool _auto;                // the elevated copy, installing at once with the choices it was given
    bool _done, _folderEdited, _contentTouched, _contentLoading;
    ContentEstimate? _documentsEstimate, _picturesEstimate;
    readonly string? _hotkey = Setup.ConfiguredHotkey();

    public SetupForm(Setup.Options o)
    {
        _o = o;
        _auto = o.Auto;
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
        // Wide rather than tall, and never taller than the screen it opens on (the body scrolls if it must).
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        var frame = SizeFromClientSize(Size.Empty);
        ClientSize = new Size(Math.Min(S(760), work.Width - frame.Width), Math.Min(S(640), work.Height - frame.Height));
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
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(S(28), S(10), S(28), S(8)), ColumnCount = 1, AutoSize = false, AutoScroll = true };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // The width text can use: the body's, less its padding and room for a scroll bar.
        int inner = ClientSize.Width - S(56) - SystemInformation.VerticalScrollBarWidth;
        Label Heading(string t, bool first = false) => new() { Text = t, AutoSize = true, Font = _bold, Margin = new Padding(0, first ? 0 : S(8), 0, S(3)), ForeColor = ExplorerTheme.Text };
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

        // Search inside files: the radios have a panel of their own (radios group by parent; the scope's are separate).
        var content = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        foreach (var (radio, detail) in new[] { (_names, _namesDetail), (_documents, _documentsDetail), (_pictures, _picturesDetail) })
        {
            radio.Margin = new Padding(0, S(2), 0, 0);
            detail.AutoSize = true;
            detail.Font = _small;
            detail.ForeColor = ExplorerTheme.SecondaryText;
            detail.UseMnemonic = false;
            detail.MaximumSize = new Size(inner - S(20), 0);
            detail.Margin = new Padding(S(20), 0, 0, S(2));
            content.Controls.Add(radio);
            content.Controls.Add(detail);
        }
        _adminNote.Text = ContentPresentation.AdminNote;
        _adminNote.Visible = false;
        _contentFoot.Text = ContentPresentation.PrivacyNote + " " + ContentPresentation.ChangeLater;
        foreach (var label in new[] { _adminNote, _contentFoot })
        {
            label.AutoSize = true;
            label.Font = _small;
            label.UseMnemonic = false;
            label.ForeColor = label == _adminNote ? ExplorerTheme.Text : ExplorerTheme.SecondaryText;
            label.MaximumSize = new Size(inner, 0);
            label.Margin = new Padding(0, S(2), 0, 0);
            content.Controls.Add(label);
        }
        _tips.SetToolTip(_documents, "Reads the words in " + ContentPresentation.KindsDescription + ", in your Desktop, Documents, Downloads, Pictures and OneDrive.");
        _tips.SetToolTip(_pictures, "Also reads pictures' " + ContentPresentation.PictureDetailsDescription + ", from their headers only.");

        // Options: short ones share a row (the window is wide, not tall).
        var options = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0),
            MaximumSize = new Size(inner, 0), MinimumSize = new Size(inner, 0) };
        foreach (var c in new Control[] { _service, _startup, _replace, _admin, _updates, _desktop, _launch })
        {
            c.Margin = new Padding(0, S(2), S(24), S(2));
            options.Controls.Add(c);
        }
        body.Controls.Add(Heading("Install for", first: true));
        body.Controls.Add(scope);
        body.Controls.Add(Heading("Folder"));
        body.Controls.Add(folderRow);
        body.Controls.Add(Heading("Search inside files"));
        body.Controls.Add(content);
        body.Controls.Add(Heading("Options"));
        body.Controls.Add(options);

        // Footer: status / progress on the left, buttons on the right.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = S(64), BackColor = ExplorerTheme.TitleBar };
        footer.Paint += (_, e) => { using var p = new Pen(ExplorerTheme.Divider); e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
        _cancel.Anchor = _install.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _status.Location = new Point(S(28), S(14));
        _status.Size = new Size(S(460), S(36));
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
        // A new install gets everything that makes it work best; an update keeps the choices made before. Running as
        // administrator stays off: the instant-indexing service makes it unnecessary, and it would ask at every start.
        _desktop.Checked = o.Auto ? o.DesktopShortcut : _existing is null || ShortcutExists(desktop: true, allUsers);
        _admin.Checked = o.RunAsAdmin;
        // The hotkey only works while Rock Preview runs, so a new install starts with Windows unless unticked.
        if (_hotkey is not null) _startup.Text = $"Start Ultimate File Explorer when Windows starts, so {_hotkey} always opens it";
        _startup.Checked = o.Auto ? o.StartWithWindows : _existing is null || Setup.StartsWithWindows();
        _replace.Checked = o.Auto ? o.ReplaceExplorer : _existing is null || ExplorerReplacement.IsEnabled();
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
        // Search inside files: the choice made before, wherever it was made (an installed copy, the portable one, or before
        // an uninstall that kept the settings); names only when none was.
        _userContent = o.Auto ? null : Setup.ContentModeOfUser();
        var seeded = o.Auto ? o.Content ?? ContentMode.NamesOnly : _userContent ?? ContentMode.NamesOnly;
        _contentLoading = true;
        (seeded switch { ContentMode.Documents => _documents, ContentMode.DocumentsAndPictures => _pictures, _ => _names }).Checked = true;
        _contentLoading = false;
        // The elevated copy installs at once, and an elevated window would count the administrator's folders: no numbers there.
        bool counting = !o.Auto && !Program.IsElevated();
        _contentWaiting = counting ? "Counting your files…" : "Sizes are shown in Settings › Indexing.";
        // The count leaves out what the name index doesn't hold (an update keeps its exclusions): the documents choice says
        // which of the folders searched inside that leaves out. Not in an elevated window (the administrator's settings).
        if (counting) _notByName = Setup.ContentFoldersNotSearchedByName();
        ShowContentDetails();

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
        foreach (var radio in new[] { _names, _documents, _pictures })
            radio.CheckedChanged += (_, _) =>
            {
                if (!_contentLoading) _contentTouched = true;
                UpdateAdminNote();
            };
        _admin.CheckedChanged += (_, _) => UpdateAdminNote();
        UpdateAdminNote();
        _install.Click += async (_, _) => await OnInstallClick();
        _cancel.Click += (_, _) => Close();
        FormClosed += (_, _) => _estimateCts.Cancel();
        Shown += async (_, _) =>
        {
            if (o.Auto)
            {
                await OnInstallClick();
                return;
            }
            _ = MeasureIndex();
            if (counting) _ = EstimateContent();
        };
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
        return File.Exists(Path.Combine(Environment.GetFolderPath(folder), Setup.AppName + ".lnk"))
            || File.Exists(Path.Combine(Environment.GetFolderPath(folder), Setup.OldShortcutName + ".lnk"));
    }

    // ---- search inside files ---------------------------------------------------------------------

    /// <summary>
    /// The three cost lines (the content choices' say they're being counted until they are); the documents one ends by
    /// naming the folders searched inside that aren't searched by name, and so are left out.
    /// </summary>
    void ShowContentDetails()
    {
        var none = new ContentSettings();
        _namesDetail.Text = ContentPresentation.OptionDetail(ContentMode.NamesOnly,
            _indexBytes > 0 ? new ContentEstimate(ContentMode.NamesOnly, 0, 0, _indexBytes, TimeSpan.Zero, 0, false) : null, none);
        _documentsDetail.Text = (_contentWaiting ?? ContentPresentation.OptionDetail(ContentMode.Documents, _documentsEstimate, none) + _networkNote)
            + ContentPresentation.LeftOutClause(_notByName);
        _picturesDetail.Text = _contentWaiting ?? ContentPresentation.OptionDetail(ContentMode.DocumentsAndPictures, _picturesEstimate, none);
    }

    void UpdateAdminNote()
    {
        bool show = _admin.Checked && (_documents.Checked || _pictures.Checked);
        if (_adminNote.Visible != show) _adminNote.Visible = show;
    }

    /// <summary>
    /// Looks for the user's text index on disk (in the background: a moved database can be on a share), so names only
    /// says what it deletes. One kept from before with no choice made leaves every choice unpicked: whatever is picked
    /// then is a choice, and nothing is written (or deleted) while none is.
    /// </summary>
    async Task MeasureIndex()
    {
        long bytes;
        try { bytes = await Task.Run(Setup.ContentIndexBytesOfUser, _estimateCts.Token); }
        catch (OperationCanceledException) { return; }
        if (IsDisposed) return;
        _indexBytes = bytes;
        if (bytes > 0 && _userContent is null && !_contentTouched && _names.Checked)
        {
            _contentLoading = true;
            _names.Checked = false;
            _contentLoading = false;
        }
        ShowContentDetails();
    }

    /// <summary>
    /// Counts the user's own folders (no administrator, no service: plain folder listing, 20 seconds at most), reads a
    /// short sample through the real readers (4 seconds), then shows each choice's size and first pass. Install never
    /// waits for it.
    /// </summary>
    async Task EstimateContent()
    {
        var ct = _estimateCts.Token;
        try
        {
            var app = AppSettings.Load(AppSettings.DefaultPath(demo: false));
            var settings = app.Content.Clone();
            settings.Mode = ContentMode.DocumentsAndPictures; // count pictures too; each choice takes its part
            settings.ModeExplicit = true;
            // The user's own scope: My folders when they're used, plus the folders added, nested ones counted once.
            var folders = new List<string>();
            var network = new List<string>();
            foreach (var f in settings.EffectiveFolders()) (IsNetwork(f) ? network : folders).Add(f);
            var progress = new Progress<ContentCensus>(c =>
            {
                if (IsDisposed || ct.IsCancellationRequested) return;
                int n = c.Kinds.Values.Sum(k => k.Files);
                _contentWaiting = $"Counting your files… ({n:N0} so far)";
                ShowContentDetails();
            });
            // Only what the name index holds is ever read inside: its own exclusions count too (an update keeps them).
            var census = await Task.Run(() => ContentEstimator.CountFoldersAsync(folders, settings, TimeSpan.FromSeconds(20), progress, ct, app.Index), ct);
            var calibration = await Task.Run(() => ContentEstimator.Calibrate(census, ContentServices.Files(), TimeSpan.FromSeconds(4), ct), ct);
            if (IsDisposed || ct.IsCancellationRequested) return;
            _documentsEstimate = ContentEstimator.Estimate(census, calibration, ContentMode.Documents, settings);
            _picturesEstimate = ContentEstimator.Estimate(census, calibration, ContentMode.DocumentsAndPictures, settings);
            _networkNote = network.Count > 0 ? $" Plus {string.Join(", ", network)}: counted in Settings › Indexing." : "";
            _contentWaiting = null;
            ShowContentDetails();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (IsDisposed) return;
            _contentWaiting = "Sizes are shown in Settings › Indexing.";
            ShowContentDetails();
        }
    }

    /// <summary>A network folder (a share, or on a mapped drive): Settings counts it, setup doesn't wait on the network.</summary>
    static bool IsNetwork(string folder)
    {
        if (folder.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try { return Path.GetPathRoot(folder) is { Length: > 0 } root && Anything.Core.Indexing.Drives.KindOf(root) == Anything.Core.DriveKind.Network; }
        catch (Exception) { return true; }
    }

    /// <summary>
    /// The choice to write (null: leave the user's settings as they are). A radio the user picked is written. Left as the
    /// window showed it, nothing is written: the choice made before stays; and with none made, names only written now
    /// would delete a text index kept from before (or one not looked for yet) that nobody chose to delete, and on an
    /// update the app can still offer the choice. Only a new install with no index writes the names-only default.
    /// </summary>
    ContentMode? ChosenContent()
    {
        ContentMode? picked = _documents.Checked ? ContentMode.Documents : _pictures.Checked ? ContentMode.DocumentsAndPictures
            : _names.Checked ? ContentMode.NamesOnly : null;
        if (picked is null || _contentTouched) return picked;
        if (_userContent is not null || _existing is not null || _indexBytes != 0) return null;
        return picked;
    }

    // ---- install ---------------------------------------------------------------------------------

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
        if (!_auto) _o.Content = ChosenContent(); // the elevated copy keeps what the setup window chose
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
            _o.AsUser = Setup.CurrentUserSid(); // the choices belong to this user, whoever's password elevates
            if (Program.RelaunchElevated(_o.ToArgs())) { Close(); return; }
            _o.Auto = false;
            _o.AsUser = null;
            _status.Text = "Administrator permission is needed to install for everyone. Choose \"Just me\" to install without it.";
            return;
        }

        _estimateCts.Cancel(); // counting files is for choosing; installing doesn't wait for it
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
                foreach (Control c in new Control[] { _justMe, _everyone, _folder, _browse, _service, _desktop, _admin, _startup, _replace, _updates, _names, _documents, _pictures })
                    c.Enabled = false;
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
        // The search-inside-files choice can change on an update too (unlike where it's installed).
        foreach (var radio in new[] { _names, _documents, _pictures }) radio.Enabled = !busy;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _ui.Dispose(); _bold.Dispose(); _title.Dispose(); _small.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}

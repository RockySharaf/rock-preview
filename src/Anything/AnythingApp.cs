using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Anything.Core;
using Anything.Core.Indexing;
using Anything.Demo;
using Anything.Interop;

namespace Anything;

/// <summary>
/// Process-wide state: the shared index and search engine, settings, the search windows, the tray icon,
/// the global hotkey and the hidden message window that receives forwarded command lines.
/// </summary>
internal sealed class AnythingApp : ApplicationContext
{
    readonly List<MainForm> _windows = [];
    readonly SynchronizationContext _sync;
    readonly MessageWindow _messageWindow;
    MainForm? _lastActive;
    NotifyIcon? _tray;
    NotifyIcon? _notifier; // stands in for the tray icon (turned off) while a notification shows
    FileIndex? _subscribedIndex;
    SettingsForm? _settings;
    GetStartedForm? _welcome;
    System.Windows.Forms.Timer? _updateTimer;
    Task<Version?>? _updateCheck;
    Version? _updateNoticeHidden;
    int _changePending, _statePending, _drivesSeq;
    bool _exiting;

    public AppSettings Settings { get; }
    public IIndexManager IndexManager { get; }
    public ISearchEngine SearchEngine { get; }
    public bool IsDemo { get; }
    public Icon AppIcon { get; }
    public string? HotkeyError { get; private set; }
    public bool Exiting => _exiting;
    public IReadOnlyList<MainForm> Windows => _windows;
    /// <summary>
    /// The drives Windows had when they were last listed (at startup, when index settings change, and when a fresh look
    /// at a folder's drive found the list old); empty until then.
    /// </summary>
    public IReadOnlyList<DriveEntry> KnownDrives { get; private set; } = [];
    /// <summary>Drives that Folders › Leave these folders out leaves out whole and that would be searched otherwise (the windows' bar).</summary>
    public IReadOnlyList<string> LeftOutDrives { get; private set; } = [];

    public static AnythingApp? Create(CommandLine cmd)
    {
        IIndexManager index;
        ISearchEngine engine;
        try
        {
            if (cmd.Demo)
            {
                index = new DemoIndexManager();
                engine = new DemoSearchEngine();
            }
            else
            {
                index = Services.CreateIndexManager();
                engine = Services.CreateSearchEngine();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ultimate File Explorer could not start its indexing services.\n\n{ex.Message}\n\nRun with --demo to try the interface with a sample index.",
                "Ultimate File Explorer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        return new AnythingApp(cmd, index, engine);
    }

    AnythingApp(CommandLine cmd, IIndexManager index, ISearchEngine engine)
    {
        _sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        IsDemo = cmd.Demo;
        IndexManager = index;
        SearchEngine = engine;
        Settings = AppSettings.Load(AppSettings.DefaultPath(cmd.Demo));
        if (!cmd.Demo) ExplorerReplacement.RepairIfMissing(Environment.ProcessPath); // folders must never point at a deleted copy
        AppIcon = LoadIcon();

        ShellInfo.Initialize();
        ShellInfo.IconsResolved += () => Post(() => { foreach (var w in _windows) w.OnIconsResolved(); });
        DiskDates.Resolved += () => Post(() => { foreach (var w in _windows) w.OnDatesResolved(); });

        _messageWindow = new MessageWindow(this, Program.MessageWindowTitle + (cmd.Demo ? ".Demo" : ""));

        IndexManager.StateChanged += OnStateChangedBackground;
        SubscribeIndex();
        if (!cmd.Demo) LoadRunHistory();
        try { IndexManager.Start(Settings.Index); }
        catch (Exception ex)
        {
            MessageBox.Show($"Indexing could not be started.\n\n{ex.Message}", "Ultimate File Explorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        SubscribeIndex();
        // A card put in, a drive unlocked or a SUBST made: the folder bar's reasons follow the drives the index sees.
        IndexManager.DrivesChanged += () => Post(RefreshDrives);

        UpdateTray();
        RegisterHotkey();
        HandleCommandLine(cmd, initial: true);
        StartUpdateChecks();
        RefreshDrives();
    }

    static Icon LoadIcon()
    {
        try
        {
            using var s = typeof(AnythingApp).Assembly.GetManifestResourceStream("RockPreview.ico");
            if (s is not null) return new Icon(s);
        }
        catch { }
        return (Icon)SystemIcons.Application.Clone();
    }

    public string Title => IsDemo ? "Ultimate File Explorer (Demo)" : "Ultimate File Explorer";

    /// <summary>The global hotkey registered right now ("Alt+X"), or null when none is set or it's taken.</summary>
    public string? ActiveHotkey { get; private set; }

    /// <summary>The name followed by the key that opens it from anywhere: "Rock Preview (Alt+X)".</summary>
    public string TitleWithHotkey => ActiveHotkey is not { } h ? Title : IsDemo ? $"Ultimate File Explorer (Demo, {h})" : $"Ultimate File Explorer ({h})";

    public void Post(Action a) => _sync.Post(_ => { try { a(); } catch (ObjectDisposedException) { } }, null);

    // ---- run history ----------------------------------------------------------------------------

    /// <summary>"Run History.csv" beside the settings, as Everything keeps it beside Everything.ini.</summary>
    public static string RunHistoryPath => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath(false))!, "Run History.csv");

    /// <summary>Everything's own run history, which <see cref="ImportEverythingRunHistory"/> brings in.</summary>
    public static string EverythingRunHistoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everything", "Run History.csv");

    void LoadRunHistory()
    {
        var history = IndexManager.Index.RunHistory;
        history.Load(RunHistoryPath);
        history.Changed += OnRunHistoryChanged;
    }

    void OnRunHistoryChanged()
    {
        // Saved at once (a small file); the windows search again to show the new counts.
        try { IndexManager.Index.RunHistory.Save(RunHistoryPath); } catch (Exception) { }
        OnIndexChangedBackground();
    }

    /// <summary>Counts a run of each path opened from the results (Run Count, Date Run), unless run history is off.</summary>
    public void RecordRuns(IEnumerable<string> paths)
    {
        if (IsDemo || !Settings.RunHistoryEnabled) return;
        long now = DateTime.UtcNow.ToFileTimeUtc();
        foreach (var p in paths) IndexManager.Index.RunHistory.Increment(p, now);
    }

    /// <summary>Adds Everything's run history to ours (higher count and later date win). Returns how many files changed, -1 when there is none.</summary>
    public int ImportEverythingRunHistory()
    {
        if (IsDemo || !File.Exists(EverythingRunHistoryPath)) return -1;
        return IndexManager.Index.RunHistory.Import(EverythingRunHistoryPath);
    }

    // ---- index events -------------------------------------------------------------------------

    void SubscribeIndex()
    {
        var idx = IndexManager.Index;
        if (ReferenceEquals(idx, _subscribedIndex)) return;
        if (_subscribedIndex is not null) _subscribedIndex.Changed -= OnIndexChangedBackground;
        _subscribedIndex = idx;
        if (idx is not null) idx.Changed += OnIndexChangedBackground;
    }

    void OnIndexChangedBackground()
    {
        if (Interlocked.Exchange(ref _changePending, 1) != 0) return;
        Post(() =>
        {
            Volatile.Write(ref _changePending, 0);
            foreach (var w in _windows.ToArray()) w.OnIndexChanged();
        });
    }

    void OnStateChangedBackground()
    {
        if (Interlocked.Exchange(ref _statePending, 1) != 0) return;
        Post(() =>
        {
            Volatile.Write(ref _statePending, 0);
            SubscribeIndex();
            foreach (var w in _windows.ToArray()) w.OnIndexStateChanged();
        });
    }

    // ---- command line / windows ----------------------------------------------------------------

    public void HandleCommandLine(CommandLine cmd, bool initial)
    {
        if (_exiting) return;
        if (cmd.Exit) { ExitApp(); return; }
        if (cmd.Startup && cmd.Search is null && cmd.Path is null && !cmd.NewWindow && !cmd.Explorer)
        {
            if (initial) return; // start hidden in the tray
        }
        if (cmd.Explorer)
        {
            // Started in place of File Explorer (Win+E, opening a folder or drive): a new tab there in the window used
            // last, like Explorer's "open folders in a new tab", or (Settings › General) a new window, like Explorer's.
            if (Settings.OpenFoldersInTabs && RecentWindow() is { } recent)
            {
                recent.NewTab(cmd.Path);
                ShowWindow(recent);
                return;
            }
            var ew = CreateWindow(_windows.Count > 0 ? _lastActive : null);
            ew.OpenAsExplorer(cmd.Path);
            ShowWindow(ew);
            return;
        }
        MainForm w = cmd.NewWindow && _windows.Count > 0 && !initial
            ? CreateWindow(_lastActive)
            : _lastActive ?? _windows.LastOrDefault() ?? CreateWindow(null);
        if (cmd.Search is not null) w.SetSearch(cmd.Search);
        ShowWindow(w);
        if (cmd.Path is not null) w.BrowseTo(cmd.Path);
        ShowWelcomeOnce();
    }

    /// <summary>The window used last among those on screen (a window closed into the background doesn't count).</summary>
    MainForm? RecentWindow() => _lastActive is { Visible: true, IsDisposed: false } w ? w : _windows.LastOrDefault(x => x.Visible);

    public MainForm CreateWindow(MainForm? source)
    {
        var w = new MainForm(this, source);
        _windows.Add(w);
        w.Activated += (_, _) => _lastActive = w;
        w.FormClosed += (_, _) =>
        {
            _windows.Remove(w);
            if (_lastActive == w) _lastActive = _windows.LastOrDefault();
            if (_windows.Count == 0 && !_exiting && !Settings.RunInBackground) ExitApp();
        };
        _lastActive ??= w;
        return w;
    }

    public void NewWindow(MainForm? source) => ShowWindow(CreateWindow(source));

    public void ShowWindow(MainForm w)
    {
        if (!w.Visible) w.Show();
        if (w.WindowState == FormWindowState.Minimized) w.WindowState = FormWindowState.Normal;
        w.Activate();
        Native.SetForegroundWindow(w.Handle);
        w.FocusSearch(selectAll: true);
        _lastActive = w;
    }

    public void ShowSearchWindow()
    {
        var w = _lastActive ?? _windows.LastOrDefault() ?? CreateWindow(null);
        ShowWindow(w);
    }

    /// <summary>Called from MainForm.FormClosing. Decides between closing, hiding to the background and exiting.</summary>
    public void OnWindowClosing(MainForm w, FormClosingEventArgs e)
    {
        w.SaveStateTo(Settings);
        if (_exiting || w.ForceClose) return;
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            e.Cancel = true;
            Post(ExitApp);
            return;
        }
        Settings.Save();
        bool othersVisible = _windows.Any(x => x != w && x.Visible);
        if (othersVisible) return; // just close this one
        e.Cancel = true;
        if (Settings.RunInBackground)
        {
            w.Hide();
            foreach (var other in _windows.Where(x => x != w && !x.Visible).ToArray())
            {
                other.ForceClose = true;
                other.Close();
            }
        }
        else
        {
            Post(ExitApp);
        }
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        foreach (var w in _windows.Where(x => x != _lastActive)) w.SaveStateTo(Settings);
        _lastActive?.SaveStateTo(Settings);
        Settings.Save();
        foreach (var w in _windows.ToArray())
        {
            try { w.Close(); w.Dispose(); } catch { }
        }
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _updateTimer?.Dispose();
        CloseNotifier();
        _welcome?.Close();
        _messageWindow.UnregisterHotkey();
        _messageWindow.DestroyHandle();
        try { IndexManager.Save(); } catch { }
        try { IndexManager.Dispose(); } catch { }
        ExitThread();
    }

    /// <summary>Saves everything, exits and starts again (used to apply settings that only take effect at startup).</summary>
    public void Restart()
    {
        var args = new List<string> { "-restarted" };
        if (IsDemo) args.Add("--demo");
        if (Program.Relaunch(args)) ExitApp();
    }

    /// <summary>
    /// Turns on instant indexing for good: installs (or updates) the Rock Preview service (Windows asks for permission
    /// this once), then restarts indexing from the database so the drives are read through it: what offline network
    /// drives had is kept, and everything stays searchable meanwhile. Rock Preview itself keeps running as a normal app.
    /// </summary>
    public async void TurnOnInstantIndexing(IWin32Window? owner)
    {
        if (IsDemo) return;
        int code;
        if (Program.IsElevated())
        {
            code = await Task.Run(() =>
            {
                try { IndexService.Install(Environment.ProcessPath!, System.Security.Principal.WindowsIdentity.GetCurrent().User!); return 0; }
                catch (Exception) { return 1; }
            });
        }
        else
        {
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            code = await Task.Run(() => Program.RunElevatedAndWait(["-installservice", "-for", user]));
        }
        if (code == -1) return; // the permission prompt was declined: nothing changed
        if (code != 0)
        {
            MessageBox.Show(owner, "Ultimate File Explorer couldn't turn on instant indexing.", "Ultimate File Explorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        for (int i = 0; i < 40 && !Anything.Core.Indexing.VolumeService.IsRunning(); i++) await Task.Delay(250);
        try { IndexManager.Start(Settings.Index); }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Unable to restart indexing.\n\n{ex.Message}", "Ultimate File Explorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        SubscribeIndex();
        foreach (var w in _windows) w.OnIndexStateChanged();
    }

    public void RestartElevated()
    {
        var args = new List<string> { "-restarted" };
        if (IsDemo) args.Add("--demo");
        if (Program.RelaunchElevated(args)) ExitApp();
    }

    // ---- tray / hotkey / options ------------------------------------------------------------

    public void UpdateTray()
    {
        if (Settings.ShowTrayIcon)
        {
            if (_tray is not null) return;
            var menu = new ContextMenuStrip();
            var show = new ToolStripMenuItem("&Search Ultimate File Explorer...", null, (_, _) => ShowSearchWindow()) { ShortcutKeyDisplayString = ActiveHotkey };
            show.Font = new Font(show.Font, FontStyle.Bold);
            menu.Items.Add(show);
            menu.Items.Add(new ToolStripMenuItem("&New Search Window", null, (_, _) => NewWindow(_lastActive)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("&Settings...", null, (_, _) => ShowSettings(null)));
            menu.Items.Add(new ToolStripMenuItem("", null, (_, _) => OpenReleasePage()) { Name = "update", Visible = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("E&xit", null, (_, _) => ExitApp()));
            _tray = new NotifyIcon
            {
                Icon = new Icon(AppIcon, SystemInformation.SmallIconSize),
                Text = TitleWithHotkey,
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSearchWindow(); };
            _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSearchWindow(); };
            _tray.BalloonTipClicked += (_, _) => OpenReleasePage(); // its only notification: a new version
            UpdateTrayNotice();
        }
        else if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
    }

    public void RegisterHotkey()
    {
        _messageWindow.UnregisterHotkey();
        HotkeyError = null;
        ActiveHotkey = null;
        if (Hotkey.TryParse(Settings.Hotkey, out uint mods, out Keys key))
        {
            // The demo leaves the hotkey to a normal copy that is running (it would only take it away from it).
            bool leaveToRunningCopy = IsDemo && Program.RunningInstanceId() is int other && other != Environment.ProcessId;
            if (leaveToRunningCopy) { }
            else if (_messageWindow.RegisterHotkey(mods, key)) ActiveHotkey = Hotkey.Format(mods, key);
            else if (!HeldByAnotherCopy())
                HotkeyError = $"The hotkey {Settings.Hotkey} is already in use by another program. Choose another in Settings › Keyboard.";
        }
        if (_tray is not null)
        {
            _tray.Text = TitleWithHotkey;
            if (_tray.ContextMenuStrip?.Items[0] is ToolStripMenuItem show) show.ShortcutKeyDisplayString = ActiveHotkey;
        }
        foreach (var w in _windows) w.UpdateTitle();
    }

    /// <summary>
    /// Whether another copy of Ultimate File Explorer (a demo next to the normal one, or the normal one next to a demo)
    /// is running: then the hotkey is ours already, not another program's, and there is nothing to complain about.
    /// </summary>
    bool HeldByAnotherCopy()
    {
        int self = Environment.ProcessId;
        return Program.RunningInstanceId() is int normal && normal != self
               || Program.RunningInstanceId(demo: true) is int demo && demo != self;
    }

    /// <summary>Releases the hotkey while Options records a new one, so that pressing it there is seen.</summary>
    public void SuspendHotkey() => _messageWindow.UnregisterHotkey();

    /// <summary>Something the getting-started window teaches was just done.</summary>
    public event Action<GettingStarted>? StepTried;

    public void Tried(GettingStarted step) => StepTried?.Invoke(step);

    internal void OnHotkey()
    {
        IntPtr fg = Native.GetForegroundWindow();
        var active = _windows.FirstOrDefault(x => x.Visible && x.IsHandleCreated && x.Handle == fg);
        if (active is not null) active.Hide();
        else ShowSearchWindow();
        Tried(active is null ? GettingStarted.Show : GettingStarted.Hide);
    }

    /// <summary>
    /// Once per user, the first time a window opens (normally right after setup): the getting-started window in the
    /// corner of the screen.
    /// </summary>
    void ShowWelcomeOnce()
    {
        if (IsDemo || Settings.WelcomeShown || _welcome is not null || ActiveHotkey is null) return;
        Settings.WelcomeShown = true;
        Settings.Save();
        Post(ShowGetStarted);
    }

    /// <summary>The getting-started window (Help › Get Started, Settings › About).</summary>
    public void ShowGetStarted()
    {
        if (_exiting) return;
        if (_welcome is { IsDisposed: false }) return;
        _welcome = new GetStartedForm(this, ActiveHotkey ?? (Settings.Hotkey.Length > 0 ? Settings.Hotkey : "the hotkey"));
        _welcome.FormClosed += (_, _) => _welcome = null;
        _welcome.ShowNear(_lastActive);
    }

    // ---- new versions -------------------------------------------------------------------------

    public static Version CurrentVersion => typeof(AnythingApp).Assembly.GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);

    /// <summary>A newer Rock Preview on GitHub (from the latest check), or null.</summary>
    public Version? NewVersion { get; private set; }

    /// <summary>Whether windows show the new-version bar ("Not now" hides it until Rock Preview starts again).</summary>
    public bool ShowUpdateNotice => NewVersion is not null && NewVersion != _updateNoticeHidden;

    /// <summary>Tells about a version found earlier right away, then asks GitHub when a day has passed (looked at hourly).</summary>
    void StartUpdateChecks()
    {
        if (IsDemo) return;
        if (Settings.CheckForUpdates && UpdateCheck.ParseTag(Settings.LatestVersion) is { } known && UpdateCheck.IsNewer(known, CurrentVersion))
            SetNewVersion(known);
        _updateTimer = new System.Windows.Forms.Timer { Interval = 20_000 };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = 60 * 60 * 1000;
            await CheckForUpdatesIfDue();
        };
        _updateTimer.Start();
    }

    async Task CheckForUpdatesIfDue()
    {
        if (IsDemo || _exiting || !Settings.CheckForUpdates || DateTime.UtcNow - Settings.LastUpdateCheck < TimeSpan.FromDays(1)) return;
        try { await CheckForUpdatesNow(); }
        catch (Exception) { /* offline, or GitHub didn't answer: the next hourly look tries again */ }
    }

    /// <summary>Asks GitHub now for the latest release (null when there is none). Throws when GitHub can't be reached.</summary>
    public async Task<Version?> CheckForUpdatesNow()
    {
        if (_updateCheck is { } running) return await running;
        var check = UpdateCheck.FetchLatestAsync(Setup.Version, CancellationToken.None);
        _updateCheck = check;
        Version? latest;
        try { latest = await check; }
        finally { _updateCheck = null; }
        if (_exiting) return latest;
        Settings.LastUpdateCheck = DateTime.UtcNow;
        Settings.LatestVersion = latest?.ToString(3) ?? "";
        Settings.Save();
        SetNewVersion(UpdateCheck.IsNewer(latest, CurrentVersion) ? latest : null);
        return latest;
    }

    /// <summary>Shows or clears the notice everywhere; a new version also gets one Windows notification.</summary>
    void SetNewVersion(Version? version)
    {
        NewVersion = version;
        UpdateTrayNotice();
        foreach (var w in _windows) w.UpdateUpdateBar();
        if (version is null || Settings.NotifiedVersion == version.ToString(3)) return;
        Settings.NotifiedVersion = version.ToString(3);
        Settings.Save();
        Notify($"Ultimate File Explorer {version.ToString(3)} is available", $"You have {CurrentVersion.ToString(3)}.");
    }

    /// <summary>
    /// A Windows notification that opens the new version's page when clicked. Should Windows refuse it, a tray balloon
    /// instead (from a tray icon that is there just for it when the tray icon is turned off).
    /// </summary>
    void Notify(string title, string text)
    {
        if (!IsDemo && NewVersion is { } v && Toast.TryShow(title, text + " See what's new and get it.", UpdateCheck.ReleasePage(v), "See what's new"))
            return;
        text += " Click here to see what's new and get it.";
        if (_tray is not null)
        {
            _tray.ShowBalloonTip(15_000, title, text, ToolTipIcon.Info);
            return;
        }
        CloseNotifier();
        var icon = new NotifyIcon { Icon = new Icon(AppIcon, SystemInformation.SmallIconSize), Text = Title, Visible = true };
        icon.BalloonTipClicked += (_, _) => { OpenReleasePage(); Post(CloseNotifier); };
        icon.BalloonTipClosed += (_, _) => Post(CloseNotifier);
        _notifier = icon;
        icon.ShowBalloonTip(15_000, title, text, ToolTipIcon.Info);
    }

    void CloseNotifier()
    {
        if (_notifier is not { } icon) return;
        _notifier = null;
        icon.Visible = false;
        icon.Icon?.Dispose();
        icon.Dispose();
    }

    void UpdateTrayNotice()
    {
        if (_tray?.ContextMenuStrip?.Items["update"] is not ToolStripMenuItem item) return;
        item.Visible = NewVersion is not null;
        if (NewVersion is { } v) item.Text = $"&Get Ultimate File Explorer {v.ToString(3)}...";
    }

    public void HideUpdateNotice()
    {
        _updateNoticeHidden = NewVersion;
        foreach (var w in _windows) w.UpdateUpdateBar();
    }

    /// <summary>The new version's page on GitHub: what's new, and the installer.</summary>
    public void OpenReleasePage()
    {
        if (NewVersion is { } v) OpenWebPage(UpdateCheck.ReleasePage(v));
    }

    /// <summary>Help › Check for Updates, Settings › About: asks GitHub now and says what it found.</summary>
    public async void CheckForUpdatesInteractive(IWin32Window? owner)
    {
        var current = CurrentVersion;
        Version? latest;
        try { latest = await CheckForUpdatesNow(); }
        catch (Exception)
        {
            if (owner is Control { IsDisposed: true }) return;
            TaskDialog.ShowDialog(owner!, new TaskDialogPage
            {
                Caption = Title,
                Heading = "Couldn't check for a new version",
                Text = "Ultimate File Explorer couldn't reach GitHub. Check your internet connection and try again.",
                Icon = TaskDialogIcon.Warning,
            });
            return;
        }
        if (owner is Control { IsDisposed: true }) return;
        if (UpdateCheck.IsNewer(latest, current))
        {
            var get = new TaskDialogButton("See what's new and get it");
            var page = new TaskDialogPage
            {
                Caption = Title,
                Heading = $"Ultimate File Explorer {latest!.ToString(3)} is available",
                Text = $"You have {current.ToString(3)}. Its page on GitHub has what's new and the installer.",
                Icon = TaskDialogIcon.Information,
                Buttons = { get, new TaskDialogButton("Not now") },
                DefaultButton = get,
            };
            if (TaskDialog.ShowDialog(owner!, page) == get) OpenReleasePage();
        }
        else
        {
            TaskDialog.ShowDialog(owner!, new TaskDialogPage
            {
                Caption = Title,
                Heading = "Ultimate File Explorer is up to date",
                Text = $"You have the latest version, {current.ToString(3)}.",
                Icon = TaskDialogIcon.Information,
            });
        }
    }

    /// <summary>Opens a page in the default browser, with normal rights even when Rock Preview runs as administrator.</summary>
    internal static void OpenWebPage(string url)
    {
        try
        {
            if (Program.IsElevated()) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = false })?.Dispose();
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception) { }
    }

    /// <summary>The Settings window, at <paramref name="section"/> (General when null).</summary>
    public void ShowSettings(IWin32Window? owner, string? section = null)
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsForm(this, section);
        try { _settings.ShowDialog(owner); }
        finally { _settings.Dispose(); _settings = null; }
    }

    /// <summary>
    /// Applies changed settings everywhere. Changed index settings go to the index, which adds or drops only the drives
    /// and folders that changed and keeps everything else searchable meanwhile. <paramref name="rebuildIndex"/>
    /// (Settings › Indexing › Rebuild now) discards the index and scans everything again instead.
    /// </summary>
    public void ApplySettings(bool indexChanged, bool rebuildIndex = false)
    {
        Settings.Save();
        if (!IsDemo)
        {
            Settings.ApplyStartWithWindows();
            Settings.ApplyExplorerReplacement();
        }
        UpdateTray();
        RegisterHotkey();
        _coverage.Clear();
        FindLeftOutDrives();
        foreach (var w in _windows) w.ApplyUiSettings();
        foreach (var w in _windows) w.UpdateFolderBar();
        if (!IsDemo)
        {
            if (!Settings.CheckForUpdates) { if (NewVersion is not null) SetNewVersion(null); }
            else _ = CheckForUpdatesIfDue();
        }
        if (rebuildIndex) RebuildIndex();
        else if (indexChanged) ApplyIndexSettings();
        if (indexChanged || rebuildIndex) RefreshDrives();
    }

    /// <summary>Hands the index the changed settings (it keeps its own copy): it returns at once and works in the background.</summary>
    void ApplyIndexSettings()
    {
        if (IsDemo) return; // the sample index doesn't follow the settings (and would only be added again)
        try { IndexManager.Apply(Settings.Index); }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to apply the new index settings.\n\n{ex.Message}", "Ultimate File Explorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        SubscribeIndex();
    }

    // ---- drives left out ----------------------------------------------------------------------

    /// <summary>Lists the drives in the background (a network drive can take a moment to answer), then updates the windows' bar.</summary>
    void RefreshDrives()
    {
        int seq = ++_drivesSeq;
        Task.Run(() => Drives.List()).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Post(() =>
            {
                if (seq != _drivesSeq || _exiting) return; // a newer listing is on its way
                KnownDrives = t.Result;
                _coverage.Clear();
                FindLeftOutDrives();
                foreach (var w in _windows) w.UpdateFolderBar(); // and the left-out drive bar
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Which drives are left out whole by Folders › Leave these folders out (C:\ on the list, say) although they would
    /// be searched otherwise, leaving out the ones the user chose to keep left out and those added to Also search
    /// these folders, which are searched anyway.
    /// </summary>
    void FindLeftOutDrives()
    {
        var found = new List<string>();
        if (!IsDemo)
        {
            var ix = Settings.Index;
            var filter = new ExclusionFilter(ix);
            var leftOut = new List<string>(); // found or kept left out: what is mounted in their folders is left out with them
            // Drive letters first, so a drive comes before the volumes mounted in its folders.
            foreach (var d in KnownDrives.OrderBy(d => d.IsDriveLetter ? 0 : 1).ThenBy(d => d.Root, StringComparer.OrdinalIgnoreCase))
            {
                if (IndexPlan.ForDrive(ix, d, filter) != DriveIndexing.LeftOut || Settings.IsSearchedAsFolder(d.Root)) continue;
                bool inside = leftOut.Any(r => AppSettings.IsUnder(d.Root, r)); // mounted in a folder of a drive named already, or kept left out
                leftOut.Add(d.Root);
                if (!inside && !Settings.IsLeftOutDriveKept(d.Root)) found.Add(d.Root);
            }
        }
        LeftOutDrives = found;
    }

    /// <summary>
    /// The bar's "Show C: in searches": takes the drives off the list of folders to leave out (or, for one mounted in a
    /// folder that is left out, adds it to the folders to search), and indexes them. False when no folder on the list
    /// was the reason (a name in "Leave out files named like" is).
    /// </summary>
    public bool IncludeLeftOutDrives(IEnumerable<string> roots)
    {
        bool changed = false;
        foreach (var root in roots) changed |= Settings.IncludeDrive(root);
        if (changed) ApplySettings(indexChanged: true);
        return changed;
    }

    /// <summary>The bar's "Keep it left out": stops asking about these drives.</summary>
    public void KeepDrivesLeftOut(IEnumerable<string> roots)
    {
        foreach (var root in roots) Settings.KeepDriveLeftOut(root);
        Settings.Save();
        _coverage.Clear();
        FindLeftOutDrives();
        foreach (var w in _windows) w.UpdateFolderBar();
    }

    // ---- folders the index doesn't have ---------------------------------------------------------

    readonly Dictionary<string, FolderCoverage> _coverage = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the index holds <paramref name="folder"/> and, if not, why: worked out from the settings, the drives
    /// listed and the attributes of the folder and its parents (a junction, a hidden folder), never anything on the
    /// network, so the UI thread can ask. Remembered until the settings or the drives change; <paramref name="fresh"/>
    /// looks again, at the folder's own local drive too (the drive list is only as new as its last listing). Null in the
    /// demo, whose sample index ignores the settings.
    /// </summary>
    public FolderCoverage? CoverageOf(string folder, bool fresh = false)
    {
        if (IsDemo) return null;
        if (!fresh && _coverage.TryGetValue(folder, out var known)) return known;
        var ix = Settings.Index;
        var drives = DrivesFor(folder, fresh);
        var (attributes, links) = IndexPlan.ChainReaders(drives, folder);
        var coverage = IndexPlan.Coverage(ix, drives, folder, new ExclusionFilter(ix), attributes, links);
        if (_coverage.Count >= 256) _coverage.Clear();
        return _coverage[folder] = coverage;
    }

    /// <summary>"C:\" for any spelling of a drive's root (c:, C:\., \\?\C:\).</summary>
    static string DriveRootOf(string root)
    {
        string r = root.StartsWith(@"\\?\", StringComparison.Ordinal) || root.StartsWith(@"\\.\", StringComparison.Ordinal) ? root[4..] : root;
        return char.ToUpperInvariant(r[0]) + @":\";
    }

    /// <summary>A drive's root (not a SUBST letter, which stands for a folder): it has its own switches and entry.</summary>
    public bool IsWholeDrive(string folder) => IndexPlan.IsWholeDrive(DrivesFor(folder), folder);

    /// <summary>
    /// The drives listed, plus the folder's own drive letter when no listing has seen it yet (taken as ready).
    /// <paramref name="fresh"/>: a local letter the list lacks, or has as not ready, is looked at again (a card put in, a
    /// drive unlocked or a SUBST made since it was listed; a network one never is, as that can wait on the server). When
    /// that look differs from the list, every drive is listed again in the background.
    /// </summary>
    List<DriveEntry> DrivesFor(string folder, bool fresh = false)
    {
        var drives = KnownDrives.ToList();
        if (folder.Length >= 2 && folder[1] == ':' && char.IsAsciiLetter(folder[0]))
        {
            string root = char.ToUpperInvariant(folder[0]) + @":\";
            int i = drives.FindIndex(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && (!fresh || drives[i].IsReady)) return drives;
            var kind = i >= 0 ? drives[i].Kind : Drives.KindOf(root);
            DriveEntry? now = null;
            if (fresh && kind != DriveKind.Network)
            {
                try { now = Drives.Describe(root); }
                catch (Exception) { /* gone while we looked: as listed */ }
            }
            // The list is old: every drive is listed again, for the bars and the lookups that don't look again.
            if (now != null && (i < 0 || now.IsReady != drives[i].IsReady || now.Kind != drives[i].Kind || now.SubstTarget != drives[i].SubstTarget))
                RefreshDrives();
            if (i >= 0) { if (now != null) drives[i] = now; }
            else drives.Add(now ?? new DriveEntry(root, kind, "", "", true, null, null));
        }
        return drives;
    }

    /// <summary>What kind of drive <paramref name="root"/> is, as the drive list has it (a share is a network drive): never asks a share.</summary>
    public DriveKind KindOfDrive(string root) => root.StartsWith(@"\\", StringComparison.Ordinal) ? DriveKind.Network
        : DrivesFor(root).Find(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase))?.Kind ?? DriveKind.Unknown;

    /// <summary>
    /// The folder bar's "Show them in searches": takes the folders off the list of folders to leave out, or adds them to
    /// Also search these folders, then indexes them. A whole drive comes off the list the way the left-out drive bar does
    /// it. False when nothing could change (a drive switch is the reason: that is in Settings › Indexing).
    /// </summary>
    public bool ShowFolderInSearches(IEnumerable<string> folders)
    {
        if (IsDemo) return false;
        bool changed = false;
        foreach (var folder in folders)
        {
            var drives = DrivesFor(folder);
            var (attributes, links) = IndexPlan.ChainReaders(drives, folder);
            changed |= IndexPlan.SearchFolder(Settings.Index, drives, folder, attributes, links) switch
            {
                SearchFolderResult.Added or SearchFolderResult.Unexcluded => true,
                SearchFolderResult.DriveRoot => Settings.IncludeDrive(IndexPlan.IsWholeDrive(drives, folder) ? DriveRootOf(folder) : folder),
                _ => false,
            };
        }
        if (changed) ApplySettings(indexChanged: true);
        return changed;
    }

    /// <summary>Discards the index and scans everything again (Rebuild now, and after turning on instant indexing).</summary>
    public void RebuildIndex()
    {
        // Rebuild takes a fresh copy of the settings it was given, so changes not applied yet are included.
        try { IndexManager.Rebuild(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to rebuild the index.\n\n{ex.Message}", "Ultimate File Explorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        SubscribeIndex();
    }

    public void BookmarksChanged()
    {
        Settings.Save();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray?.Dispose();
            AppIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    sealed class MessageWindow : NativeWindow
    {
        readonly AnythingApp _app;
        bool _hotkey;

        public MessageWindow(AnythingApp app, string title)
        {
            _app = app;
            CreateHandle(new CreateParams { Caption = title, Parent = Native.HWND_MESSAGE });
            // Allow a non-elevated second instance to forward its command line to an elevated first instance.
            Native.ChangeWindowMessageFilterEx(Handle, Native.WM_COPYDATA, Native.MSGFLT_ALLOW, IntPtr.Zero);
        }

        public bool RegisterHotkey(uint mods, Keys key)
        {
            _hotkey = Native.RegisterHotKey(Handle, 1, mods | Native.MOD_NOREPEAT, (uint)key);
            return _hotkey;
        }

        public void UnregisterHotkey()
        {
            if (_hotkey && Handle != IntPtr.Zero) Native.UnregisterHotKey(Handle, 1);
            _hotkey = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_COPYDATA)
            {
                var cds = Marshal.PtrToStructure<Native.COPYDATASTRUCT>(m.LParam);
                if (cds.dwData == Program.CopyDataMagic)
                {
                    string payload = cds.cbData > 0 && cds.lpData != IntPtr.Zero ? Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2) : "";
                    string[] args = payload.Length == 0 ? [] : payload.Split('\0');
                    var cmd = CommandLine.Parse(args);
                    _app.Post(() => _app.HandleCommandLine(cmd, initial: false));
                    m.Result = new IntPtr(1);
                    return;
                }
            }
            else if (m.Msg == Native.WM_HOTKEY)
            {
                _app.OnHotkey();
                return;
            }
            base.WndProc(ref m);
        }
    }
}

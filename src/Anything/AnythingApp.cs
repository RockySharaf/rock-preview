using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Anything.Core;
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
    FileIndex? _subscribedIndex;
    OptionsForm? _options;
    int _changePending, _statePending;
    bool _exiting;

    public AppSettings Settings { get; }
    public IIndexManager IndexManager { get; }
    public ISearchEngine SearchEngine { get; }
    public bool IsDemo { get; }
    public Icon AppIcon { get; }
    public string? HotkeyError { get; private set; }
    public bool Exiting => _exiting;
    public IReadOnlyList<MainForm> Windows => _windows;

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
            MessageBox.Show($"Rock Preview could not start its indexing services.\n\n{ex.Message}\n\nRun with --demo to try the interface with a sample index.",
                "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        _messageWindow = new MessageWindow(this, Program.MessageWindowTitle + (cmd.Demo ? ".Demo" : ""));

        IndexManager.StateChanged += OnStateChangedBackground;
        SubscribeIndex();
        try { IndexManager.Start(Settings.Index); }
        catch (Exception ex)
        {
            MessageBox.Show($"Indexing could not be started.\n\n{ex.Message}", "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        SubscribeIndex();

        UpdateTray();
        RegisterHotkey();
        HandleCommandLine(cmd, initial: true);
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

    public string Title => IsDemo ? "Rock Preview (Demo)" : "Rock Preview";

    /// <summary>The global hotkey registered right now ("Alt+X"), or null when none is set or it's taken.</summary>
    public string? ActiveHotkey { get; private set; }

    /// <summary>The name followed by the key that opens it from anywhere: "Rock Preview (Alt+X)".</summary>
    public string TitleWithHotkey => ActiveHotkey is not { } h ? Title : IsDemo ? $"Rock Preview (Demo, {h})" : $"Rock Preview ({h})";

    public void Post(Action a) => _sync.Post(_ => { try { a(); } catch (ObjectDisposedException) { } }, null);

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
            // Started in place of File Explorer (Win+E, opening a folder or drive): a new window there, like Explorer's.
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
    }

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
    /// Turns on instant indexing for good: installs the Rock Preview service (Windows asks for permission this once),
    /// then re-indexes through it. Rock Preview itself keeps running as a normal app.
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
            MessageBox.Show(owner, "Rock Preview couldn't turn on instant indexing.", "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        for (int i = 0; i < 40 && !Anything.Core.Indexing.VolumeService.IsRunning(); i++) await Task.Delay(250);
        RebuildIndex();
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
            var show = new ToolStripMenuItem("&Search Rock Preview...", null, (_, _) => ShowSearchWindow()) { ShortcutKeyDisplayString = ActiveHotkey };
            show.Font = new Font(show.Font, FontStyle.Bold);
            menu.Items.Add(show);
            menu.Items.Add(new ToolStripMenuItem("&New Search Window", null, (_, _) => NewWindow(_lastActive)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("&Options...", null, (_, _) => ShowOptions(null)));
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
            if (_messageWindow.RegisterHotkey(mods, key)) ActiveHotkey = Hotkey.Format(mods, key);
            else HotkeyError = $"The hotkey {Settings.Hotkey} is already in use by another program. Choose another in Tools > Options > Keyboard.";
        }
        if (_tray is not null)
        {
            _tray.Text = TitleWithHotkey;
            if (_tray.ContextMenuStrip?.Items[0] is ToolStripMenuItem show) show.ShortcutKeyDisplayString = ActiveHotkey;
        }
        foreach (var w in _windows) w.UpdateTitle();
    }

    /// <summary>Releases the hotkey while Options records a new one, so that pressing it there is seen.</summary>
    public void SuspendHotkey() => _messageWindow.UnregisterHotkey();

    internal void OnHotkey()
    {
        IntPtr fg = Native.GetForegroundWindow();
        var active = _windows.FirstOrDefault(x => x.Visible && x.IsHandleCreated && x.Handle == fg);
        if (active is not null) active.Hide();
        else ShowSearchWindow();
    }

    public void ShowOptions(IWin32Window? owner)
    {
        if (_options is { IsDisposed: false })
        {
            _options.Activate();
            return;
        }
        _options = new OptionsForm(this);
        try { _options.ShowDialog(owner); }
        finally { _options.Dispose(); _options = null; }
    }

    /// <summary>Applies changed settings everywhere. Index settings changes rebuild the index.</summary>
    public void ApplySettings(bool indexChanged)
    {
        Settings.Save();
        if (!IsDemo)
        {
            Settings.ApplyStartWithWindows();
            Settings.ApplyExplorerReplacement();
        }
        UpdateTray();
        RegisterHotkey();
        foreach (var w in _windows) w.ApplyUiSettings();
        if (indexChanged) RebuildIndex();
    }

    public void RebuildIndex()
    {
        try { IndexManager.Rebuild(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to rebuild the index.\n\n{ex.Message}", "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

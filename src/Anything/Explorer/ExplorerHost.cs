using System.ComponentModel;
using System.Runtime.InteropServices;
using Anything.Core;

namespace Anything.Explorer;

/// <summary>
/// Hosts the Windows Shell's own ExplorerBrowser — the same file view, navigation pane, context menus, drag and
/// drop, views and thumbnails as File Explorer — inside Rock Preview. Rock Preview keeps its own Back/Forward
/// history (so it can show button state) and tracks the selection for its preview pane.
/// </summary>
internal sealed class ExplorerHost : Control
{
    IExplorerBrowser? _browser;
    uint _cookie;
    readonly Callbacks _callbacks;
    readonly List<byte[]> _back = [], _forward = [];
    byte[]? _current;
    bool _historyNavigation;
    IShellItem? _pendingItem;           // keeps the navigation target alive until it completes
    string? _selectAfterNavigate;       // full path to select once the view shows it
    int _selectAttempts;
    readonly System.Windows.Forms.Timer _selectionTimer = new() { Interval = 200 };
    readonly System.Windows.Forms.Timer _selectTimer = new() { Interval = 100 };
    readonly System.Windows.Forms.Timer _themeTimer = new() { Interval = 400 };
    string? _lastSelection;
    int _lastSelectionCount = -1;
    int _lastFocused = -1;
    bool _selectionReported;            // the view said the selection changed (CDBOSC_SELCHANGE) since the last poll
    int _itemCount, _lastItemCount = -1;
    bool _renameAfterSelect;
    bool _navPane = true;
    bool _thisPcPending;                // the view being created shows This PC

    /// <summary>Show ExplorerBrowser's own navigation pane. Rock Preview hosts a separate tree, so this is off.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowFrames { get; set; }

    /// <summary>Number of items in the current folder view.</summary>
    public int ItemCount => _itemCount;

    /// <summary>Raised on the UI thread after the view shows a new location.</summary>
    public event Action? Navigated;
    /// <summary>Raised when the first selected item (or the selection count) changes.</summary>
    public event Action? SelectionChanged;
    public event Action<string>? NavigationFailed;

    public ExplorerHost()
    {
        _callbacks = new Callbacks(this);
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        _selectionTimer.Tick += (_, _) => PollSelection();
        _selectTimer.Tick += (_, _) => TrySelectPending();
        _themeTimer.Tick += (_, _) => { _themeTimer.Stop(); ApplyViewTheme(); };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    // ---- state --------------------------------------------------------------------------------

    /// <summary>File-system path of the current folder, or null for virtual locations (This PC, Libraries...).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? CurrentPath { get; private set; }
    /// <summary>What Explorer's address bar would show for the current location ("This PC", "C:\Users\...").</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? CurrentEditingName { get; private set; }
    /// <summary>Short display name ("Downloads", "This PC").</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? CurrentDisplayName { get; private set; }
    /// <summary>Parsing name that navigates back here later ("C:\x", "::{20D04FE0-...}").</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? CurrentParsingName { get; private set; }
    /// <summary>The location's icon in the system's small image list (shown on its tab), or -1.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CurrentIconIndex { get; private set; } = -1;

    public bool HasLocation => _current != null;
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>First selected item's file-system path (null when nothing or only virtual items are selected).</summary>
    public string? SelectedPath => _lastSelection;
    public int SelectedCount => Math.Max(0, _lastSelectionCount);

    /// <summary>Explorer's navigation pane (Quick access, This PC, drives, network) on the left.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool NavigationPaneVisible
    {
        get => _navPane;
        set
        {
            if (_navPane == value) return;
            _navPane = value;
            if (_current != null) Navigate(_current, historyNavigation: true); // panes are queried on navigation
        }
    }

    // ---- lifetime -----------------------------------------------------------------------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_browser != null) return;
        try
        {
            var clsid = ShellNative.CLSID_ExplorerBrowser;
            _browser = (IExplorerBrowser)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;
            ((IObjectWithSite)_browser).SetSite(_callbacks); // pane visibility is queried through the site
            _browser.SetOptions((ShowFrames ? EXPLORER_BROWSER_OPTIONS.ShowFrames : 0) | EXPLORER_BROWSER_OPTIONS.NoBorder | EXPLORER_BROWSER_OPTIONS.NoTravelLog);
            var rect = new RECT(ClientRectangle);
            var fs = new FOLDERSETTINGS { ViewMode = ShellNative.FVM_DETAILS, fFlags = ShellNative.FWF_SHOWSELALWAYS };
            Marshal.ThrowExceptionForHR(_browser.Initialize(Handle, ref rect, ref fs));
            _browser.Advise(_callbacks, out _cookie);
            _selectionTimer.Start();
        }
        catch (Exception ex)
        {
            _browser = null;
            NavigationFailed?.Invoke("The Windows Explorer view could not be created: " + ex.Message);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        ReleaseBrowser();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _selectionTimer.Dispose();
            _selectTimer.Dispose();
            _themeTimer.Dispose();
            ReleaseBrowser();
        }
        base.Dispose(disposing);
    }

    void ReleaseBrowser()
    {
        var b = _browser;
        if (b == null) return;
        _browser = null;
        _selectionTimer.Stop();
        try { if (_cookie != 0) b.Unadvise(_cookie); } catch { }
        try { ((IObjectWithSite)b).SetSite(null); } catch { }
        try { b.Destroy(); } catch { }
        try { Marshal.FinalReleaseComObject(b); } catch { }
        _pendingItem = null;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _browser?.SetRect(IntPtr.Zero, new RECT(ClientRectangle));
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) _browser?.SetRect(IntPtr.Zero, new RECT(ClientRectangle));
    }

    // ---- navigation ---------------------------------------------------------------------------

    /// <summary>
    /// Navigates to a path, "shell:" location or parsing name ("::{GUID}"). When <paramref name="selectPath"/>
    /// is given, that item is selected once it appears. Returns false if the location can't be resolved.
    /// </summary>
    public bool Navigate(string location, string? selectPath = null)
    {
        if (_browser == null || string.IsNullOrWhiteSpace(location)) return false;
        location = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
        if (location.Length == 2 && location[1] == ':') location += "\\";
        var iid = ShellNative.IID_IShellItem;
        if (ShellNative.SHCreateItemFromParsingName(location, IntPtr.Zero, ref iid, out var item) != ShellNative.S_OK || item == null)
            return false;
        _selectAfterNavigate = selectPath;
        _renameAfterSelect = false;
        _pendingItem = item;
        return _browser.BrowseToObject(item, ShellNative.SBSP_ABSOLUTE) == ShellNative.S_OK;
    }

    bool Navigate(byte[] pidl, bool historyNavigation)
    {
        if (_browser == null) return false;
        IntPtr p = Marshal.AllocCoTaskMem(pidl.Length);
        try
        {
            Marshal.Copy(pidl, 0, p, pidl.Length);
            var iid = ShellNative.IID_IShellItem;
            if (ShellNative.SHCreateItemFromIDList(p, ref iid, out var item) != ShellNative.S_OK || item == null) return false;
            _historyNavigation = historyNavigation;
            _pendingItem = item;
            return _browser.BrowseToObject(item, ShellNative.SBSP_ABSOLUTE) == ShellNative.S_OK;
        }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public void GoBack()
    {
        if (_back.Count == 0 || _current == null) return;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(_current);
        if (!Navigate(target, historyNavigation: true)) { _forward.RemoveAt(_forward.Count - 1); _back.Add(target); }
    }

    public void GoForward()
    {
        if (_forward.Count == 0 || _current == null) return;
        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(_current);
        if (!Navigate(target, historyNavigation: true)) { _back.RemoveAt(_back.Count - 1); _forward.Add(target); }
    }

    public void GoUp() => _browser?.BrowseToIDList(IntPtr.Zero, ShellNative.SBSP_PARENT);

    public void RefreshView()
    {
        var view = GetView<IShellView>(ShellNative.IID_IShellView);
        if (view == null) return;
        try { view.Refresh(); } finally { Marshal.ReleaseComObject(view); }
    }

    /// <summary>Explorer view mode (FVM_* constant) and icon size in pixels (0 = default for the mode).</summary>
    /// <summary>The view's FOLDERVIEWMODE (FVM_DETAILS, FVM_ICON...), or 0 when there is no view.</summary>
    public int ViewMode
    {
        get
        {
            var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
            if (fv == null) return 0;
            try { return fv.GetCurrentViewMode(out uint mode) == ShellNative.S_OK ? (int)mode : 0; }
            catch (COMException) { return 0; }
            finally { Marshal.ReleaseComObject(fv); }
        }
    }

    public void SetViewMode(int fvm, int iconSize = 0)
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return;
        try { fv.SetViewModeAndIconSize(fvm, iconSize); } finally { Marshal.ReleaseComObject(fv); }
    }

    public void FocusView()
    {
        var view = GetView<IShellView>(ShellNative.IID_IShellView);
        if (view == null) { Focus(); return; }
        try
        {
            view.UIActivate(2); // SVUIA_ACTIVATE_FOCUS
            if (view.GetWindow(out var hwnd) == ShellNative.S_OK) SetFocus(hwnd);
        }
        finally { Marshal.ReleaseComObject(view); }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        // WinForms focuses the host itself (back from another app, Tab): the keys belong to the file view inside it.
        if (_browser != null) BeginInvoke(() => { if (Focused) FocusView(); });
    }

    /// <summary>
    /// Focus moved into the file view behind WinForms' back: make the container's ActiveControl agree, so switching to
    /// another app and back returns to the files, as in Explorer, not to the navigation pane or the search box.
    /// </summary>
    void SyncActiveControl()
    {
        if (IsDisposed || !FocusInView(out _)) return;
        try
        {
            if (GetContainerControl() is ContainerControl cc && cc.ActiveControl != this && CanSelect) cc.ActiveControl = this;
        }
        catch (Exception) { }
    }

    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    T? GetView<T>(Guid iid) where T : class
    {
        if (_browser == null) return null;
        if (_browser.GetCurrentView(ref iid, out var ptr) != ShellNative.S_OK || ptr == IntPtr.Zero) return null;
        try { return Marshal.GetObjectForIUnknown(ptr) as T; }
        finally { Marshal.Release(ptr); }
    }

    void OnNavigationComplete(IntPtr pidl)
    {
        var bytes = ShellNative.CopyPidl(pidl);
        if (_historyNavigation) _historyNavigation = false;
        else if (_current != null && !_current.AsSpan().SequenceEqual(bytes))
        {
            _back.Add(_current);
            if (_back.Count > 200) _back.RemoveAt(0);
            _forward.Clear();
        }
        _current = bytes;
        _pendingItem = null;
        CurrentPath = ShellNative.NameOf(pidl, SIGDN.FileSysPath);
        CurrentEditingName = ShellNative.NameOf(pidl, SIGDN.DesktopAbsoluteEditing) ?? CurrentPath;
        CurrentDisplayName = ShellNative.NameOf(pidl, SIGDN.NormalDisplay) ?? CurrentPath;
        CurrentParsingName = ShellNative.NameOf(pidl, SIGDN.DesktopAbsoluteParsing) ?? CurrentPath;
        CurrentIconIndex = ShellInfo.GetIconIndex(pidl);
        _lastSelection = null;
        _lastSelectionCount = -1;
        // A new folder's rename belongs to the folder it was made in: going elsewhere before it shows (Back, Up, a
        // double-click) drops it, or the rename box would open on a same-named item here.
        if (_renameAfterSelect && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(_selectAfterNavigate) ?? ""),
                Path.TrimEndingDirectorySeparator(CurrentPath ?? ""), StringComparison.OrdinalIgnoreCase))
        {
            _selectAfterNavigate = null;
            _renameAfterSelect = false;
        }
        if (_selectAfterNavigate != null) { _selectAttempts = 0; _selectTimer.Start(); }
        ApplyViewTheme();
        Navigated?.Invoke();
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Each new view, before it is shown: column headers only in Details, as in File Explorer (the view otherwise puts
    /// them above tiles and icons too). This PC opens as File Explorer shows it: drive tiles with a storage bar (red
    /// when nearly full) and "X GB free of Y GB". Every other folder opens in Details, as before; the View menu still
    /// switches either until you leave.
    /// </summary>
    void OnViewCreated(IntPtr psv)
    {
        if (psv == IntPtr.Zero || Marshal.GetObjectForIUnknown(psv) is not IFolderView2 fv) return;
        try
        {
            fv.SetCurrentFolderFlags(ShellNative.FWF_NOHEADERINALLVIEWS, ShellNative.FWF_NOHEADERINALLVIEWS);
            if (_thisPcPending) fv.SetViewModeAndIconSize(ShellNative.FVM_TILE, 48);
        }
        catch (COMException) { }
        finally { Marshal.ReleaseComObject(fv); }
    }

    /// <summary>
    /// Keeps the folder view in the app's theme when it differs from Windows' app theme (light app, dark Windows, or the
    /// other way round). Left alone, the view can then switch to Windows' theme on its own (it did, running as
    /// administrator: a black view inside a light window). When the two themes agree the view keeps its native style:
    /// setting any theme gives it slightly larger group headers. Applied after every navigation and when Windows'
    /// settings change.
    /// </summary>
    void ApplyViewTheme()
    {
        bool windowsDark = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
        if (windowsDark == ExplorerTheme.Dark) return;
        var props = GetView<IVisualProperties>(ShellNative.IID_IVisualProperties);
        if (props == null) return;
        try { props.SetTheme(ExplorerTheme.Dark ? "DarkMode_ItemsView" : "ItemsView", null); }
        catch (COMException) { }
        finally { Marshal.ReleaseComObject(props); }
    }

    void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        // The view reacts to the change itself first; set the app's theme back once it has.
        if (IsHandleCreated && !IsDisposed) BeginInvoke(() => _themeTimer.Start());
    }

    void OnNavigationFailedInternal(IntPtr pidl)
    {
        _pendingItem = null;
        _historyNavigation = false;
        NavigationFailed?.Invoke("Unable to open " + (ShellNative.NameOf(pidl, SIGDN.DesktopAbsoluteEditing) ?? "that location") + ".");
    }

    // ---- selection ----------------------------------------------------------------------------

    void PollSelection()
    {
        if (!Visible || _browser == null) return;
        string? first = null;
        int count = 0;
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv != null)
        {
            try
            {
                if (fv.ItemCount(0x2 /* SVGIO_ALLVIEW */, out int all) == ShellNative.S_OK) _itemCount = all;
                // The count and the focused item are cheap; the selection array copies every selected item, so a big
                // selection (Ctrl+A among 100,000 files) is read again only when one of them changes or the view says
                // the selection did (CDBOSC_SELCHANGE). Which item comes first, and so is previewed, stays the array's.
                int selected = fv.ItemCount(0x1 /* SVGIO_SELECTION */, out int s) == ShellNative.S_OK ? s : -1;
                int focused = fv.GetFocusedItem(out int f) == ShellNative.S_OK ? f : -1;
                bool reported = _selectionReported;
                _selectionReported = false;
                if (selected != 0)
                {
                    if (ShellSelection.CanKeepFirst(selected, focused, reported, _lastSelectionCount, _lastFocused))
                        (count, first) = (selected, _lastSelection);
                    else
                        (count, first) = ReadSelection(fv);
                }
                _lastFocused = focused;
            }
            catch (COMException) { }
            finally { Marshal.ReleaseComObject(fv); }
        }
        if (count == _lastSelectionCount && string.Equals(first, _lastSelection, StringComparison.OrdinalIgnoreCase) && _itemCount == _lastItemCount) return;
        _lastItemCount = _itemCount;
        _lastSelection = first;
        _lastSelectionCount = count;
        SelectionChanged?.Invoke();
    }

    /// <summary>How many items are selected and the first one's file-system path, from the view's selection array.</summary>
    static (int Count, string? First) ReadSelection(IFolderView2 fv)
    {
        if (fv.GetSelection(false, out var items) != ShellNative.S_OK || items == null) return (0, null);
        try
        {
            if (items.GetCount(out uint n) != ShellNative.S_OK || n == 0) return (0, null);
            string? first = null;
            if (items.GetItemAt(0, out var item) == ShellNative.S_OK && item != null)
            {
                first = ShellNative.NameOf(item, SIGDN.FileSysPath);
                Marshal.ReleaseComObject(item);
            }
            return ((int)n, first);
        }
        finally { Marshal.ReleaseComObject(items); }
    }

    /// <summary>
    /// Open a double-clicked folder here instead of through Windows' default action. Needed when Rock Preview is the
    /// default for opening folders (Explorer replacement): that action would start another Rock Preview window.
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool OpenFoldersInPlace { get; set; }

    /// <summary>
    /// Double-click / Enter in the view: a single selected folder (or This PC, which Rock Preview also opens in place
    /// of File Explorer) is opened here; anything else as usual.
    /// </summary>
    bool OnDefaultCommand()
    {
        if (!OpenFoldersInPlace && !ExplorerReplacement.IsEnabled()) return false; // also when turned on elsewhere (Setup)
        var selected = GetSelectedPaths(2, SIGDN.DesktopAbsoluteParsing);
        if (selected.Count != 1) return false;
        string target = selected[0];
        if (!Directory.Exists(target) && !string.Equals(target, ThisPcParsingName, StringComparison.OrdinalIgnoreCase)) return false;
        BeginInvoke(() => Navigate(target)); // not inside the view's own callback
        return true;
    }

    const string ThisPcParsingName = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    /// <summary>
    /// File-system paths of the selected items when all of them are folders on disk (not zips, which the shell also
    /// calls folders) and there are at most <paramref name="max"/>; otherwise none. Decided from the attributes the
    /// view already holds, so folders selected on a slow network share never wait on the network.
    /// </summary>
    public List<string> GetSelectedFolders(int max)
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return [];
        try
        {
            if (fv.GetSelection(false, out var items) != ShellNative.S_OK || items == null) return [];
            try
            {
                if (items.GetCount(out uint n) != ShellNative.S_OK || n == 0 || n > max) return [];
                if (items.GetAttributes(ShellNative.SIATTRIBFLAGS_AND, ShellSelection.SfgaoFolder | ShellSelection.SfgaoFileSystem, out uint all) < 0
                    || items.GetAttributes(ShellNative.SIATTRIBFLAGS_OR, ShellSelection.SfgaoStream, out uint any) < 0
                    || !ShellSelection.AreFolders(all, any)) return [];
                var paths = new List<string>((int)n);
                for (uint i = 0; i < n; i++)
                {
                    if (items.GetItemAt(i, out var item) != ShellNative.S_OK || item == null) return [];
                    try
                    {
                        if (ShellNative.NameOf(item, SIGDN.FileSysPath) is not { } p) return []; // all of them, or the search would look in part
                        paths.Add(p);
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
                return paths;
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        catch (COMException) { return []; }
        finally { Marshal.ReleaseComObject(fv); }
    }

    /// <summary>Names of the selected items (by default file-system paths; virtual items are skipped), at most <paramref name="max"/>.</summary>
    public List<string> GetSelectedPaths(int max, SIGDN name = SIGDN.FileSysPath)
    {
        var paths = new List<string>();
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return paths;
        try
        {
            if (fv.GetSelection(false, out var items) == ShellNative.S_OK && items != null)
            {
                try
                {
                    if (items.GetCount(out uint n) == ShellNative.S_OK)
                    {
                        for (uint i = 0; i < n && paths.Count < max; i++)
                        {
                            if (items.GetItemAt(i, out var item) != ShellNative.S_OK || item == null) continue;
                            try { if (ShellNative.NameOf(item, name) is { } p) paths.Add(p); }
                            finally { Marshal.ReleaseComObject(item); }
                        }
                    }
                }
                finally { Marshal.ReleaseComObject(items); }
            }
        }
        catch (COMException) { }
        finally { Marshal.ReleaseComObject(fv); }
        return paths;
    }

    void TrySelectPending()
    {
        string? path = _selectAfterNavigate;
        if (path == null || ++_selectAttempts > 30) { _selectTimer.Stop(); _selectAfterNavigate = null; _renameAfterSelect = false; return; }
        // A rename box closes when the view gets the focus after it opens, so the view gets it first, a tick earlier.
        if (_renameAfterSelect && _selectAttempts == 1) { FocusView(); return; }
        var view = GetView<IShellView>(ShellNative.IID_IShellView);
        if (view == null) return;
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != ShellNative.S_OK || pidl == IntPtr.Zero) { _selectAfterNavigate = null; _renameAfterSelect = false; return; }
            IntPtr child = ShellNative.ILFindLastID(pidl);
            uint flags = (_renameAfterSelect ? ShellNative.SVSI_EDIT : ShellNative.SVSI_SELECT)
                | ShellNative.SVSI_DESELECTOTHERS | ShellNative.SVSI_ENSUREVISIBLE | ShellNative.SVSI_FOCUSED;
            if (view.SelectItem(child, flags) == ShellNative.S_OK)
            {
                _selectTimer.Stop();
                _selectAfterNavigate = null;
                if (_renameAfterSelect) _renameAfterSelect = false;
                else FocusView();
            }
        }
        finally
        {
            if (pidl != IntPtr.Zero) ShellNative.ILFree(pidl);
            Marshal.ReleaseComObject(view);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    // ---- commands ----------------------------------------------------------------------------

    /// <summary>Creates "New folder" (or "New folder (2)"...) in <paramref name="folder"/>, selects it and starts renaming, like Explorer.</summary>
    public void CreateNewFolder(string folder)
    {
        string path = Path.Combine(folder, "New folder");
        for (int n = 2; Directory.Exists(path) || File.Exists(path); n++) path = Path.Combine(folder, $"New folder ({n})");
        try { Directory.CreateDirectory(path); }
        catch (Exception ex) { NavigationFailed?.Invoke("Can't create a folder here: " + ex.Message); return; }
        _selectAfterNavigate = path;
        _renameAfterSelect = true;
        _selectAttempts = 0;
        _selectTimer.Start();
    }

    /// <summary>Starts in-place rename of the selected item.</summary>
    public void RenameSelection()
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return;
        try { fv.DoRename(); } catch (COMException) { } finally { Marshal.ReleaseComObject(fv); }
    }

    /// <summary>Runs a shell verb on the selected items: "cut", "copy", "delete", "properties", "Windows.ModernShare"...</summary>
    public bool InvokeVerbOnSelection(string verb)
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return false;
        try { return fv.InvokeVerbOnSelection(verb) == ShellNative.S_OK; }
        catch (COMException) { return false; }
        finally { Marshal.ReleaseComObject(fv); }
    }

    public void SelectNone()
    {
        var view = GetView<IShellView>(ShellNative.IID_IShellView);
        if (view == null) return;
        try { view.SelectItem(IntPtr.Zero, ShellNative.SVSI_DESELECTOTHERS); }
        catch (COMException) { }
        finally { Marshal.ReleaseComObject(view); }
    }

    public void InvertSelection()
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return;
        try
        {
            if (fv.ItemCount(0x2 /* SVGIO_ALLVIEW */, out int count) != ShellNative.S_OK) return;
            for (int i = 0; i < count; i++)
            {
                if (fv.Item(i, out var pidl) != ShellNative.S_OK || pidl == IntPtr.Zero) continue;
                try
                {
                    fv.GetSelectionState(pidl, out uint state);
                    fv.SelectItem(i, (state & ShellNative.SVSI_SELECT) != 0 ? ShellNative.SVSI_DESELECT : ShellNative.SVSI_SELECT);
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
        }
        catch (COMException) { }
        finally { Marshal.ReleaseComObject(fv); }
    }

    /// <summary>The current shell view (an IShellView RCW) for siting shell menus; the caller releases it.</summary>
    public object? GetShellViewObject() => GetView<IShellView>(ShellNative.IID_IShellView);

    /// <summary>Sends a keystroke (SendKeys syntax) to the file view, e.g. "^c" — Explorer handles it exactly as if typed.</summary>
    public void SendToView(string keys)
    {
        FocusView();
        SendKeys.SendWait(keys);
    }

    static readonly Guid FMTID_Storage = new("B725F130-47EF-101A-A5F1-02608C9EEBAC");

    public void SortBy(ExplorerSortColumn column, bool descending)
    {
        uint pid = column switch
        {
            ExplorerSortColumn.DateModified => 14,
            ExplorerSortColumn.Type => 4,
            ExplorerSortColumn.Size => 12,
            _ => 10, // System.ItemNameDisplay
        };
        SetSort(FMTID_Storage, pid, descending ? -1 : 1);
    }

    static readonly Guid FMTID_ItemDate = new("F7DB74B4-4287-4103-AFBA-F1B13DCD75CF");

    /// <summary>The view's sort (its first column) when the results can sort the same way: name, date, size or type.</summary>
    public (Anything.Core.SortKey Key, bool Descending)? CurrentSort()
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return null;
        IntPtr buf = Marshal.AllocCoTaskMem(24);
        try
        {
            if (fv.GetSortColumns(buf, 1) != ShellNative.S_OK) return null;
            var guid = new byte[16];
            Marshal.Copy(buf, guid, 0, 16);
            var fmtid = new Guid(guid);
            int pid = Marshal.ReadInt32(buf, 16);
            bool descending = Marshal.ReadInt32(buf, 20) < 0;
            Anything.Core.SortKey? key = fmtid == FMTID_Storage
                ? pid switch
                {
                    10 => Anything.Core.SortKey.Name,
                    14 => Anything.Core.SortKey.DateModified,
                    15 => Anything.Core.SortKey.DateCreated,
                    12 => Anything.Core.SortKey.Size,
                    4 => Anything.Core.SortKey.Type,
                    _ => null,
                }
                : fmtid == FMTID_ItemDate && pid == 100 ? Anything.Core.SortKey.DateModified : null; // "Date" in photo folders
            return key is { } k ? (k, descending) : null;
        }
        finally { Marshal.FreeCoTaskMem(buf); Marshal.ReleaseComObject(fv); }
    }

    public void SetSortDirection(bool descending)
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return;
        IntPtr buf = Marshal.AllocCoTaskMem(24);
        try
        {
            if (fv.GetSortColumns(buf, 1) != ShellNative.S_OK) return;
            Marshal.WriteInt32(buf, 20, descending ? -1 : 1);
            fv.SetSortColumns(buf, 1);
        }
        finally { Marshal.FreeCoTaskMem(buf); Marshal.ReleaseComObject(fv); }
    }

    void SetSort(Guid fmtid, uint pid, int direction)
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return;
        IntPtr buf = Marshal.AllocCoTaskMem(24); // SORTCOLUMN { PROPERTYKEY { GUID fmtid; DWORD pid } key; int direction }
        try
        {
            Marshal.Copy(fmtid.ToByteArray(), 0, buf, 16);
            Marshal.WriteInt32(buf, 16, (int)pid);
            Marshal.WriteInt32(buf, 20, direction);
            fv.SetSortColumns(buf, 1);
        }
        finally { Marshal.FreeCoTaskMem(buf); Marshal.ReleaseComObject(fv); }
    }

    // ---- keyboard ----------------------------------------------------------------------------

    /// <summary>
    /// The window with keyboard focus belongs to the file view: its items, or (<paramref name="inRename"/>) the box
    /// renaming one (new folders open in it).
    /// </summary>
    bool FocusInView(out bool inRename)
    {
        inRename = false;
        var focus = GetFocus();
        if (focus == IntPtr.Zero) return false;
        var view = GetView<IShellView>(ShellNative.IID_IShellView);
        if (view == null) return false;
        try
        {
            if (view.GetWindow(out var viewHwnd) != ShellNative.S_OK || (focus != viewHwnd && !IsChild(viewHwnd, focus))) return false;
        }
        finally { Marshal.ReleaseComObject(view); }
        var sb = new System.Text.StringBuilder(32);
        GetClassName(focus, sb, sb.Capacity);
        inRename = sb.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>
    /// Keys for the rename box: all but Alt shortcuts and Ctrl+Tab (the window's tabs). Space, Escape, Tab (on to the
    /// next item), Ctrl+A/C/V/Z and the arrows edit the name instead of reaching the view's accelerators or the form's
    /// shortcuts and menus.
    /// </summary>
    static bool IsRenameKey(Keys keyData)
        => (keyData & Keys.Alt) == 0 && !((keyData & Keys.Control) != 0 && (keyData & Keys.KeyCode) == Keys.Tab);

    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    /// <summary>Lets the shell view handle its accelerators (Delete, F2, Ctrl+C/X/V/A...) before Rock Preview's menus.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (FocusInView(out bool inRename) && inRename && IsRenameKey(keyData)) return false;
        if (keyData is Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
        var view = GetView<IShellView>(ShellNative.IID_IShellView);
        if (view != null)
        {
            try
            {
                // Only while the file view itself has focus: in the navigation pane, Delete must not act on the view's selection.
                if (view.GetWindow(out var viewHwnd) == ShellNative.S_OK && (msg.HWnd == viewHwnd || IsChild(viewHwnd, msg.HWnd)))
                {
                    var m = new MSG { hwnd = msg.HWnd, message = msg.Msg, wParam = msg.WParam, lParam = msg.LParam };
                    if (view.TranslateAccelerator(ref m) == ShellNative.S_OK) return true;
                    if (keyData == Keys.Back) { GoUp(); return true; }
                }
            }
            finally { Marshal.ReleaseComObject(view); }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        if (FocusInView(out bool inRename) && inRename && IsRenameKey(keyData)) return true;
        return base.IsInputKey(keyData);
    }

    protected override bool IsInputChar(char charCode)
        => FocusInView(out _) || base.IsInputChar(charCode); // type-ahead and typing in the rename box, not mnemonics

    // ---- COM callbacks (separate object so the Control itself is never exposed to COM) --------

    [ComVisible(true)]
    sealed class Callbacks(ExplorerHost host) : IExplorerBrowserEvents, IServiceProvider, IExplorerPaneVisibility, ICommDlgBrowser, ICommDlgBrowser2
    {
        public int OnNavigationPending(IntPtr pidlFolder)
        {
            host._thisPcPending = string.Equals(ShellNative.NameOf(pidlFolder, SIGDN.DesktopAbsoluteParsing), ThisPcParsingName, StringComparison.OrdinalIgnoreCase);
            return ShellNative.S_OK;
        }
        public int OnViewCreated(IntPtr psv)
        {
            try { host.OnViewCreated(psv); } catch (Exception) { }
            return ShellNative.S_OK;
        }
        public int OnNavigationComplete(IntPtr pidlFolder) { host.OnNavigationComplete(pidlFolder); return ShellNative.S_OK; }
        public int OnNavigationFailed(IntPtr pidlFolder) { host.OnNavigationFailedInternal(pidlFolder); return ShellNative.S_OK; }

        public int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject)
        {
            if (guidService == ShellNative.IID_IExplorerPaneVisibility && riid == ShellNative.IID_IExplorerPaneVisibility)
            {
                ppvObject = Marshal.GetComInterfaceForObject(this, typeof(IExplorerPaneVisibility));
                return ShellNative.S_OK;
            }
            if (guidService == ShellNative.IID_ICommDlgBrowser) // SID_SExplorerBrowserFrame
            {
                if (riid == ShellNative.IID_ICommDlgBrowser) { ppvObject = Marshal.GetComInterfaceForObject(this, typeof(ICommDlgBrowser)); return ShellNative.S_OK; }
                if (riid == ShellNative.IID_ICommDlgBrowser2) { ppvObject = Marshal.GetComInterfaceForObject(this, typeof(ICommDlgBrowser2)); return ShellNative.S_OK; }
            }
            ppvObject = IntPtr.Zero;
            return ShellNative.E_NOINTERFACE;
        }

        // ICommDlgBrowser(2): only the double-click is taken over; everything else is the view's normal behaviour.
        public int OnDefaultCommand(IntPtr ppshv)
        {
            try { return host.OnDefaultCommand() ? ShellNative.S_OK : ShellNative.S_FALSE; }
            catch (Exception) { return ShellNative.S_FALSE; }
        }
        public int OnStateChange(IntPtr ppshv, uint uChange)
        {
            try
            {
                if (uChange == ShellNative.CDBOSC_SETFOCUS && host.IsHandleCreated && !host.IsDisposed)
                    host.BeginInvoke(host.SyncActiveControl); // not inside the view's own focus change
                else if (uChange == ShellNative.CDBOSC_SELCHANGE)
                    host._selectionReported = true; // the next poll reads a big selection again; the view calls on the UI thread
            }
            catch (Exception) { }
            return ShellNative.S_OK;
        }
        public int IncludeObject(IntPtr ppshv, IntPtr pidl) => ShellNative.S_OK;
        public int Notify(IntPtr ppshv, uint dwNotifyType) => ShellNative.S_OK;
        public int GetDefaultMenuText(IntPtr ppshv, IntPtr pszText, int cchMax) => ShellNative.S_FALSE;
        public int GetViewFlags(out uint pdwFlags)
        {
            pdwFlags = ShellNative.CDB2GVF_NOSELECTVERB | ShellNative.CDB2GVF_NOINCLUDEITEM; // no "Select" verb, no per-item callbacks
            return ShellNative.S_OK;
        }

        public int GetPaneState(ref Guid ep, out uint peps)
        {
            if (ep == ShellNative.EP_NavPane)
                peps = (host._navPane ? ShellNative.EPS_DEFAULT_ON : ShellNative.EPS_DEFAULT_OFF) | ShellNative.EPS_FORCE;
            else if (ep == ShellNative.EP_PreviewPane || ep == ShellNative.EP_DetailsPane || ep == ShellNative.EP_QueryPane
                     || ep == ShellNative.EP_AdvQueryPane || ep == ShellNative.EP_Ribbon || ep == ShellNative.EP_StatusBar
                     || ep == ShellNative.EP_Commands || ep == ShellNative.EP_Commands_Organize || ep == ShellNative.EP_Commands_View)
                peps = ShellNative.EPS_DEFAULT_OFF | ShellNative.EPS_FORCE; // Rock Preview has its own preview pane and status bar
            else
                peps = 0; // EPS_DONTCARE
            return ShellNative.S_OK;
        }
    }
}

internal enum ExplorerSortColumn { Name, DateModified, Type, Size }

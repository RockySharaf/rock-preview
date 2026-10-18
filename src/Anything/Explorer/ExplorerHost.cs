using System.ComponentModel;
using System.Runtime.InteropServices;

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
    string? _lastSelection;
    int _lastSelectionCount = -1;
    int _itemCount, _lastItemCount = -1;
    bool _renameAfterSelect;
    bool _navPane = true;

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
            _selectionTimer.Dispose();
            _selectTimer.Dispose();
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
        _lastSelection = null;
        _lastSelectionCount = -1;
        if (_selectAfterNavigate != null) { _selectAttempts = 0; _selectTimer.Start(); }
        Navigated?.Invoke();
        SelectionChanged?.Invoke();
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
                if (fv.GetSelection(false, out var items) == ShellNative.S_OK && items != null)
                {
                    try
                    {
                        if (items.GetCount(out uint n) == ShellNative.S_OK && n > 0)
                        {
                            count = (int)n;
                            if (items.GetItemAt(0, out var item) == ShellNative.S_OK && item != null)
                            {
                                first = ShellNative.NameOf(item, SIGDN.FileSysPath);
                                Marshal.ReleaseComObject(item);
                            }
                        }
                    }
                    finally { Marshal.ReleaseComObject(items); }
                }
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

    void TrySelectPending()
    {
        string? path = _selectAfterNavigate;
        if (path == null || ++_selectAttempts > 30) { _selectTimer.Stop(); _selectAfterNavigate = null; return; }
        var view = GetView<IShellView>(ShellNative.IID_IShellView);
        if (view == null) return;
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != ShellNative.S_OK || pidl == IntPtr.Zero) { _selectAfterNavigate = null; return; }
            IntPtr child = ShellNative.ILFindLastID(pidl);
            uint flags = ShellNative.SVSI_SELECT | ShellNative.SVSI_DESELECTOTHERS | ShellNative.SVSI_ENSUREVISIBLE | ShellNative.SVSI_FOCUSED;
            if (view.SelectItem(child, flags) == ShellNative.S_OK)
            {
                _selectTimer.Stop();
                _selectAfterNavigate = null;
                FocusView();
                if (_renameAfterSelect) { _renameAfterSelect = false; RenameSelection(); }
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

    /// <summary>Lets the shell view handle its accelerators (Delete, F2, Ctrl+C/X/V/A...) before Rock Preview's menus.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
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

    // ---- COM callbacks (separate object so the Control itself is never exposed to COM) --------

    [ComVisible(true)]
    sealed class Callbacks(ExplorerHost host) : IExplorerBrowserEvents, IServiceProvider, IExplorerPaneVisibility
    {
        public int OnNavigationPending(IntPtr pidlFolder) => ShellNative.S_OK;
        public int OnViewCreated(IntPtr psv) => ShellNative.S_OK;
        public int OnNavigationComplete(IntPtr pidlFolder) { host.OnNavigationComplete(pidlFolder); return ShellNative.S_OK; }
        public int OnNavigationFailed(IntPtr pidlFolder) { host.OnNavigationFailedInternal(pidlFolder); return ShellNative.S_OK; }

        public int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject)
        {
            if (guidService == ShellNative.IID_IExplorerPaneVisibility && riid == ShellNative.IID_IExplorerPaneVisibility)
            {
                ppvObject = Marshal.GetComInterfaceForObject(this, typeof(IExplorerPaneVisibility));
                return ShellNative.S_OK;
            }
            ppvObject = IntPtr.Zero;
            return ShellNative.E_NOINTERFACE;
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

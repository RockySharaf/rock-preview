using System.ComponentModel;
using System.Runtime.InteropServices;
using Anything.Core;

namespace Anything.Explorer;

/// <summary>
/// Hosts the Windows Shell's own ExplorerBrowser — the same file view, navigation pane, context menus, drag and
/// drop, views and thumbnails as File Explorer — inside Rock Preview. Rock Preview keeps its own Back/Forward
/// history (so it can show button state) and tracks the selection for its preview pane.
/// </summary>
internal sealed partial class ExplorerHost : Control
{
    IExplorerBrowser? _browser;
    uint _cookie;
    readonly Callbacks _callbacks;
    readonly NavigationHistory<byte[]> _history = new();
    byte[]? _current;
    bool _historyNavigation;
    // A Back or Forward the shell is still opening: when it fails after BrowseToObject returned, the history step is
    // undone, so Back and Forward don't lead to the folder still open. _inStep: inside the step's own BrowseToObject.
    // _stepOpening: the place the step's BrowseToObject opens; _stepFailMessage: a failure the shell reported inside it,
    // told to the window once the history is final.
    NavigationHistory<byte[]>.Entry? _stepTarget;
    bool _stepWasBack, _inStep, _stepFailedEarly;
    byte[]? _stepOpening;
    string? _stepFailMessage;
    byte[]? _leavingItem;              // the item selected in the folder a navigation is leaving, kept with it in Back
    IShellItem? _pendingItem;           // keeps the navigation target alive until it completes
    string? _selectAfterNavigate;       // full path to select once the view shows it
    byte[]? _selectItemAfterNavigate;   // or an item's ID inside _selectItemIn, once that folder shows it (Back, Up)
    byte[]? _selectItemIn;
    int _selectAttempts;
    readonly System.Windows.Forms.Timer _selectionTimer = new() { Interval = 200 };
    readonly System.Windows.Forms.Timer _selectTimer = new() { Interval = 100 };
    readonly System.Windows.Forms.Timer _themeTimer = new() { Interval = 400 };
    readonly System.Windows.Forms.Timer _newFolderTimer = new() { Interval = NewFolderRename.WaitMilliseconds };
    string? _lastSelection;
    int _lastSelectionCount = -1;
    int _lastFocused = -1;
    int _firstIndex = -1;               // view index of a big selection's first item, read again each tick; -1 unknown
    bool _selectionReported;            // the view said the selection changed (CDBOSC_SELCHANGE) since the last poll
    int _itemCount, _lastItemCount = -1;
    bool _renameAfterSelect;
    // The selection is the item Back, Forward or Up selected by itself, not changed by the user since (see
    // SelectionFromNavigation). _navSelectionId: that item's ID in the view, once TrySelectPending has selected it.
    bool _selectionFromNavigation;
    byte[]? _navSelectionId;
    // The New folder command's registration for the shell's word of the folder it makes (CreateNewFolder), 0 when none,
    // and its second one for the view's own form of the folder when that differs (0 when none); the message that word
    // comes in, and the view's location when the command ran.
    uint _newFolderNotify, _newFolderNotifyShown;
    int _newFolderMessage, _newFolderCount;
    string? _newFolderLocation;
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

    /// <summary>
    /// The selection is only the item Back, Forward or Up selected by itself (the one they came from): the user hasn't
    /// changed the selection since. Such a selection doesn't make the search look inside it.
    /// </summary>
    public bool SelectionFromNavigation => _selectionFromNavigation;

    public ExplorerHost()
    {
        _callbacks = new Callbacks(this);
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        _selectionTimer.Tick += (_, _) => PollSelection();
        _selectTimer.Tick += (_, _) => TrySelectPending();
        _themeTimer.Tick += (_, _) => { _themeTimer.Stop(); ApplyViewTheme(); };
        _newFolderTimer.Tick += (_, _) => { _newFolderTimer.Stop(); EndNewFolderWatch(); };
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
    /// <summary>The archive file the location is in or is (Extract all works on it), or null: decided at each navigation.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? CurrentArchive { get; private set; }

    public bool HasLocation => _current != null;
    /// <summary>The open location's ID list (null before the first folder), to name it to Windows without asking the disk.</summary>
    public byte[]? CurrentIdList => _current;
    public bool CanGoBack => _history.CanGoBack;
    public bool CanGoForward => _history.CanGoForward;
    /// <summary>Names of the places Back and Forward go to ("Downloads"), for the buttons' tooltips; null when unknown.</summary>
    public string? BackName => _history.BackEntry?.Name;
    public string? ForwardName => _history.ForwardEntry?.Name;
    /// <summary>How many places Back holds: with the folder, where in the history the view is.</summary>
    public int BackCount => _history.BackCount;
    /// <summary>The last folder opened came from Back or Forward (which keep the history on the other side).</summary>
    public bool LastNavigationWasHistory { get; private set; }
    /// <summary>There is a folder above this one: false at the Desktop, the top of the namespace, as in Explorer.</summary>
    public bool CanGoUp => _current != null && !IdList.IsEmpty(_current);
    /// <summary>Name of the folder Up goes to ("Documents"), for the Up button's tooltip.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? ParentDisplayName { get; private set; }

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
        WatchUserInput(true);
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
        WatchUserInput(false);
        EndNewFolderWatch();
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
            _newFolderTimer.Dispose();
            WatchUserInput(false);
            EndNewFolderWatch();
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
        _menuHook.EndOpenInNewTab(raise: false); // a menu still open: its hook goes with the view
        _menuHook.ReleaseHandle();
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
        else
        {
            // Hidden (a search, another tab): Back's or Up's late select, or a new folder's rename, would come back to a
            // moved-on view (and a rename box out of sight would take the keys).
            DropItemSelect();
            DropNewFolderRename();
        }
    }

    /// <summary>
    /// Forgets a new folder's rename still to come: the wait for the shell's word of it, and its select and rename. The
    /// user went on meanwhile (input in the view, the view hidden), so what they do stands.
    /// </summary>
    void DropNewFolderRename()
    {
        EndNewFolderWatch();
        if (!_renameAfterSelect) return;
        _renameAfterSelect = false;
        _selectAfterNavigate = null;
        if (_selectItemAfterNavigate == null) _selectTimer.Stop();
    }

    /// <summary>Forgets the item Back or Up was to select (not a path asked for, nor a new folder's rename).</summary>
    void DropItemSelect()
    {
        if (_selectItemAfterNavigate == null) return;
        _selectItemAfterNavigate = _selectItemIn = null;
        if (_selectAfterNavigate == null) _selectTimer.Stop();
        if (_selectionFromNavigation)
        {
            // What is selected now is not Back's or Up's doing (the user went on, or the view hides): read it again.
            _selectionFromNavigation = false;
            _lastSelectionCount = -1;
        }
    }

    UserInputWatch? _inputWatch;

    /// <summary>
    /// The user clicking, turning the wheel or pressing a key in the folder view while Back's or Up's item is still to be
    /// selected: what they do then stands, so that select is dropped. Windows' own view windows aren't WinForms controls,
    /// so their input is seen here, as every posted message on the thread passes the message filters. Only from the
    /// moment the item is queued: the side button or key that started the Back or Up came before it. A selection the view
    /// makes by itself (the first item, after keyboard use) is not the user's and does not drop it.
    /// </summary>
    sealed class UserInputWatch(ExplorerHost host) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            try
            {
                if (host._selectItemAfterNavigate == null && !host._selectionFromNavigation
                    && host._newFolderNotify == 0 && !host._renameAfterSelect) return false;
                if (!NavigationInput.MovesOn(m.Msg, m.WParam, m.LParam)) return false;
                if (host.IsDisposed || !host.IsHandleCreated) return false;
                if (m.HWnd != host.Handle && !IsChild(host.Handle, m.HWnd)) return false;
                host.DropItemSelect();
                // Nor a new folder's rename still to come: a late one would take the focus from what the user does now
                // (their own F2 on it, a click elsewhere). Its own key (Ctrl+Shift+N) comes before the wait starts.
                host.DropNewFolderRename();
                // What makes the selection the user's is the user changing it, which the poll sees (PollSelection, by
                // the item's ID): a click on the column header (drawn inside the items window, so not told apart from a
                // click on an item) or on a scroll bar, the wheel, a key that moves nothing, or a click on the item
                // already selected leave it as it was.
            }
            catch (Exception) { } // never out of a message filter
            return false; // only watched: the message goes on as usual
        }
    }

    void WatchUserInput(bool on)
    {
        if (on && _inputWatch == null) Application.AddMessageFilter(_inputWatch = new UserInputWatch(this));
        else if (!on && _inputWatch != null) { Application.RemoveMessageFilter(_inputWatch); _inputWatch = null; }
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
        _selectItemAfterNavigate = _selectItemIn = null;
        _renameAfterSelect = false;
        _pendingItem = item;
        GiveUpStep();
        return _browser.BrowseToObject(item, ShellNative.SBSP_ABSOLUTE) == ShellNative.S_OK;
    }

    /// <summary>
    /// The user goes somewhere else (an address, Up, a link) while a Back or Forward is still opening: the shell cancels
    /// that step, so it stops being in flight and its history is put back. The folder left then goes into Back as usual,
    /// and the next folder to open is not taken for a history navigation.
    /// </summary>
    void GiveUpStep()
    {
        _historyNavigation = false;
        if (_stepTarget is not { } target) return;
        _stepTarget = null;
        UndoStep(target, _stepWasBack);
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
            if (!historyNavigation) GiveUpStep();
            _historyNavigation = historyNavigation;
            _pendingItem = item;
            return _browser.BrowseToObject(item, ShellNative.SBSP_ABSOLUTE) == ShellNative.S_OK;
        }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public void GoBack()
    {
        if (_current == null || !_history.CanGoBack) return;
        Step(back: true);
    }

    public void GoForward()
    {
        if (_current == null || !_history.CanGoForward) return;
        Step(back: false);
    }

    void Step(bool back)
    {
        _stepTarget = null;
        _stepFailedEarly = false;
        _stepFailMessage = null;
        _inStep = true;
        NavigationHistory<byte[]>.Entry? opened;
        try { opened = back ? _history.GoBack(Here(), OpenFromHistory) : _history.GoForward(Here(), OpenFromHistory); }
        finally { _inStep = false; _stepOpening = null; }
        string? failed = _stepFailMessage;
        _stepFailMessage = null;
        if (opened != null)
        {
            if (_stepFailedEarly) UndoStep(opened, back);  // the shell said it failed but BrowseToObject still returned S_OK
            else if (_historyNavigation) { _stepTarget = opened; _stepWasBack = back; } // still opening
        }
        // After the history is put back: the window then shows Back, Forward and their tooltips as they now are.
        if (failed != null) NavigationFailed?.Invoke(failed);
    }

    /// <summary>Puts the history back as it was before a Back (or Forward) to <paramref name="target"/> that failed.</summary>
    void UndoStep(NavigationHistory<byte[]>.Entry target, bool back)
    {
        // The step moved the place left onto the other side and took the target off: stepping the other way, with the
        // target as "here", does exactly the reverse.
        if (back) _history.GoForward(target, _ => true);
        else _history.GoBack(target, _ => true);
    }

    /// <summary>This place as the history keeps it: with the item selected now, selected again on coming back.</summary>
    NavigationHistory<byte[]>.Entry Here() => new(_current!, CurrentDisplayName, SelectedItemId());

    /// <summary>Back or Forward to a place, selecting the item that was selected when it was left (nothing if none was).</summary>
    bool OpenFromHistory(NavigationHistory<byte[]>.Entry entry)
    {
        // Set before browsing: the navigation can complete inside BrowseToObject.
        _selectAfterNavigate = null;
        _renameAfterSelect = false;
        _selectItemAfterNavigate = entry.Item;
        _selectItemIn = entry.Item != null ? entry.Place : null;
        _stepOpening = entry.Place;
        if (Navigate(entry.Place, historyNavigation: true)) return true;
        _selectItemAfterNavigate = _selectItemIn = null;
        return false;
    }

    /// <summary>Up to the folder above, with the folder just left selected and scrolled into view, as in Explorer.</summary>
    public void GoUp()
    {
        if (_browser == null || _current == null || IdList.Parent(_current) is not { } parent) return;
        _selectAfterNavigate = null;
        _renameAfterSelect = false;
        _selectItemAfterNavigate = IdList.LastItem(_current);
        _selectItemIn = parent;
        GiveUpStep();
        if (_browser.BrowseToIDList(IntPtr.Zero, ShellNative.SBSP_PARENT) != ShellNative.S_OK)
            _selectItemAfterNavigate = _selectItemIn = null;
    }

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
        LastNavigationWasHistory = _historyNavigation;
        _stepTarget = null;
        if (_historyNavigation) _historyNavigation = false;
        else if (_current != null && !_current.AsSpan().SequenceEqual(bytes))
            _history.Visit(new(_current, CurrentDisplayName, _leavingItem));
        _leavingItem = null;
        _current = bytes;
        _pendingItem = null;
        CurrentPath = ShellNative.NameOf(pidl, SIGDN.FileSysPath);
        CurrentEditingName = ShellNative.NameOf(pidl, SIGDN.DesktopAbsoluteEditing) ?? CurrentPath;
        CurrentDisplayName = ShellNative.NameOf(pidl, SIGDN.NormalDisplay) ?? CurrentPath;
        CurrentParsingName = ShellNative.NameOf(pidl, SIGDN.DesktopAbsoluteParsing) ?? CurrentPath;
        CurrentIconIndex = ShellInfo.GetIconIndex(pidl);
        CurrentArchive = ArchiveAbove(bytes, CurrentParsingName);
        ParentDisplayName = IdList.Parent(bytes) is { } parent ? NameOf(parent, SIGDN.NormalDisplay) : null;
        // An item to select belongs to the folder it was meant for: somewhere else, a same-named item would be picked.
        // The shell may hand back the folder's ID list with different bytes, so its name decides when they differ.
        if (_selectItemAfterNavigate != null && (_selectItemIn == null || !(_selectItemIn.AsSpan().SequenceEqual(bytes)
                || CurrentParsingName != null && string.Equals(NameOf(_selectItemIn, SIGDN.DesktopAbsoluteParsing), CurrentParsingName, StringComparison.OrdinalIgnoreCase))))
            _selectItemAfterNavigate = _selectItemIn = null;
        _lastSelection = null;
        _lastSelectionCount = -1;
        // A new folder: Back's or Up's item, if any, is to be selected again (below). From now on: Up's parent folder may
        // select it by itself before Rock Preview does.
        _selectionFromNavigation = _selectItemAfterNavigate != null;
        _navSelectionId = null;
        // A new folder's rename belongs to the folder it was made in: going elsewhere before it shows (Back, Up, a
        // double-click) drops it, or the rename box would open on a same-named item here.
        if (_renameAfterSelect && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(_selectAfterNavigate) ?? ""),
                Path.TrimEndingDirectorySeparator(CurrentPath ?? ""), StringComparison.OrdinalIgnoreCase))
        {
            _selectAfterNavigate = null;
            _renameAfterSelect = false;
        }
        if (_selectAfterNavigate != null || _selectItemAfterNavigate != null) { _selectAttempts = 0; _selectTimer.Start(); }
        ApplyViewTheme();
        HookViewMenu();
        Navigated?.Invoke();
        SelectionChanged?.Invoke();
    }

    readonly ViewMenuHook _menuHook = new();

    /// <summary>Each folder gets a new view window: its context menu is watched there (<see cref="ViewMenuHook"/>).</summary>
    void HookViewMenu()
    {
        var view = GetView<IShellView>(ShellNative.IID_IShellView);
        if (view == null) return;
        try
        {
            if (view.GetWindow(out IntPtr hwnd) != ShellNative.S_OK || hwnd == IntPtr.Zero || hwnd == _menuHook.Handle) return;
            _menuHook.Host ??= this;
            _menuHook.EndOpenInNewTab(raise: false);
            _menuHook.ReleaseHandle();
            _menuHook.AssignHandle(hwnd);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException) { }
        finally { Marshal.ReleaseComObject(view); }
    }

    /// <summary>
    /// The folder view's window, which owns the menu it shows for the items right-clicked: as that menu opens (the first
    /// popup of a menu loop it starts), "Open with" moves up to right after the default verb, Open, and a single folder's
    /// menu gets "Open in new tab" at its top (ExplorerHost.NewTab.cs).
    /// </summary>
    sealed partial class ViewMenuHook : NativeWindow
    {
        const int WM_ENTERMENULOOP = 0x0211, WM_EXITMENULOOP = 0x0212, WM_NCDESTROY = 0x0082;
        bool _firstPopup;

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_ENTERMENULOOP:
                    _firstPopup = m.WParam != IntPtr.Zero; // TRUE: a menu from TrackPopupMenu, the context menu
                    break;
                case Anything.Interop.Native.WM_INITMENUPOPUP when _firstPopup:
                    _firstPopup = false;
                    try
                    {
                        int open = Anything.Interop.Native.GetMenuDefaultItem(m.WParam, 1, Anything.Interop.Native.GMDI_USEDISABLED);
                        if (open >= 0) Anything.Interop.MenuOrder.MoveOpenWith(m.WParam, open + 1);
                        if (open >= 0) AddOpenInNewTab(m.WParam); // an item's menu (the background's has no default)
                    }
                    catch (Exception) { }
                    break;
                case WM_MENUSELECT:
                    TrackOpenInNewTab(m.WParam, m.LParam);
                    break;
                case WM_EXITMENULOOP:
                    _firstPopup = false;
                    EndOpenInNewTab(raise: true);
                    break;
                case WM_NCDESTROY:
                    EndOpenInNewTab(raise: false);
                    base.WndProc(ref m);
                    ReleaseHandle();
                    return;
            }
            base.WndProc(ref m);
        }
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
        // Only what belonged to the folder that failed: a navigation cancelled for a newer one leaves the newer one's alone.
        byte[]? failed = pidl != IntPtr.Zero ? ShellNative.CopyPidl(pidl) : null;
        string message = "Unable to open " + (ShellNative.NameOf(pidl, SIGDN.DesktopAbsoluteEditing) ?? "that location") + ".";
        // A Back or Forward being opened is undone only when its own folder failed, whether the shell says so inside the
        // step's BrowseToObject or later: it may report an earlier navigation, cancelled by the step, from either.
        bool stepInFlight = _inStep || _stepTarget != null;
        byte[]? stepPlace = _inStep ? _stepOpening : _stepTarget?.Place;
        bool stepFailed = failed != null && stepPlace != null && SamePlace(failed, stepPlace);
        if (!stepInFlight || stepFailed || failed == null)
        {
            _pendingItem = null;
            _historyNavigation = false;
        }
        if (failed != null && _selectItemIn != null && failed.AsSpan().SequenceEqual(_selectItemIn)) DropItemSelect();
        if (_inStep)
        {
            // Step undoes it once BrowseToObject returns (if that still said S_OK), then tells the window.
            if (stepFailed) _stepFailedEarly = true;
            if (stepFailed || _stepFailMessage == null) _stepFailMessage = message;
            return;
        }
        if (stepFailed && _stepTarget is { } target)
        {
            _stepTarget = null;
            UndoStep(target, _stepWasBack);
        }
        NavigationFailed?.Invoke(message); // after the history is put back, so the buttons show it
    }

    /// <summary>Whether two ID lists name the same place: the shell may hand back a folder's ID list with different bytes.</summary>
    static bool SamePlace(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b)
        || NameOf(a, SIGDN.DesktopAbsoluteParsing) is { } name && string.Equals(name, NameOf(b, SIGDN.DesktopAbsoluteParsing), StringComparison.OrdinalIgnoreCase);

    // ---- selection ----------------------------------------------------------------------------

    void PollSelection()
    {
        if (!Visible || _browser == null) return;
        string? first = null;
        int count = 0;
        bool reported = false;
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv != null)
        {
            try
            {
                if (fv.ItemCount(0x2 /* SVGIO_ALLVIEW */, out int all) == ShellNative.S_OK) _itemCount = all;
                // The count and the focused item are cheap; the selection array copies every selected item, so a big
                // selection (Ctrl+A among 100,000 files) is read again only when one of them changes or the view says
                // the selection did (CDBOSC_SELCHANGE). Which item comes first, and so is previewed, stays the array's;
                // its name alone is read again from the view each tick, as a rename changes none of those.
                int selected = fv.ItemCount(0x1 /* SVGIO_SELECTION */, out int s) == ShellNative.S_OK ? s : -1;
                int focused = fv.GetFocusedItem(out int f) == ShellNative.S_OK ? f : -1;
                reported = _selectionReported;
                _selectionReported = false;
                if (selected != 0)
                {
                    Func<int> firstSelected = () => fv.GetSelectedItem(-1, out int i) == ShellNative.S_OK ? i : -1;
                    ShellSelection.SelectedItemPath read = (int i, out string? p) => ReadSelectedItem(fv, i, out p);
                    if (ShellSelection.CanKeepFirst(selected, focused, reported, _lastSelectionCount, _lastFocused)
                        && ShellSelection.TryRefreshFirst(_firstIndex, _lastSelection, focused, firstSelected, read, out first))
                        count = selected;
                    else
                    {
                        (count, first) = ReadSelection(fv);
                        _firstIndex = count > ShellSelection.FullReadLimit ? ShellSelection.IndexOfFirst(first, focused, firstSelected, read) : -1;
                    }
                }
                _lastFocused = focused;
            }
            catch (COMException) { }
            finally { Marshal.ReleaseComObject(fv); }
        }
        if (count == _lastSelectionCount && string.Equals(first, _lastSelection, StringComparison.OrdinalIgnoreCase) && _itemCount == _lastItemCount)
        {
            // The same count and first item, but other items; or another item without a path (a phone's) in place of
            // Back's, which ends it.
            if (reported) { if (EndNavigationSelection(count)) SelectionChanged?.Invoke(); else SelectionContentChanged?.Invoke(); }
            return;
        }
        _lastItemCount = _itemCount;
        _lastSelection = first;
        _lastSelectionCount = count;
        EndNavigationSelection(count);
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Once Back's, Forward's or Up's item is selected (TrySelectPending), a selection that is anything but that one item
    /// is the user's: <see cref="SelectionFromNavigation"/> ends, and true is returned. Before then whatever the view
    /// shows is waited out: the user's own input drops that select (DropItemSelect), and the flag with it.
    /// </summary>
    bool EndNavigationSelection(int count)
    {
        if (!_selectionFromNavigation || _selectItemAfterNavigate != null) return false;
        if (count == 1 && _navSelectionId != null && SelectedItemId() is { } id && SameItemInView(id, _navSelectionId)) return false;
        _selectionFromNavigation = false;
        return true;
    }

    static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    const int SHCIDS_CANONICALONLY = 0x10000000;

    /// <summary>
    /// Whether two IDs of items in the view's folder name the same item. Compared by the folder (by name, not the disk):
    /// the view swaps in new bytes for an item when it is told of a change (a new modified date), and the history's copy
    /// may differ from the view's.
    /// </summary>
    bool SameItemInView(byte[] a, byte[] b)
    {
        if (a.AsSpan().SequenceEqual(b)) return true;
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return false;
        IntPtr ptr = IntPtr.Zero, pa = IntPtr.Zero, pb = IntPtr.Zero;
        object? obj = null;
        try
        {
            var iid = IID_IShellFolder;
            if (fv.GetFolder(ref iid, out ptr) != ShellNative.S_OK || ptr == IntPtr.Zero) return false;
            obj = Marshal.GetObjectForIUnknown(ptr);
            if (obj is not Anything.Interop.IShellFolder folder) return false;
            pa = Marshal.AllocCoTaskMem(a.Length);
            Marshal.Copy(a, 0, pa, a.Length);
            pb = Marshal.AllocCoTaskMem(b.Length);
            Marshal.Copy(b, 0, pb, b.Length);
            int hr = folder.CompareIDs(SHCIDS_CANONICALONLY, pa, pb);
            return hr >= 0 && (short)(hr & 0xFFFF) == 0; // the result is the HRESULT's code: 0 for the same item
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return false; }
        finally
        {
            if (pa != IntPtr.Zero) Marshal.FreeCoTaskMem(pa);
            if (pb != IntPtr.Zero) Marshal.FreeCoTaskMem(pb);
            if (obj != null) Marshal.ReleaseComObject(obj);
            if (ptr != IntPtr.Zero) Marshal.Release(ptr);
        }
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
    /// The file-system path of the view's item at <paramref name="index"/> (null for a virtual item); false when it can't
    /// be read or isn't selected. One item, named from its pidl, so no disk.
    /// </summary>
    static bool ReadSelectedItem(IFolderView2 fv, int index, out string? path)
    {
        path = null;
        if (fv.Item(index, out var pidl) != ShellNative.S_OK || pidl == IntPtr.Zero) return false;
        try
        {
            if (fv.GetSelectionState(pidl, out uint state) != ShellNative.S_OK || (state & ShellNative.SVSI_SELECT) == 0) return false;
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
        var iid = ShellNative.IID_IShellItem;
        if (fv.GetItem(index, ref iid, out var ptr) != ShellNative.S_OK || ptr == IntPtr.Zero) return false;
        object? obj = null;
        try
        {
            obj = Marshal.GetObjectForIUnknown(ptr);
            if (obj is not IShellItem item) return false;
            path = ShellNative.NameOf(item, SIGDN.FileSysPath);
            return true;
        }
        finally
        {
            if (obj != null) Marshal.ReleaseComObject(obj);
            Marshal.Release(ptr);
        }
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
        if (OpenShortcutFolder()) return true; // a shortcut to a folder opens it here, whatever opens folders (ExplorerHost.Links.cs)
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
        byte[]? itemId = _selectItemAfterNavigate;
        if ((path == null && itemId == null) || ++_selectAttempts > 30)
        {
            if (itemId != null && _selectionFromNavigation) { _selectionFromNavigation = false; _lastSelectionCount = -1; } // never selected
            _selectTimer.Stop();
            _selectAfterNavigate = null;
            _selectItemAfterNavigate = _selectItemIn = null;
            _renameAfterSelect = false;
            return;
        }
        // A new folder's rename only while the keys are still in this view, where the command left them: gone to the
        // search box or the address bar meanwhile (a click there is no input in the view), what the user does stands.
        if (_renameAfterSelect && !KeysInView()) { DropNewFolderRename(); return; }
        // A rename box closes when the view gets the focus after it opens, so the view gets it first, a tick earlier.
        if (_renameAfterSelect && _selectAttempts == 1) { FocusView(); return; }
        var view = GetView<IShellView>(ShellNative.IID_IShellView);
        if (view == null) return;
        IntPtr pidl = IntPtr.Zero;
        try
        {
            IntPtr child;
            if (path != null)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != ShellNative.S_OK || pidl == IntPtr.Zero) { _selectAfterNavigate = null; _renameAfterSelect = false; return; }
                child = ShellNative.ILFindLastID(pidl);
            }
            else
            {
                // The item's own ID inside the folder (Back, Up): found in virtual folders and libraries too.
                pidl = Marshal.AllocCoTaskMem(itemId!.Length);
                Marshal.Copy(itemId, 0, pidl, itemId.Length);
                child = pidl;
            }
            uint flags = (_renameAfterSelect ? ShellNative.SVSI_EDIT : ShellNative.SVSI_SELECT)
                | ShellNative.SVSI_DESELECTOTHERS | ShellNative.SVSI_ENSUREVISIBLE | ShellNative.SVSI_FOCUSED;
            // A path outside the folder shown (a library shows its locations' items as its own) is found by its path among
            // the view's items: there the view's SelectItem says S_OK for the folder's own ID and selects nothing.
            bool inView = path == null || string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? ""),
                Path.TrimEndingDirectorySeparator(CurrentPath ?? ""), StringComparison.OrdinalIgnoreCase);
            if (inView ? view.SelectItem(child, flags) == ShellNative.S_OK : SelectInViewByPath(path!, flags))
            {
                _selectTimer.Stop();
                _selectAfterNavigate = null;
                _selectItemAfterNavigate = _selectItemIn = null;
                if (path == null) // Back's, Forward's or Up's item: selected by the navigation, not by the user
                {
                    _selectionFromNavigation = true;
                    // Its ID as the view holds it now (the history's copy may differ in bytes): what the poll compares.
                    _navSelectionId = SelectedItemId() ?? itemId;
                    _lastSelectionCount = -1; // read again and told to the window, also when the view had already selected it
                }
                if (_renameAfterSelect) _renameAfterSelect = false;
                // Back's or Up's item takes the focus only while the keys are still in this view: not when the view is
                // hidden (a search, another tab) or the user has gone on to the address bar, the navigation pane...
                else if (path != null || KeysInView()) FocusView();
            }
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                if (path != null) ShellNative.ILFree(pidl);
                else Marshal.FreeCoTaskMem(pidl);
            }
            Marshal.ReleaseComObject(view);
        }
    }

    /// <summary>
    /// Whether the form's own record of where the keys go ends at this host (also while the app is in the background).
    /// Follows the chain of ActiveControls down from the form: the nearest container alone (the split) keeps naming this
    /// host after the focus has gone to the address bar or the navigation pane, which sit outside it.
    /// </summary>
    bool IsFormsActiveControl()
    {
        Control? active = FindForm()?.ActiveControl;
        for (int depth = 0; depth < 32 && active != null && active != this && active is ContainerControl cc; depth++)
        {
            var next = cc.ActiveControl;
            if (next == null || next == active) break;
            active = next;
        }
        return active == this;
    }

    /// <summary>The view is shown and the keys are in it (also while the app is in the background).</summary>
    bool KeysInView() => Visible && (ContainsFocus || IsFormsActiveControl());

    /// <summary>
    /// The selected item's ID inside the folder (the focused one when it is selected, else the first selected), kept in
    /// the history so Back selects it again; null when nothing is selected.
    /// </summary>
    byte[]? SelectedItemId()
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return null;
        try
        {
            if (fv.GetFocusedItem(out int focused) == ShellNative.S_OK && SelectedItemAt(fv, focused) is { } item) return item;
            // GetSelectedItem's documentation doesn't say whether the start index itself counts: both ways are tried.
            foreach (int start in (ReadOnlySpan<int>)[0, -1])
                if (fv.GetSelectedItem(start, out int index) == ShellNative.S_OK && SelectedItemAt(fv, index) is { } first) return first;
            return null;
        }
        catch (COMException) { return null; }
        finally { Marshal.ReleaseComObject(fv); }
    }

    /// <summary>The ID of the view's item at <paramref name="index"/> when it is selected, else null.</summary>
    static byte[]? SelectedItemAt(IFolderView2 fv, int index)
    {
        if (index < 0 || fv.Item(index, out var pidl) != ShellNative.S_OK || pidl == IntPtr.Zero) return null;
        try
        {
            return fv.GetSelectionState(pidl, out uint state) == ShellNative.S_OK && (state & ShellNative.SVSI_SELECT) != 0
                ? ShellNative.CopyPidl(pidl) : null;
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }

    /// <summary>A display name of an ID list kept as bytes.</summary>
    static string? NameOf(byte[] idList, SIGDN kind)
    {
        IntPtr p = Marshal.AllocCoTaskMem(idList.Length);
        try
        {
            Marshal.Copy(idList, 0, p, idList.Length);
            return ShellNative.NameOf(p, kind);
        }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>The attributes in <paramref name="mask"/> of an ID list kept as bytes (0 when it can't be asked).</summary>
    static uint AttributesOf(byte[] idList, uint mask)
    {
        IntPtr p = Marshal.AllocCoTaskMem(idList.Length);
        IShellItem? item = null;
        try
        {
            Marshal.Copy(idList, 0, p, idList.Length);
            var iid = ShellNative.IID_IShellItem;
            if (ShellNative.SHCreateItemFromIDList(p, ref iid, out item) != ShellNative.S_OK || item == null) return 0;
            return item.GetAttributes(mask, out uint attrs) < 0 ? 0 : attrs & mask;
        }
        catch (COMException) { return 0; }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
            Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>
    /// The archive the location is in or is (a zip or other archive file opened as a folder), by the attributes of the
    /// location and the folders above it: a stream is a file, so a real folder named "site.zip" never counts. Only
    /// asked when a part of the location's name looks like an archive, so ordinary folders cost nothing.
    /// </summary>
    static string? ArchiveAbove(byte[] location, string? parsingName)
    {
        if (CommandBarRules.ArchiveContaining(parsingName, archiveFolders: true) == null) return null;
        for (byte[]? at = location; at != null && !IdList.IsEmpty(at); at = IdList.Parent(at))
            if ((AttributesOf(at, ShellSelection.SfgaoStream) & ShellSelection.SfgaoStream) != 0)
                return NameOf(at, SIGDN.FileSysPath) ?? NameOf(at, SIGDN.DesktopAbsoluteParsing);
        return null;
    }

    /// <summary>Whether the one selected item is a file (a zip counts: a folder and a stream), not a plain folder. From the attributes the view holds.</summary>
    public bool SelectedItemIsFile()
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return false;
        try
        {
            if (fv.GetSelection(false, out var items) != ShellNative.S_OK || items == null) return false;
            try
            {
                if (items.GetCount(out uint n) != ShellNative.S_OK || n != 1) return false;
                if (items.GetAttributes(ShellNative.SIATTRIBFLAGS_AND, ShellSelection.SfgaoFolder | ShellSelection.SfgaoStream, out uint attrs) < 0) return false;
                return (attrs & ShellSelection.SfgaoStream) != 0 || (attrs & ShellSelection.SfgaoFolder) == 0;
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        catch (COMException) { return false; }
        finally { Marshal.ReleaseComObject(fv); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    // ---- commands ----------------------------------------------------------------------------

    /// <summary>
    /// Creates a new folder where the view is, selects it and starts renaming, like Explorer: by the view's own New
    /// folder command when it can (Windows' name in the user's language, Ctrl+Z undoes it, libraries too). Otherwise
    /// "New folder" (or "New folder (2)"...) in <paramref name="folder"/> (a library's save folder), by Windows' file
    /// operation so it can still be undone, and only when that fails made directly.
    /// </summary>
    public void CreateNewFolder(string? folder)
    {
        DropItemSelect(); // Back's or Up's late select would end the rename box
        // Where the folder lands on disk: a library makes it in its save folder.
        if (NewFolderRename.NeedsSaveFolder(folder))
            folder = (folder ?? CurrentParsingName) is { } name ? ShellDragDrop.FileSystemFolder(name) : null;
        // Hosted here, the view makes the folder but leaves it unselected and not renaming: done as Explorer does once
        // the shell tells which folder it made (OnNewFolderMade). An earlier command still waiting ends: this one's is
        // the folder to rename. A wait that brings no word ends by itself after a while (NewFolderRename.WaitMilliseconds).
        WatchForNewFolder(folder);
        if (NewFolderInView(out int refused))
        {
            if (_newFolderNotify != 0) _newFolderTimer.Start();
            return;
        }
        EndNewFolderWatch();
        if (refused == HResultAccessDenied) // said as when making it directly fails, below; a cancel says nothing
        {
            NavigationFailed?.Invoke("Can't create a folder here: " + new Win32Exception(5 /* ERROR_ACCESS_DENIED */).Message);
            return;
        }
        if (refused != 0 || folder == null) return;
        string path = Path.Combine(folder, "New folder");
        for (int n = 2; Directory.Exists(path) || File.Exists(path); n++) path = Path.Combine(folder, $"New folder ({n})");
        if (!NewFolderWithUndo(folder, Path.GetFileName(path)))
        {
            try { Directory.CreateDirectory(path); }
            catch (Exception ex) { NavigationFailed?.Invoke("Can't create a folder here: " + ex.Message); return; }
        }
        SelectAndRename(path);
    }

    /// <summary>Selects a new item here once the view shows it, scrolled into view, with its name in edit mode.</summary>
    void SelectAndRename(string path)
    {
        _selectAfterNavigate = path;
        _selectItemAfterNavigate = _selectItemIn = null;
        _renameAfterSelect = true;
        _selectAttempts = 0;
        _selectTimer.Start();
    }

    /// <summary>
    /// Asks the shell for word of the folder the New folder command is about to make in <paramref name="folder"/> (on
    /// disk; a library's save folder). That word (SHCNE_MKDIR) is the one Windows' own file operation raises itself, in
    /// this process, as it makes the folder, carrying the folder's ID list: the view learns of its new item the same
    /// way. Shell-level only, so it never waits on a change notice from the disk or a share (many send none), and no
    /// clock decides when the rename starts: one only ends a wait that brought no word. Each command gets a message of
    /// its own, so a word still queued for an earlier one is told apart.
    /// The word names the folder as the shell saw it made, which need not be its path's ID list: the Desktop shown as the
    /// top of the namespace, This PC's Downloads or Documents name their items their own way, and a registration on one
    /// form of a folder hears nothing raised in another. So the view's own folder, as the view names it, is asked for too,
    /// by a registration of its own with the same message: Windows faults on a registration with more than one entry.
    /// </summary>
    void WatchForNewFolder(string? folder)
    {
        EndNewFolderWatch();
        if (folder == null || !IsHandleCreated) return;
        if (SHParseDisplayName(folder, IntPtr.Zero, out var onDisk, 0, out _) != ShellNative.S_OK || onDisk == IntPtr.Zero) return;
        IntPtr shown = IntPtr.Zero;
        try
        {
            _newFolderMessage = NewFolderRename.MessageFor(++_newFolderCount);
            _newFolderNotify = RegisterForNewFolder(onDisk);
            if (_newFolderNotify == 0) return;
            _newFolderLocation = CurrentParsingName;
            if (_current == null) return;
            shown = Marshal.AllocCoTaskMem(_current.Length);
            Marshal.Copy(_current, 0, shown, _current.Length);
            if (!ILIsEqual(onDisk, shown)) _newFolderNotifyShown = RegisterForNewFolder(shown);
        }
        finally // a registration keeps its own copy
        {
            ShellNative.ILFree(onDisk);
            if (shown != IntPtr.Zero) Marshal.FreeCoTaskMem(shown);
        }
    }

    /// <summary>
    /// One registration for SHCNE_MKDIR in the folder <paramref name="pidl"/> (not below it), told in the command's
    /// message; 0 when refused.
    /// </summary>
    uint RegisterForNewFolder(IntPtr pidl)
    {
        var entry = new SHChangeNotifyEntry { pidl = pidl, fRecursive = 0 };
        return SHChangeNotifyRegister(Handle, SHCNRF_ShellLevel | SHCNRF_NewDelivery, SHCNE_MKDIR, (uint)_newFolderMessage, 1, ref entry);
    }

    /// <summary>Stops waiting for the New folder command's folder, if it still is (also when the host's window goes).</summary>
    void EndNewFolderWatch()
    {
        _newFolderTimer.Stop();
        if (_newFolderNotify != 0) SHChangeNotifyDeregister(_newFolderNotify);
        if (_newFolderNotifyShown != 0) SHChangeNotifyDeregister(_newFolderNotifyShown);
        _newFolderNotify = _newFolderNotifyShown = 0;
        _newFolderLocation = null;
    }

    /// <summary>
    /// The shell's word of a folder made where the New folder command ran: it is selected and its rename started, while
    /// the user is still where the command left them (the same location, shown, the keys in the view, which the command
    /// gave the focus). Gone to the search box, the address bar or another tab: left alone. Word for an earlier command,
    /// or one that came after this one ended, is only let go. The path is read from the folder's ID list in whichever form
    /// the word names it; the first word ends the wait, so the same folder told in both forms is renamed once.
    /// </summary>
    void OnNewFolderMade(ref Message m)
    {
        string? path = null;
        IntPtr held = SHChangeNotification_Lock(m.WParam, (uint)m.LParam, out IntPtr pidls, out int what);
        if (held == IntPtr.Zero) return;
        try
        {
            if (m.Msg == _newFolderMessage && _newFolderNotify != 0 && (what & SHCNE_MKDIR) != 0 && pidls != IntPtr.Zero)
                path = ShellNative.NameOf(Marshal.ReadIntPtr(pidls), SIGDN.FileSysPath);
        }
        finally { SHChangeNotification_Unlock(held); } // always: the word's memory is freed only so
        if (path == null) return;
        string? location = _newFolderLocation;
        EndNewFolderWatch();
        if (NewFolderRename.MayStart(KeysInView(), CurrentParsingName, location)) SelectAndRename(path);
    }

    protected override void WndProc(ref Message m)
    {
        if (NewFolderRename.IsMessage(m.Msg))
        {
            try { OnNewFolderMade(ref m); } catch (Exception) { } // never out of the window's message
            return;
        }
        base.WndProc(ref m);
    }

    const int SHCNRF_ShellLevel = 0x0002, SHCNRF_NewDelivery = 0x8000, SHCNE_MKDIR = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    struct SHChangeNotifyEntry { public IntPtr pidl; public int fRecursive; }

    [DllImport("shell32.dll")]
    static extern uint SHChangeNotifyRegister(IntPtr hwnd, int fSources, int fEvents, uint wMsg, int cEntries, ref SHChangeNotifyEntry pshcne);
    [DllImport("shell32.dll")] static extern bool SHChangeNotifyDeregister(uint ulID);
    [DllImport("shell32.dll")] static extern bool ILIsEqual(IntPtr pidl1, IntPtr pidl2);
    [DllImport("shell32.dll")] static extern IntPtr SHChangeNotification_Lock(IntPtr hChange, uint dwProcId, out IntPtr pppidl, out int plEvent);
    [DllImport("shell32.dll")] static extern bool SHChangeNotification_Unlock(IntPtr hLock);

    /// <summary>
    /// In a library the view's items are the library's own, so the ID of a folder's path on disk isn't found among them:
    /// the item whose path it is is looked up in the view instead, newest items first (they come last).
    /// </summary>
    bool SelectInViewByPath(string path, uint flags)
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return false;
        try
        {
            if (fv.ItemCount(0x2 /* SVGIO_ALLVIEW */, out int count) != ShellNative.S_OK) return false;
            var iid = ShellNative.IID_IShellItem;
            for (int i = count - 1; i >= 0 && i >= count - 5000; i--)
            {
                if (fv.GetItem(i, ref iid, out var ptr) != ShellNative.S_OK || ptr == IntPtr.Zero) continue;
                object? obj = null;
                try
                {
                    obj = Marshal.GetObjectForIUnknown(ptr);
                    if (obj is IShellItem item && string.Equals(ShellNative.NameOf(item, SIGDN.FileSysPath), path, StringComparison.OrdinalIgnoreCase))
                        return fv.SelectItem(i, flags) == ShellNative.S_OK;
                }
                finally
                {
                    if (obj != null) Marshal.ReleaseComObject(obj);
                    Marshal.Release(ptr);
                }
            }
            return false;
        }
        catch (COMException) { return false; }
        finally { Marshal.ReleaseComObject(fv); }
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
        // Escape, Ctrl+N (the view's own opens a File Explorer window) and F1 (the view's own does nothing here; the
        // window's opens Search Syntax, as the menu says): the window's, as with the keys in other places.
        if (keyData is Keys.Escape or (Keys.Control | Keys.N) or Keys.F1) return base.ProcessCmdKey(ref msg, keyData);
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
            // No select until the new folder has opened: its view may already be the current one before it completes,
            // and an item ID from the folder being left would pick a same-named item there. Completion restarts it.
            host._selectTimer.Stop();
            // The folder being left still shows: what is selected in it goes into Back with it.
            try { host._leavingItem = host.SelectedItemId(); } catch (Exception) { host._leavingItem = null; }
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
                {
                    host._selectionReported = true; // the next poll reads a big selection again; the view calls on the UI thread
                    host._selectionVersion++;       // and the status bar adds the selection up again
                }
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

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Anything.Explorer;

/// <summary>
/// Hosts Windows' own Explorer navigation pane — the Shell NamespaceTreeControl from explorerframe.dll — with
/// the Windows 11 File Explorer roots (Home, Gallery, your pinned folders, OneDrive, This PC, Network, Linux), set up
/// the way Explorer sets up its own (reverse-engineered with REA from ExplorerFrame.dll and measured on a live window,
/// see docs/research/07-windows-explorer-breakdown.md): Explorer's tree styles and extended styles, 32px rows with an
/// 8px indent, and the group dividers (a double-height first row with a line at a quarter of its height). Context
/// menus, rename, drag and drop, icons, overlays and change notifications are all the shell's own.
/// </summary>
internal sealed partial class NavigationTree : Control
{
    // Explorer's own pane (window styles read from a live Explorer: 0x5621922D / extended 0x3C15): chevrons always
    // shown (no fade), full-row "Explorer" theme selection, no lines, even row heights, no horizontal scrolling, rich
    // tooltips instead of infotips.
    const NSTCSTYLE TreeStyle = NSTCSTYLE.HASEXPANDOS | NSTCSTYLE.ROOTHASEXPANDO | NSTCSTYLE.SHOWSELECTIONALWAYS
                                | NSTCSTYLE.FULLROWSELECT | NSTCSTYLE.TABSTOP | NSTCSTYLE.EVENHEIGHT
                                | NSTCSTYLE.NOINFOTIP | NSTCSTYLE.RICHTOOLTIP;

    // TVS_EX bits Explorer adds on top of the control's own (0x415 -> 0x3C15): undocumented, they give its rows the
    // Windows 11 left padding.
    const int ExplorerExtendedStyle = 0x3800;
    const int TVM_SETITEMHEIGHT = 0x111B, TVM_GETITEMHEIGHT = 0x111C, TVM_SETINDENT = 0x1107, TVM_SETEXTENDEDSTYLE = 0x112C,
              TVM_GETNEXTITEM = 0x110A, TVM_GETITEMW = 0x113E, TVM_SETITEMW = 0x113F, TVM_SETBKCOLOR = 0x111D, TVM_SETTEXTCOLOR = 0x111E;
    const int TVIF_TEXT = 0x1, TVIF_HANDLE = 0x10, TVIF_INTEGRAL = 0x80, TVIF_CONTENTOFFSET = 0x400; // 0x400: iReserved = how far down the row's content starts (comctl32 private, used by ExplorerFrame)

    const string HomeGuid = "shell:::{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
    const string QuickAccessGuid = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";
    const string GalleryGuid = "shell:::{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";
    const string ThisPcGuid = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    const string NetworkGuid = "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";
    const string LinuxGuid = "shell:::{B2B4A4D1-2754-4140-A2EB-9A76D9D7CDC6}";

    INameSpaceTreeControl? _tree;
    uint _cookie;
    IntPtr _hwndTree;       // the NamespaceTreeControl window (child of this control)
    IntPtr _hwndFocus;      // the SysTreeView32 inside it
    readonly Callbacks _callbacks;
    readonly List<IShellItem> _roots = [];

    // The tree posts OnSelectionChanged (it arrives after SetItemState returns), so programmatic changes are
    // recognised by name: _current is kept equal to the tree's selection and a matching notification is ignored.
    // _suppress additionally covers any notification delivered synchronously while we change the tree.
    int _suppress;
    int _selectionChanges;          // bumped on every OnSelectionChanged
    string? _current;               // parsing name of the tree's selected item, as far as we know
    string? _raisePending;          // FolderSelected waiting to be raised (posted / debounced)
    long _lastRaiseTicks, _lastInputTicks;      // input = a click or key the tree reported (OnItemClick / OnKeyboardInput)
    bool _lastInputWasKey;
    string? _deferredSelect;        // SelectFolder before the handle exists
    readonly HashSet<(string Name, int Level)> _groupFirst = [];           // visible top-level rows that start a group (divider above)
    readonly HashSet<IntPtr> _tallRows = [];                               // rows we made double height
    string[] _group0 = [], _group2 = [];   // display names of the roots before / after the pinned folders
    TreeHook? _hook;
    bool _groupLayoutQueued, _scrolledOnce;
    IntPtr _dividerPen;
    int _dividerPenWidth;

    // SelectFolder for an item whose ancestors are still being enumerated: retried on a timer.
    IShellItem? _pendingItem;
    List<IShellItem>? _pendingChain; // root .. parent, top-down
    int _pendingAttempts;
    readonly System.Windows.Forms.Timer _pendingTimer = new() { Interval = 100 };
    readonly System.Windows.Forms.Timer _raiseTimer = new() { Interval = 300 };

    /// <summary>The user picked a folder (click or keyboard). Argument: parsing name ("C:\Users", "::{20D04FE0-...}").</summary>
    public event Action<string>? FolderSelected;

    /// <summary>Why the shell tree could not be created (null when it works).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Error { get; private set; }

    /// <summary>Parsing name of the highlighted folder (null when nothing is selected).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedParsingName => _current;

    public NavigationTree()
    {
        _callbacks = new Callbacks(this);
        SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        BackColor = SystemColors.Window;
        _pendingTimer.Tick += (_, _) => OnPendingTick();
        _raiseTimer.Tick += (_, _) => { _raiseTimer.Stop(); RaisePending(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x02000000; // WS_CLIPCHILDREN: never paint over the shell tree
            return cp;
        }
    }

    // ---- lifetime -----------------------------------------------------------------------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_tree != null || DesignMode) return;
        try
        {
            var type = Type.GetTypeFromCLSID(NavTreeNative.CLSID_NamespaceTreeControl, throwOnError: true)!;
            _tree = (INameSpaceTreeControl)Activator.CreateInstance(type)!;
            var rc = new RECT(ClientRectangle);
            Marshal.ThrowExceptionForHR(_tree.Initialize(Handle, ref rc, TreeStyle));
            _tree.SetTheme("Explorer");
            if (_tree is INameSpaceTreeControl2 tree2)
            {
                // What Explorer sets on its pane (ExplorerFrame.dll draws its group dividers only with both bits).
                var style2 = NSTCSTYLE2.DISPLAYPADDING | NSTCSTYLE2.DISPLAYPINNEDONLY;
                try { tree2.SetControlStyle2(style2, style2); } catch (COMException) { }
            }
            Marshal.ThrowExceptionForHR(_tree.TreeAdvise(_callbacks, out _cookie));
            FindTreeWindows();
            ApplyExplorerMetrics();
            ResizeTree();
            AddRoots();
            BeginInvoke(() => { if (_hwndTree != IntPtr.Zero) ShellDragDrop.CopyFirstInside(_hwndTree); }); // drops copy unless a key says move
            Error = null;
            if (_deferredSelect != null) { var s = _deferredSelect; _deferredSelect = null; SelectFolder(s); }
        }
        catch (Exception ex)
        {
            ReleaseTree();
            Error = "The Windows navigation pane could not be created: " + ex.Message;
            Invalidate();
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_tree != null && RecreatingHandle) _deferredSelect ??= _current;
        ReleaseTree();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseTree();
            _pendingTimer.Dispose();
            _raiseTimer.Dispose();
        }
        if (_dividerPen != IntPtr.Zero) { NavTreeNative.DeleteObject(_dividerPen); _dividerPen = IntPtr.Zero; }
        base.Dispose(disposing);
    }

    void ReleaseTree()
    {
        _pinDrag = null;
        CancelPending();
        _raiseTimer.Stop();
        _raisePending = null;
        var t = _tree;
        if (t == null) return;
        _tree = null;
        try { if (_cookie != 0) t.TreeUnadvise(_cookie); } catch { }
        _cookie = 0;
        try { t.RemoveAllRoots(); } catch { }
        ReleaseRoots();
        _hook?.ReleaseHandle();
        _hook = null;
        _tallRows.Clear();
        if (_hwndTree != IntPtr.Zero && IsWindow(_hwndTree)) DestroyWindow(_hwndTree);
        _hwndTree = _hwndFocus = IntPtr.Zero;
        try { Marshal.FinalReleaseComObject(t); } catch { }
        _current = null;
    }

    void ReleaseRoots()
    {
        foreach (var r in _roots) { try { Marshal.ReleaseComObject(r); } catch { } }
        _roots.Clear();
    }

    void FindTreeWindows()
    {
        _hwndTree = IntPtr.Zero;
        if (_tree is IOleWindow ow && ow.GetWindow(out var h) == NavTreeNative.S_OK) _hwndTree = h;
        if (_hwndTree == IntPtr.Zero) _hwndTree = NavTreeNative.GetWindow(Handle, NavTreeNative.GW_CHILD);
        _hwndFocus = _hwndTree == IntPtr.Zero ? IntPtr.Zero : NavTreeNative.FindWindowEx(_hwndTree, IntPtr.Zero, "SysTreeView32", null);
        if (_hwndFocus == IntPtr.Zero) _hwndFocus = _hwndTree;
    }

    void ResizeTree()
    {
        if (_hwndTree == IntPtr.Zero) return;
        var size = ClientSize;
        NavTreeNative.SetWindowPos(_hwndTree, IntPtr.Zero, 0, 0, Math.Max(0, size.Width), Math.Max(0, size.Height),
            NavTreeNative.SWP_NOZORDER | NavTreeNative.SWP_NOACTIVATE);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ResizeTree();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Error == null) return;
        var r = ClientRectangle;
        r.Inflate(-8, -8);
        TextRenderer.DrawText(e.Graphics, Error, Font, r, SystemColors.GrayText,
            TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (_hwndFocus == IntPtr.Zero) return;
        // WinForms focus lands on us; hand it to the tree. A TreeView without a selection selects its first item
        // when it gains focus — that is not the user picking a folder.
        _suppress++;
        try { NavTreeNative.SetFocus(_hwndFocus); }
        finally { _suppress--; }
        _current = TreeSelectionName() ?? _current; // its (posted) notification then matches and is ignored
    }

    // ---- roots --------------------------------------------------------------------------------

    void AddRoots()
    {
        if (_tree == null) return;
        var flags = SHCONTF.FOLDERS | SHCONTF.NAVIGATION_ENUM;
        try
        {
            using var adv = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            if (adv?.GetValue("Hidden") is int hidden && hidden == 1) flags |= SHCONTF.INCLUDEHIDDEN;
            if (adv?.GetValue("ShowSuperHidden") is int super && super == 1) flags |= SHCONTF.INCLUDESUPERHIDDEN;
        }
        catch { }

        _suppress++;
        try
        {
            // Windows 11 Explorer: Home, Gallery (and OneDrive) | the pinned and frequent folders, shown at the top
            // level by a hidden, expanded Quick access root | This PC, Network, Linux. On Windows 10 (no Home) Quick
            // access itself is the first root.
            int first = _roots.Count;
            AddRoot(flags, NSTCROOTSTYLE.VISIBLE, HomeGuid);
            bool hasHome = _roots.Count > first;
            if (!hasHome) AddRoot(flags, NSTCROOTSTYLE.VISIBLE, QuickAccessGuid);
            AddRoot(flags, NSTCROOTSTYLE.VISIBLE, GalleryGuid);
            AddRoot(flags, NSTCROOTSTYLE.VISIBLE, "shell:OneDrive");
            _group0 = _roots.Skip(first).Select(r => ShellNative.NameOf(r, SIGDN.NormalDisplay) ?? "").ToArray();
            if (hasHome) AddRoot(flags, NSTCROOTSTYLE.HIDDEN | NSTCROOTSTYLE.EXPANDED, QuickAccessGuid);
            int afterPinned = _roots.Count;
            AddRoot(flags, NSTCROOTSTYLE.EXPANDED, ThisPcGuid);
            AddRoot(flags, NSTCROOTSTYLE.VISIBLE, NetworkGuid);
            if (HasWslDistros()) AddRoot(flags, NSTCROOTSTYLE.VISIBLE, LinuxGuid, @"\\wsl.localhost");
            _group2 = _roots.Skip(afterPinned).Select(r => ShellNative.NameOf(r, SIGDN.NormalDisplay) ?? "").ToArray();
        }
        finally { _suppress--; }
        _current = TreeSelectionName();
        QueueGroupLayout();
    }

    void AddRoot(SHCONTF flags, NSTCROOTSTYLE style, params string[] candidates)
    {
        foreach (var name in candidates)
        {
            var item = CreateItem(name);
            if (item == null) continue;
            if (IsUsableRoot(item) && _tree!.AppendRoot(item, flags, style, IntPtr.Zero) == NavTreeNative.S_OK) { _roots.Add(item); return; }
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>
    /// Parsing can "succeed" for things that aren't there: "shell:OneDrive" without OneDrive set up yields a
    /// nameless placeholder item. Require a named folder, and an existing directory for file-system folders.
    /// </summary>
    static bool IsUsableRoot(IShellItem item)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ShellNative.NameOf(item, SIGDN.NormalDisplay))) return false;
            if (item.GetAttributes(NavTreeNative.SFGAO_FOLDER, out uint attrs) < 0 || (attrs & NavTreeNative.SFGAO_FOLDER) == 0) return false;
            var fs = ShellNative.NameOf(item, SIGDN.FileSysPath);
            return string.IsNullOrEmpty(fs) || Directory.Exists(fs);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Only probe the Linux root when WSL has distributions: resolving \\wsl.localhost without WSL goes through every
    /// network provider and can stall the UI thread for seconds.
    /// </summary>
    static bool HasWslDistros()
    {
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            return lxss != null && lxss.GetSubKeyNames().Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Rebuilds the roots (e.g. after OneDrive or WSL was set up), keeping the highlighted folder.</summary>
    public void RefreshRoots()
    {
        if (_tree == null) return;
        var keep = _current;
        CancelPending();
        _suppress++;
        try
        {
            _tree.RemoveAllRoots();
            ReleaseRoots();
            _current = null;
            AddRoots();
        }
        catch (COMException) { }
        finally { _suppress--; }
        if (keep != null) SelectFolder(keep);
    }

    // ---- selection sync -----------------------------------------------------------------------

    /// <summary>
    /// Highlights the folder shown on the right, expanding its ancestors from the matching root down. Best effort:
    /// never throws, and never raises <see cref="FolderSelected"/>.
    /// </summary>
    public void SelectFolder(string parsingNameOrPath)
    {
        CancelPending();
        _raiseTimer.Stop();
        _raisePending = null;               // a programmatic sync overrides a user pick still waiting to be raised
        if (string.IsNullOrWhiteSpace(parsingNameOrPath)) return;
        if (_tree == null) { _deferredSelect = parsingNameOrPath; return; }
        try
        {
            var item = CreateItem(parsingNameOrPath);
            if (item == null) return;
            (_pendingChain, _pendingItem) = BuildChain(item);
            _pendingAttempts = 0;
            if (!TrySelectPending()) _pendingTimer.Start();
        }
        catch (Exception) { CancelPending(); }
    }

    void OnPendingTick()
    {
        try
        {
            if (_tree == null || _pendingItem == null || ++_pendingAttempts > 40) { CancelPending(); return; }
            if (TrySelectPending()) _pendingTimer.Stop();
        }
        catch (Exception) { CancelPending(); }
    }

    /// <summary>Selects the pending item, expanding whatever part of its ancestor chain is already in the tree.</summary>
    bool TrySelectPending()
    {
        var tree = _tree;
        var item = _pendingItem;
        if (tree == null || item == null) return true;
        _suppress++;
        try
        {
            if (TrySelect(tree, item)) { CancelPending(); return true; }
            if (_pendingChain == null || _pendingChain.Count == 0) { CancelPending(); return true; } // not under any root
            foreach (var ancestor in _pendingChain)
            {
                // Not in the tree yet: its parent is still enumerating (the tree fills folders asynchronously).
                if (tree.GetItemState(ancestor, NSTCITEMSTATE.EXPANDED, out var state) != NavTreeNative.S_OK) break;
                if ((state & NSTCITEMSTATE.EXPANDED) == 0) tree.SetItemState(ancestor, NSTCITEMSTATE.EXPANDED, NSTCITEMSTATE.EXPANDED);
            }
            if (TrySelect(tree, item)) { CancelPending(); return true; }
            return false;
        }
        catch (COMException) { return false; }
        finally { _suppress--; }
    }

    bool TrySelect(INameSpaceTreeControl tree, IShellItem item)
    {
        // Only succeed when the item really is in the tree (SetItemState on an unknown item fails).
        if (tree.GetItemState(item, NSTCITEMSTATE.SELECTED, out _) != NavTreeNative.S_OK) return false;
        if (tree.SetItemState(item, NSTCITEMSTATE.SELECTED, NSTCITEMSTATE.SELECTED) != NavTreeNative.S_OK) return false;
        // The first sync after start-up: begin from the top (Home, Gallery, pinned folders), as Explorer opens, and
        // scroll only as far as the folder needs.
        if (!_scrolledOnce && _hwndFocus != IntPtr.Zero)
        {
            _scrolledOnce = true;
            SendMessage(_hwndFocus, 0x115 /* WM_VSCROLL */, (IntPtr)6 /* SB_TOP */, IntPtr.Zero);
        }
        tree.EnsureItemVisible(item);
        // OnSelectionChanged arrives later (posted), so _suppress can't cover it: remember what the tree now
        // reports as selected and ignore the notification that matches it.
        _current = TreeSelectionName() ?? ParsingName(item) ?? _current;
        return true;
    }

    /// <summary>Parsing name of the tree's selected item, straight from the tree.</summary>
    string? TreeSelectionName()
    {
        if (_tree == null) return null;
        try
        {
            if (_tree.GetSelectedItems(out var items) != NavTreeNative.S_OK || items == null) return null;
            try
            {
                if (items.GetCount(out uint n) != NavTreeNative.S_OK || n == 0) return null;
                if (items.GetItemAt(0, out var item) != NavTreeNative.S_OK || item == null) return null;
                try { return ParsingName(item); }
                finally { Marshal.ReleaseComObject(item); }
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        catch (Exception) { return null; }
    }

    void CancelPending()
    {
        _pendingTimer.Stop();
        if (_pendingItem != null) { try { Marshal.ReleaseComObject(_pendingItem); } catch { } _pendingItem = null; }
        if (_pendingChain != null)
        {
            foreach (var a in _pendingChain) { try { Marshal.ReleaseComObject(a); } catch { } }
            _pendingChain = null;
        }
    }

    /// <summary>
    /// The ancestors to expand (nearest root first, down to the parent) and the item to select. The item may be
    /// replaced by an equivalent one rooted where the tree shows it (see <see cref="AncestorsViaPath"/>).
    /// </summary>
    (List<IShellItem> chain, IShellItem target) BuildChain(IShellItem item)
    {
        var chain = AncestorsViaParents(item);
        if (chain.Count > 0 || MatchRoot(item) != null) return (chain, item);
        string? fsPath = null;
        try { fsPath = ShellNative.NameOf(item, SIGDN.FileSysPath); } catch (Exception) { }
        if (string.IsNullOrEmpty(fsPath)) return (chain, item);
        var fsChain = AncestorsViaPath(fsPath, out var target);
        if (target == null) return (fsChain, item);
        Marshal.ReleaseComObject(item);
        return (fsChain, target);
    }

    /// <summary>Walks IShellItem.GetParent up to a root: root (inclusive) .. parent, or empty when no root is reached.</summary>
    List<IShellItem> AncestorsViaParents(IShellItem item)
    {
        var chain = new List<IShellItem>();
        IShellItem current = item;
        for (int depth = 0; depth < 64; depth++)
        {
            if (current.GetParent(out var parent) != NavTreeNative.S_OK || parent == null) break;
            chain.Add(parent);
            if (MatchRoot(parent) != null) { chain.Reverse(); return chain; }
            current = parent;
        }
        ReleaseAll(chain);
        return [];
    }

    /// <summary>
    /// Some file-system folders parse to a shortcut identity whose parents never reach a root (the user's profile
    /// folder parses to Desktop\{UsersFiles}). Rebuild the chain from the path instead: start at the deepest path
    /// prefix that is a root (OneDrive) — or at the drive/share and its parents (This PC, Network) — and create each
    /// child by relative name so every item is the one the tree actually contains.
    /// </summary>
    List<IShellItem> AncestorsViaPath(string path, out IShellItem? target)
    {
        target = null;
        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return [];
        if (!root.EndsWith('\\')) root += "\\";
        var parts = path[Math.Min(root.Length, path.Length)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var chain = new List<IShellItem>();
        int start = -1;
        for (int i = parts.Length; i >= 0 && start < 0; i--)
        {
            var prefix = i == 0 ? root : root + string.Join('\\', parts, 0, i);
            var it = CreateItem(prefix);
            if (it == null) continue;
            var r = MatchRoot(it);
            Marshal.ReleaseComObject(it);
            if (r != null && CloneItem(r) is { } clone) { chain.Add(clone); start = i; }
        }
        if (start < 0)
        {
            var top = CreateItem(root);
            if (top == null) return [];
            var up = AncestorsViaParents(top);
            if (up.Count == 0) { Marshal.ReleaseComObject(top); return []; }
            chain.AddRange(up);
            chain.Add(top);
            start = 0;
        }
        var iid = ShellNative.IID_IShellItem;
        for (int i = start; i < parts.Length; i++)
        {
            if (NavTreeNative.SHCreateItemFromRelativeName(chain[^1], parts[i], IntPtr.Zero, ref iid, out var child) != NavTreeNative.S_OK || child == null)
            {
                ReleaseAll(chain);
                return [];
            }
            chain.Add(child);
        }
        target = chain[^1];
        chain.RemoveAt(chain.Count - 1);
        return chain;
    }

    IShellItem? MatchRoot(IShellItem item)
    {
        foreach (var root in _roots)
            if (item.Compare(root, NavTreeNative.SICHINT_CANONICAL_FS, out int order) == NavTreeNative.S_OK && order == 0) return root;
        return null;
    }

    static IShellItem? CloneItem(IShellItem item)
    {
        if (ShellNative.SHGetIDListFromObject(item, out var pidl) != ShellNative.S_OK || pidl == IntPtr.Zero) return null;
        try
        {
            var iid = ShellNative.IID_IShellItem;
            return ShellNative.SHCreateItemFromIDList(pidl, ref iid, out var copy) == ShellNative.S_OK ? copy : null;
        }
        finally { ShellNative.ILFree(pidl); }
    }

    static void ReleaseAll(List<IShellItem> items)
    {
        foreach (var i in items) { try { Marshal.ReleaseComObject(i); } catch { } }
        items.Clear();
    }

    static IShellItem? CreateItem(string name)
    {
        name = Environment.ExpandEnvironmentVariables(name.Trim().Trim('"'));
        if (name.Length == 2 && name[1] == ':') name += "\\";
        var iid = ShellNative.IID_IShellItem;
        try
        {
            return ShellNative.SHCreateItemFromParsingName(name, IntPtr.Zero, ref iid, out var item) == ShellNative.S_OK ? item : null;
        }
        catch (Exception) { return null; }
    }

    static string? ParsingName(IShellItem item)
    {
        try { return ShellNative.NameOf(item, SIGDN.DesktopAbsoluteParsing); }
        catch (Exception) { return null; }
    }

    /// <summary>Test hook: the tree's own idea of the selection (parsing names).</summary>
    internal List<string> GetSelectedParsingNames()
    {
        var names = new List<string>();
        if (_tree == null || _tree.GetSelectedItems(out var items) != NavTreeNative.S_OK || items == null) return names;
        try
        {
            if (items.GetCount(out uint n) != NavTreeNative.S_OK) return names;
            for (uint i = 0; i < n; i++)
            {
                if (items.GetItemAt(i, out var it) != NavTreeNative.S_OK || it == null) continue;
                var s = ParsingName(it);
                if (s != null) names.Add(s);
                Marshal.ReleaseComObject(it);
            }
        }
        finally { Marshal.ReleaseComObject(items); }
        return names;
    }

    /// <summary>Test hook: NSTCITEMSTATE of the item (null when it is not in the tree).</summary>
    internal NSTCITEMSTATE? GetItemState(string parsingNameOrPath)
    {
        if (_tree == null) return null;
        var item = CreateItem(parsingNameOrPath);
        if (item == null) return null;
        try
        {
            return _tree.GetItemState(item, NSTCITEMSTATE.SELECTED | NSTCITEMSTATE.EXPANDED, out var s) == NavTreeNative.S_OK ? s : null;
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    // ---- tree notifications -------------------------------------------------------------------

    void OnTreeSelectionChanged(string? name)
    {
        _selectionChanges++;
        if (_suppress > 0) { if (name != null) _current = name; return; }
        if (name == null) return;
        // Our own (posted) notification — possibly for an earlier SelectFolder while a later one is still pending.
        if (string.Equals(name, _current, StringComparison.OrdinalIgnoreCase)) return;
        _current = name;
        // Only a change right after input in the tree is the user picking a folder. Without input it is the tree
        // itself: e.g. a TreeView that gains focus with nothing selected selects its first root (posted, too).
        long now = Environment.TickCount64;
        if (now - _lastInputTicks > UserInputWindowMs) return;
        CancelPending(); // the user moved on; drop a sync still waiting for enumeration
        _raisePending = name;
        _raiseTimer.Stop();
        // Arrowing through the tree should not navigate on every keystroke (Explorer waits too).
        if (_lastInputWasKey) _raiseTimer.Start();
        else if (IsHandleCreated) BeginInvoke(RaisePending); // never call out from inside the tree's notification
    }

    const int UserInputWindowMs = 1500;

    void RaisePending()
    {
        var name = _raisePending;
        _raisePending = null;
        if (name == null || IsDisposed) return;
        _lastRaiseTicks = Environment.TickCount64;
        FolderSelected?.Invoke(name);
    }

    void OnTreeItemClick(string? name, NSTCEHITTEST hit, NSTCECLICKTYPE click)
    {
        if ((click & NSTCECLICKTYPE.BUTTON) != NSTCECLICKTYPE.LBUTTON || !IsHandleCreated) return;
        int changes = _selectionChanges;
        BeginInvoke(() =>
        {
            if (IsDisposed) return;
            SyncActiveControl();
            // Clicking the folder that is already highlighted navigates there again (as in Explorer), e.g. after the
            // view went somewhere the tree could not follow. A click that changed the selection was already raised.
            if (name == null || (click & NSTCECLICKTYPE.DBLCLICK) != 0) return;
            if ((hit & NSTCEHITTEST.ONITEM) == 0 || (hit & NSTCEHITTEST.ONITEMBUTTON) != 0) return;
            if (changes != _selectionChanges || _raisePending != null) return;
            if (Environment.TickCount64 - _lastRaiseTicks < 500) return;
            if (!string.Equals(name, _current, StringComparison.OrdinalIgnoreCase)) return;
            _lastRaiseTicks = Environment.TickCount64;
            FolderSelected?.Invoke(name);
        });
    }

    /// <summary>Focus moved into the native tree behind WinForms' back: make the container's ActiveControl agree.</summary>
    void SyncActiveControl()
    {
        try
        {
            if (GetContainerControl() is ContainerControl cc && cc.ActiveControl != this && CanSelect) cc.ActiveControl = this;
        }
        catch (Exception) { }
    }

    // ---- keyboard -----------------------------------------------------------------------------

    bool FocusInTree(out bool inLabelEdit)
    {
        inLabelEdit = false;
        if (_hwndTree == IntPtr.Zero) return false;
        var f = NavTreeNative.GetFocus();
        if (f == IntPtr.Zero || (f != _hwndTree && !NavTreeNative.IsChild(_hwndTree, f))) return false;
        var sb = new System.Text.StringBuilder(32);
        NavTreeNative.GetClassName(f, sb, sb.Capacity);
        inLabelEdit = sb.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>Keys the native tree handles itself (navigation, expand/collapse, F2 rename, Delete, context menu).</summary>
    static bool IsTreeKey(Keys keyData)
    {
        if ((keyData & Keys.Alt) != 0) return false;
        bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0;
        return (keyData & Keys.KeyCode) switch
        {
            Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown => true,
            Keys.Enter or Keys.F2 or Keys.Delete or Keys.Back or Keys.Add or Keys.Subtract or Keys.Multiply or Keys.Space or Keys.Apps => !ctrl,
            Keys.F10 => shift && !ctrl,
            _ => false,
        };
    }

    static bool IsLabelEditKey(Keys keyData) => (keyData & Keys.Alt) == 0 && (keyData & Keys.KeyCode) != Keys.Tab;

    /// <summary>While the tree has focus its keys go to it, not to the form's menu shortcuts (F2, Delete, Ctrl+C in rename...).</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (FocusInTree(out bool inEdit) && (inEdit ? IsLabelEditKey(keyData) : IsTreeKey(keyData))) return false;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        if (FocusInTree(out bool inEdit) && (inEdit ? IsLabelEditKey(keyData) : IsTreeKey(keyData))) return true;
        return base.IsInputKey(keyData);
    }

    protected override bool IsInputChar(char charCode)
        => FocusInTree(out _) || base.IsInputChar(charCode); // type-ahead and typing in the rename box, not mnemonics

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);

    // ---- COM callbacks (separate object so the Control itself is never exposed to COM) --------

    // ---- Explorer's metrics and group dividers ---------------------------------------------------

    /// <summary>
    /// Explorer's row height: 32 (40px at 125%) — or the control's own font-based height in compact mode — and
    /// its 8px indent and extended styles. The control recomputes the height on DPI and setting changes, so a hook
    /// on the tree window rewrites those requests (ExplorerFrame: height = max(scaled base, font height), even).
    /// </summary>
    void ApplyExplorerMetrics()
    {
        if (_hwndFocus == IntPtr.Zero) return;
        _hook ??= new TreeHook(this);
        if (_hook.Handle != _hwndFocus) { _hook.ReleaseHandle(); _hook.AssignHandle(_hwndFocus); }
        SendMessage(_hwndFocus, TVM_SETEXTENDEDSTYLE, (IntPtr)ExplorerExtendedStyle, (IntPtr)ExplorerExtendedStyle);
        SendMessage(_hwndFocus, TVM_SETINDENT, (IntPtr)LogicalToDeviceUnits(8), IntPtr.Zero);
        int h = RowHeight();
        if (h > 0) SendMessage(_hwndFocus, TVM_SETITEMHEIGHT, (IntPtr)h, IntPtr.Zero);
        if (ExplorerTheme.Dark)
        {
            SendMessage(_hwndFocus, TVM_SETBKCOLOR, IntPtr.Zero, (IntPtr)ColorTranslator.ToWin32(ExplorerTheme.Content));
            SendMessage(_hwndFocus, TVM_SETTEXTCOLOR, IntPtr.Zero, (IntPtr)ColorTranslator.ToWin32(Color.White));
        }
    }

    /// <summary>Explorer's row height (even), or 0 in compact mode to keep the control's own.</summary>
    int RowHeight()
    {
        try
        {
            using var adv = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            if (adv?.GetValue("UseCompactMode") is int compact && compact != 0) return 0;
        }
        catch { }
        int h = LogicalToDeviceUnits(32);
        return h + (h & 1);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyExplorerMetrics();
        QueueGroupLayout();
    }

    void QueueGroupLayout()
    {
        if (_groupLayoutQueued || !IsHandleCreated) return;
        _groupLayoutQueued = true;
        BeginInvoke(() => { _groupLayoutQueued = false; ApplyGroupLayout(); });
    }

    /// <summary>
    /// Explorer's dividers: the first row of each group (Home/Gallery | pinned folders | This PC/Network) is double
    /// height with its content in the lower half (ExplorerFrame sets iIntegral 2 and the content offset), and a line
    /// is drawn at a quarter of that row's height.
    /// </summary>
    void ApplyGroupLayout()
    {
        if (_hwndFocus == IntPtr.Zero || _tree == null) return;
        int rowHeight = (int)SendMessage(_hwndFocus, TVM_GETITEMHEIGHT, IntPtr.Zero, IntPtr.Zero);
        var first = new HashSet<IntPtr>();
        var names = new HashSet<(string, int)>();
        var text = Marshal.AllocHGlobal(522);
        try
        {
            // The visible top-level rows: a hidden root (Quick access) is a "flat" row (TVIS_EX_FLAT) whose children
            // are shown in its place, one level down.
            var rows = new List<(IntPtr Item, string Name, int Level)>();
            for (IntPtr item = SendMessage(_hwndFocus, TVM_GETNEXTITEM, IntPtr.Zero /* TVGN_ROOT */, IntPtr.Zero); item != IntPtr.Zero;
                 item = SendMessage(_hwndFocus, TVM_GETNEXTITEM, (IntPtr)1 /* TVGN_NEXT */, item))
            {
                var (name, flat) = ReadRow(item, text);
                if (!flat) { rows.Add((item, name, 0)); continue; }
                for (IntPtr child = SendMessage(_hwndFocus, TVM_GETNEXTITEM, (IntPtr)4 /* TVGN_CHILD */, item); child != IntPtr.Zero;
                     child = SendMessage(_hwndFocus, TVM_GETNEXTITEM, (IntPtr)1 /* TVGN_NEXT */, child))
                    rows.Add((child, ReadRow(child, text).Name, 1));
            }
            int prevGroup = -1;
            foreach (var (item, name, level) in rows)
            {
                int group = level == 0 && Array.IndexOf(_group0, name) >= 0 ? 0 : level == 0 && Array.IndexOf(_group2, name) >= 0 ? 2 : 1;
                if (prevGroup >= 0 && group != prevGroup) { first.Add(item); names.Add((name, level)); }
                prevGroup = group;
            }
        }
        finally { Marshal.FreeHGlobal(text); }

        bool changed = !_groupFirst.SetEquals(names) || !first.SetEquals(_tallRows);
        foreach (var item in _tallRows.Where(i => !first.Contains(i)).ToList())
        {
            var reset = new TVITEMEX { mask = TVIF_HANDLE | TVIF_INTEGRAL | TVIF_CONTENTOFFSET, hItem = item, iIntegral = 1, iReserved = 0 };
            SendMessage(_hwndFocus, TVM_SETITEMW, IntPtr.Zero, ref reset);
            _tallRows.Remove(item);
        }
        foreach (var item in first)
        {
            var tall = new TVITEMEX { mask = TVIF_HANDLE | TVIF_INTEGRAL | TVIF_CONTENTOFFSET, hItem = item, iIntegral = 2, iReserved = rowHeight };
            SendMessage(_hwndFocus, TVM_SETITEMW, IntPtr.Zero, ref tall);
            _tallRows.Add(item);
        }
        _groupFirst.Clear();
        _groupFirst.UnionWith(names);
        if (changed) InvalidateRect(_hwndFocus, IntPtr.Zero, true);
    }

    (string Name, bool Flat) ReadRow(IntPtr item, IntPtr text)
    {
        Marshal.WriteInt16(text, 0);
        var get = new TVITEMEX { mask = TVIF_TEXT | TVIF_HANDLE | 0x100 /* TVIF_STATEEX */, hItem = item, pszText = text, cchTextMax = 260 };
        SendMessage(_hwndFocus, TVM_GETITEMW, IntPtr.Zero, ref get);
        return (Marshal.PtrToStringUni(text) ?? "", (get.uStateEx & 0x1 /* TVIS_EX_FLAT */) != 0);
    }

    bool IsGroupFirst(ref NSTCCUSTOMDRAW cd)
    {
        if (_groupFirst.Count == 0 || cd.iLevel > 1 || cd.pszText == IntPtr.Zero) return false; // level 1: children of a flat row
        string name = Marshal.PtrToStringUni(cd.pszText) ?? "";
        foreach (var g in _groupFirst) if (g.Name == name) return true;
        return false;
    }

    /// <summary>ExplorerFrame's divider: 1px (scaled) line, 8px in from both sides, a quarter of the way down the row.</summary>
    void DrawDivider(IntPtr hdc, RECT row)
    {
        int width = Math.Max(1, LogicalToDeviceUnits(1));
        if (_dividerPen == IntPtr.Zero || _dividerPenWidth != width)
        {
            if (_dividerPen != IntPtr.Zero) NavTreeNative.DeleteObject(_dividerPen);
            _dividerPen = CreatePen(0, width, ColorTranslator.ToWin32(ExplorerTheme.NavDivider));
            _dividerPenWidth = width;
        }
        int inset = LogicalToDeviceUnits(8);
        // A quarter of the double-height row. Custom draw may hand over only the content half (the lower one), whose
        // top is one row below the row's top: the line is then half a row above it.
        int unit = (int)SendMessage(_hwndFocus, TVM_GETITEMHEIGHT, IntPtr.Zero, IntPtr.Zero);
        int h = row.Bottom - row.Top;
        int y = unit > 0 && h < 2 * unit - 1 ? row.Top - unit / 2 : row.Top + h / 4;
        var old = SelectObject(hdc, _dividerPen);
        MoveToEx(hdc, row.Left + inset, y, IntPtr.Zero);
        LineTo(hdc, row.Right - inset, y);
        SelectObject(hdc, old);
    }

    /// <summary>
    /// Keeps Explorer's layout while the control works: its row height when it recalculates (DPI or font changes),
    /// the double height of group-first rows when it updates a row's height (pinned folders load asynchronously),
    /// and a fresh group layout after every insertion.
    /// </summary>
    sealed class TreeHook(NavigationTree host) : NativeWindow
    {
        /// <summary>A message the tree gets as if the hook weren't there (a pinned folder's drag handed back to it).</summary>
        public void Pass(ref Message m) => DefWndProc(ref m);

        const int TVM_INSERTITEMW = 0x1132;

        protected override void WndProc(ref Message m)
        {
            if (host.HandlePinDragMessage(ref m)) return;
            if (m.Msg == TVM_SETITEMHEIGHT && host.RowHeight() is > 0 and var h && m.WParam != (IntPtr)h)
            {
                m.WParam = (IntPtr)h;
                host.QueueGroupLayout();
            }
            else if (m.Msg == TVM_SETITEMW && m.LParam != IntPtr.Zero && host._tallRows.Count > 0)
            {
                // TVITEMEX (x64): mask @0, hItem @8, iIntegral @56, iReserved @76. The caller is in this process.
                int mask = Marshal.ReadInt32(m.LParam);
                if ((mask & TVIF_INTEGRAL) != 0 && (mask & TVIF_HANDLE) != 0 && host._tallRows.Contains(Marshal.ReadIntPtr(m.LParam, 8)))
                {
                    Marshal.WriteInt32(m.LParam, mask | TVIF_CONTENTOFFSET);
                    Marshal.WriteInt32(m.LParam, 56, 2);
                    Marshal.WriteInt32(m.LParam, 76, (int)SendMessage(Handle, TVM_GETITEMHEIGHT, IntPtr.Zero, IntPtr.Zero));
                }
            }
            base.WndProc(ref m);
            if (m.Msg == TVM_INSERTITEMW) host.QueueGroupLayout();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TVITEMEX
    {
        public int mask;
        public IntPtr hItem;
        public uint state, stateMask;
        public IntPtr pszText;
        public int cchTextMax, iImage, iSelectedImage, cChildren;
        public IntPtr lParam;
        public int iIntegral;
        public uint uStateEx;
        public IntPtr hwnd;
        public int iExpandedImage, iReserved;
    }

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref TVITEMEX lParam);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);
    [DllImport("gdi32.dll")] static extern IntPtr CreatePen(int style, int width, int color);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")] static extern bool LineTo(IntPtr hdc, int x, int y);

    [ComVisible(true)]
    sealed class Callbacks(NavigationTree host) : INameSpaceTreeControlEvents, INameSpaceTreeControlCustomDraw
    {
        // ---- INameSpaceTreeControlCustomDraw: only used to draw Explorer's group dividers ----

        public int PrePaint(IntPtr hdc, ref RECT prc, out IntPtr plres)
        {
            plres = host._groupFirst.Count > 0 ? NavTreeNative.CDRF_NOTIFYITEMDRAW : NavTreeNative.CDRF_DODEFAULT;
            return NavTreeNative.S_OK;
        }

        public int PostPaint(IntPtr hdc, ref RECT prc) => NavTreeNative.S_OK;

        public int ItemPrePaint(IntPtr hdc, ref RECT prc, ref NSTCCUSTOMDRAW pnstccdItem, ref uint pclrText, ref uint pclrTextBk, out IntPtr plres)
        {
            plres = host.IsGroupFirst(ref pnstccdItem) ? NavTreeNative.CDRF_NOTIFYPOSTPAINT : NavTreeNative.CDRF_DODEFAULT;
            return NavTreeNative.S_OK;
        }

        public int ItemPostPaint(IntPtr hdc, ref RECT prc, ref NSTCCUSTOMDRAW pnstccdItem)
        {
            try { if (host.IsGroupFirst(ref pnstccdItem)) host.DrawDivider(hdc, prc); } catch (Exception) { }
            return NavTreeNative.S_OK;
        }

        // ---- INameSpaceTreeControlEvents ----

        static string? NameOfItem(IntPtr psi)
        {
            if (psi == IntPtr.Zero) return null;
            try
            {
                var item = (IShellItem)Marshal.GetObjectForIUnknown(psi);
                try { return ParsingName(item); }
                finally { Marshal.ReleaseComObject(item); }
            }
            catch (Exception) { return null; }
        }

        // S_FALSE / E_NOTIMPL = "not handled": the tree does its default (select, expand, menus, rename...).
        public int OnItemClick(IntPtr psi, NSTCEHITTEST nstceHitTest, NSTCECLICKTYPE nstceClickType)
        {
            host._lastInputTicks = Environment.TickCount64;
            host._lastInputWasKey = false;
            try { host.OnTreeItemClick(NameOfItem(psi), nstceHitTest, nstceClickType); } catch (Exception) { }
            return NavTreeNative.S_FALSE;
        }

        public int OnPropertyItemCommit(IntPtr psi) => NavTreeNative.S_FALSE;
        public int OnItemStateChanging(IntPtr psi, NSTCITEMSTATE nstcisMask, NSTCITEMSTATE nstcisState) => NavTreeNative.S_OK; // allow
        public int OnItemStateChanged(IntPtr psi, NSTCITEMSTATE nstcisMask, NSTCITEMSTATE nstcisState) => NavTreeNative.S_OK;

        public int OnSelectionChanged(IntPtr psiaSelection)
        {
            try
            {
                string? name = null;
                if (psiaSelection != IntPtr.Zero)
                {
                    var items = (IShellItemArray)Marshal.GetObjectForIUnknown(psiaSelection);
                    try
                    {
                        if (items.GetCount(out uint n) == NavTreeNative.S_OK && n > 0 && items.GetItemAt(0, out var item) == NavTreeNative.S_OK && item != null)
                        {
                            name = ParsingName(item);
                            Marshal.ReleaseComObject(item);
                        }
                    }
                    finally { Marshal.ReleaseComObject(items); }
                }
                host.OnTreeSelectionChanged(name);
            }
            catch (Exception) { }
            return NavTreeNative.S_OK;
        }

        public int OnKeyboardInput(uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            host._lastInputTicks = Environment.TickCount64;
            host._lastInputWasKey = true;
            return NavTreeNative.S_FALSE; // let the tree process the key
        }

        public int OnBeforeExpand(IntPtr psi) => NavTreeNative.S_OK;          // allow
        public int OnAfterExpand(IntPtr psi) => NavTreeNative.S_OK;
        public int OnBeginLabelEdit(IntPtr psi) => NavTreeNative.S_OK;        // allow rename
        public int OnEndLabelEdit(IntPtr psi) => NavTreeNative.S_OK;
        public int OnGetToolTip(IntPtr psi, IntPtr pszTip, int cchTip) => NavTreeNative.E_NOTIMPL; // default infotip
        public int OnBeforeItemDelete(IntPtr psi) => NavTreeNative.E_NOTIMPL;
        // Pinned folders arrive (and change) asynchronously: re-run the group layout once things settle.
        public int OnItemAdded(IntPtr psi, int fIsRoot) { host.QueueGroupLayout(); return NavTreeNative.E_NOTIMPL; }
        public int OnItemDeleted(IntPtr psi, int fIsRoot) { host.QueueGroupLayout(); return NavTreeNative.E_NOTIMPL; }

        public int OnBeforeContextMenu(IntPtr psi, ref Guid riid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            return NavTreeNative.E_NOTIMPL;   // the shell builds its own context menu
        }

        public int OnAfterContextMenu(IntPtr psi, IntPtr pcmIn, ref Guid riid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            return NavTreeNative.E_NOTIMPL;   // keep pcmIn unchanged
        }

        public int OnBeforeStateImageChange(IntPtr psi) => NavTreeNative.S_OK;

        public int OnGetDefaultIconIndex(IntPtr psi, out int piDefaultIcon, out int piOpenIcon)
        {
            piDefaultIcon = piOpenIcon = 0;
            return NavTreeNative.E_NOTIMPL;   // the shell's own icons
        }
    }
}

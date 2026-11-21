using System.Diagnostics;
using System.Runtime.InteropServices;
using Anything.Core;
using Anything.Interop;

namespace Anything.Explorer;

/// <summary>
/// What the command bar and status bar ask of the view: its layout and sort for the View and Sort menus, its columns,
/// its own New folder and Choose details commands, and what the selected files add up to.
/// </summary>
internal sealed partial class ExplorerHost
{
    // ---- view and sort ------------------------------------------------------------------------

    /// <summary>The view's FOLDERVIEWMODE and icon size, or null when there is no view.</summary>
    public (int Mode, int IconSize)? GetViewModeAndIconSize()
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return null;
        try { return fv.GetViewModeAndIconSize(out int mode, out int size) == ShellNative.S_OK ? (mode, size) : null; }
        catch (COMException) { return null; }
        finally { Marshal.ReleaseComObject(fv); }
    }

    /// <summary>The column the view sorts on first, and which way, or null when it can't say.</summary>
    public (PROPERTYKEY Key, bool Descending)? GetSortColumn()
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return null;
        IntPtr buf = Marshal.AllocCoTaskMem(24); // SORTCOLUMN { PROPERTYKEY key; int direction }
        try
        {
            if (fv.GetSortColumns(buf, 1) != ShellNative.S_OK) return null;
            return (Marshal.PtrToStructure<PROPERTYKEY>(buf), Marshal.ReadInt32(buf, 20) < 0);
        }
        catch (COMException) { return null; }
        finally { Marshal.FreeCoTaskMem(buf); Marshal.ReleaseComObject(fv); }
    }

    /// <summary>The key the fixed Sort menu (Name, Date modified, Type, Size) sorts on.</summary>
    public static PROPERTYKEY SortKeyOf(ExplorerSortColumn column) => new(FMTID_Storage, column switch
    {
        ExplorerSortColumn.DateModified => 14u,
        ExplorerSortColumn.Type => 4u,
        ExplorerSortColumn.Size => 12u,
        _ => 10u, // System.ItemNameDisplay
    });

    /// <summary>
    /// The columns the folder shows, in their order, with the names its headers show (at most <paramref name="max"/>):
    /// what the Sort menu lists, so Recycle Bin, This PC or a music folder each get their own. Empty when the view can't say.
    /// </summary>
    public List<(PROPERTYKEY Key, string Name)> GetVisibleColumns(int max)
    {
        var columns = new List<(PROPERTYKEY, string)>();
        var view = GetView<IShellView>(ShellNative.IID_IShellView);
        if (view == null) return columns;
        try
        {
            if (view is not IColumnManager cm || cm.GetColumnCount(CommandsNative.CM_ENUM_VISIBLE, out uint count) != ShellNative.S_OK || count == 0)
                return columns;
            var keys = new PROPERTYKEY[count];
            if (cm.GetColumns(CommandsNative.CM_ENUM_VISIBLE, keys, count) != ShellNative.S_OK) return columns;
            foreach (var k in keys)
            {
                if (columns.Count >= max) break;
                var key = k;
                var info = new CM_COLUMNINFO { cbSize = Marshal.SizeOf<CM_COLUMNINFO>(), dwMask = CommandsNative.CM_MASK_NAME, wszName = "" };
                if (cm.GetColumnInfo(ref key, ref info) == ShellNative.S_OK && !string.IsNullOrWhiteSpace(info.wszName))
                    columns.Add((key, info.wszName));
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { columns.Clear(); }
        finally { Marshal.ReleaseComObject(view); }
        return columns;
    }

    /// <summary>
    /// Sorts on a column the way File Explorer's Sort menu does: a new column in its own default direction (newest or
    /// largest first for dates and sizes), the column already sorted on turned round (<see cref="CommandBarRules.SortDescending"/>).
    /// </summary>
    public void SortByColumn(PROPERTYKEY key)
    {
        var current = GetSortColumn();
        bool same = current is { } c && c.Key.fmtid == key.fmtid && c.Key.pid == key.pid;
        bool descending = CommandBarRules.SortDescending(same, current is { Descending: true }, SortsDescendingFirst(key));
        SetSort(key.fmtid, key.pid, descending ? -1 : 1);
    }

    /// <summary>Whether a column sorts descending when first chosen (its property's PDVF_SORTDESCENDING), as Explorer starts it.</summary>
    static bool SortsDescendingFirst(PROPERTYKEY key)
    {
        var iid = CommandsNative.IID_IPropertyDescription;
        IntPtr ptr = IntPtr.Zero;
        object? obj = null;
        try
        {
            if (CommandsNative.PSGetPropertyDescription(ref key, ref iid, out ptr) != ShellNative.S_OK || ptr == IntPtr.Zero) return false;
            obj = Marshal.GetObjectForIUnknown(ptr);
            return obj is IPropertyDescription pd && pd.GetViewFlags(out uint flags) == ShellNative.S_OK && (flags & CommandsNative.PDVF_SORTDESCENDING) != 0;
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return false; }
        finally
        {
            if (obj != null) Marshal.ReleaseComObject(obj);
            if (ptr != IntPtr.Zero) Marshal.Release(ptr);
        }
    }

    /// <summary>Sort › More...: the view's own Choose details dialog (which columns the folder shows).</summary>
    public bool ChooseDetails() => InvokeBackgroundCommand("viewcolsettings", byMenuItem: true);

    // ---- new folder ---------------------------------------------------------------------------

    /// <summary>
    /// The view's own New folder (what Ctrl+Shift+N does while it has the focus): Windows' name for it in the user's
    /// language, renaming started, undone by Ctrl+Z, and in a library too. False when the view couldn't;
    /// <paramref name="refused"/> is then the HRESULT when the view ran it and refused or cancelled it (ERROR_CANCELLED or
    /// E_ACCESSDENIED: its own prompt was shown and answered, so trying another way would only ask again), else 0.
    /// </summary>
    bool NewFolderInView(out int refused)
    {
        FocusView(); // the rename box closes if the view takes the focus after it opens
        bool done = InvokeBackgroundCommand("NewFolder", byMenuItem: false, out int hr); // CMDSTR_NEWFOLDER
        // The view answered (the user cancelled, or the folder may not be written to): no other way is tried.
        refused = !done && hr is HResultCancelled or HResultAccessDenied ? hr : 0;
        return done;
    }

    const int HResultCancelled = unchecked((int)0x800704C7), // HRESULT_FROM_WIN32(ERROR_CANCELLED)
              HResultAccessDenied = unchecked((int)0x80070005); // E_ACCESSDENIED

    /// <summary>
    /// A new folder made by Windows' file operation, so Ctrl+Z undoes it (FOFX_ADDUNDORECORD). False when it couldn't;
    /// the caller then makes it itself.
    /// </summary>
    bool NewFolderWithUndo(string folder, string name)
    {
        IShellItem? dest = null;
        object? op = null;
        try
        {
            var iid = ShellNative.IID_IShellItem;
            if (ShellNative.SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out dest) != ShellNative.S_OK || dest == null) return false;
            if (Type.GetTypeFromCLSID(CommandsNative.CLSID_FileOperation, throwOnError: false) is not { } type) return false;
            op = Activator.CreateInstance(type);
            if (op is not IFileOperation fo) return false;
            fo.SetOwnerWindow(FindForm()?.Handle ?? Handle);
            fo.SetOperationFlags(CommandsNative.FOF_SILENT | CommandsNative.FOF_NOERRORUI | CommandsNative.FOF_NOCONFIRMMKDIR | CommandsNative.FOFX_ADDUNDORECORD);
            if (fo.NewItem(dest, CommandsNative.FILE_ATTRIBUTE_DIRECTORY, name, null, IntPtr.Zero) != ShellNative.S_OK) return false;
            return fo.PerformOperations() == ShellNative.S_OK && fo.GetAnyOperationsAborted(out bool aborted) == ShellNative.S_OK && !aborted;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException or MissingMethodException) { return false; }
        finally
        {
            if (op != null) Marshal.ReleaseComObject(op);
            if (dest != null) Marshal.ReleaseComObject(dest);
        }
    }

    // ---- the view's background menu ------------------------------------------------------------

    /// <summary>
    /// Runs a command of the view's background menu (what right-clicking empty space shows), as the view itself would.
    /// DefView takes some by name ("NewFolder"); others only by their item's id (<paramref name="byMenuItem"/>: the item
    /// whose verb this is, found after the submenus are filled, as Choose details' "viewcolsettings" in Group by).
    /// </summary>
    bool InvokeBackgroundCommand(string verb, bool byMenuItem) => InvokeBackgroundCommand(verb, byMenuItem, out _);

    /// <param name="invokeResult">What the command itself returned, or 0 when it wasn't reached (no menu, no such verb).</param>
    bool InvokeBackgroundCommand(string verb, bool byMenuItem, out int invokeResult)
    {
        invokeResult = 0;
        var view = GetView<IShellView>(ShellNative.IID_IShellView);
        if (view == null) return false;
        IntPtr ptr = IntPtr.Zero, hmenu = IntPtr.Zero, verbA = IntPtr.Zero, verbW = IntPtr.Zero;
        object? obj = null;
        try
        {
            var iid = CommandsNative.IID_IContextMenu;
            if (view.GetItemObject(CommandsNative.SVGIO_BACKGROUND, ref iid, out ptr) != ShellNative.S_OK || ptr == IntPtr.Zero) return false;
            obj = Marshal.GetObjectForIUnknown(ptr);
            if (obj is not IContextMenu menu) return false;
            hmenu = Native.CreatePopupMenu();
            if (menu.QueryContextMenu(hmenu, 0, FirstMenuId, LastMenuId, Native.CMF_NORMAL) < 0) return false;
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                hwnd = FindForm()?.Handle ?? Handle,
                nShow = 1, // SW_SHOWNORMAL
            };
            if (byMenuItem)
            {
                if (FindMenuItem(menu, obj as IContextMenu2, hmenu, verb) is not { } id) return false;
                info.lpVerb = (IntPtr)(id - FirstMenuId);
            }
            else
            {
                verbA = Marshal.StringToHGlobalAnsi(verb);
                verbW = Marshal.StringToHGlobalUni(verb);
                info.fMask = Native.CMIC_MASK_UNICODE;
                info.lpVerb = verbA;
                info.lpVerbW = verbW;
            }
            invokeResult = menu.InvokeCommand(ref info);
            return invokeResult >= 0;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException) { return false; }
        finally
        {
            if (verbA != IntPtr.Zero) Marshal.FreeHGlobal(verbA);
            if (verbW != IntPtr.Zero) Marshal.FreeHGlobal(verbW);
            if (hmenu != IntPtr.Zero) Native.DestroyMenu(hmenu);
            if (obj != null) Marshal.ReleaseComObject(obj);
            if (ptr != IntPtr.Zero) Marshal.Release(ptr);
            Marshal.ReleaseComObject(view);
        }
    }

    const uint FirstMenuId = 1, LastMenuId = 0x7FFF;

    /// <summary>The id of the menu item (in <paramref name="hmenu"/> or its submenus) whose canonical verb is <paramref name="verb"/>.</summary>
    static uint? FindMenuItem(IContextMenu menu, IContextMenu2? menu2, IntPtr hmenu, string verb)
    {
        int n = CommandsNative.GetMenuItemCount(hmenu);
        for (int i = 0; i < n; i++)
        {
            IntPtr sub = CommandsNative.GetSubMenu(hmenu, i);
            if (sub != IntPtr.Zero)
            {
                // View, Sort by and Group by fill as they open.
                if (CommandsNative.GetMenuItemCount(sub) == 0) menu2?.HandleMenuMsg(Native.WM_INITMENUPOPUP, sub, i);
                if (FindMenuItem(menu, menu2, sub, verb) is { } found) return found;
                continue;
            }
            uint id = CommandsNative.GetMenuItemID(hmenu, i);
            if (id < FirstMenuId || id > LastMenuId) continue;
            if (string.Equals(MenuVerb(menu, id - FirstMenuId), verb, StringComparison.OrdinalIgnoreCase)) return id;
        }
        return null;
    }

    static string? MenuVerb(IContextMenu menu, uint offset)
    {
        IntPtr buf = Marshal.AllocHGlobal(512 * 2);
        try
        {
            Marshal.WriteInt16(buf, 0);
            return menu.GetCommandString((UIntPtr)offset, Native.GCS_VERBW, IntPtr.Zero, buf, 512) == ShellNative.S_OK ? Marshal.PtrToStringUni(buf) : null;
        }
        catch (Exception e) when (e is COMException or ArgumentException) { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---- what the selection adds up to ---------------------------------------------------------

    int _selectionVersion;                                          // bumped by each change the view reports
    (int Version, int Count, string? First, byte[]? Location)? _sizeFor; // the selection _size was added up for
    long? _size;
    readonly SelectionSizeMemory<byte[]> _sizeSlow = new();         // the folders where adding up took too long

    /// <summary>
    /// What the selected files add up to, for the status bar ("5 items selected  12.3 MB"), or null when it shows the
    /// count only: a folder is selected (Explorer shows no size then), nothing has a size, or the selection is too big
    /// or too slow to add up. Read from the sizes the view already holds, never the disk, so a cloud-only file is not
    /// downloaded; worked out once per selection, as the status bar asks for it often.
    /// </summary>
    public long? SelectedFilesSize()
    {
        if (_sizeFor is { } k && k.Version == _selectionVersion && k.Count == _lastSelectionCount
            && string.Equals(k.First, _lastSelection, StringComparison.Ordinal) && ReferenceEquals(k.Location, _current))
            return _size;
        _size = AddUpSelection();
        _sizeFor = (_selectionVersion, _lastSelectionCount, _lastSelection, _current);
        return _size;
    }

    /// <summary>Longest the status bar waits for a selection's sizes on the UI thread.</summary>
    const int SizeBudgetMs = 100;

    long? AddUpSelection()
    {
        int count = SelectedCount;
        if (count == 0 || count > CommandBarRules.SelectionSizeLimit) return null;
        if (_sizeSlow.IsSlow(_current, count)) return null; // it was too slow here before
        var watch = Stopwatch.StartNew();
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return null;
        try
        {
            if (fv.GetSelection(false, out var items) != ShellNative.S_OK || items == null) return null;
            try
            {
                if (items.GetCount(out uint n) != ShellNative.S_OK || n == 0 || n > CommandBarRules.SelectionSizeLimit) return null;
                var total = new CommandBarRules.SelectionTotal();
                bool over = watch.ElapsedMilliseconds > SizeBudgetMs; // before the first item: the view itself was slow
                string? slowItem = null;                              // the item whose reading ran past the time
                for (uint i = 0; i < n; i++)
                {
                    if (over)
                    {
                        // Remembered for the folder when many items were too slow, or it has run out here at two
                        // different items (a phone, FTP, a cloud folder): one slow item (a handler loading the first time,
                        // a file slow every time it is read) hides the sizes of selections holding it, not every later one.
                        _sizeSlow.RanOut(_current, (int)n, (int)i, slowItem);
                        return null;
                    }
                    if (items.GetItemAt(i, out var item) != ShellNative.S_OK || item == null) return null;
                    try
                    {
                        // The attributes the item holds: a zip is a folder and a stream, and counts as a file.
                        if (item.GetAttributes(ShellSelection.SfgaoFolder | ShellSelection.SfgaoStream, out uint attrs) < 0) return null;
                        bool folder = (attrs & ShellSelection.SfgaoFolder) != 0 && (attrs & ShellSelection.SfgaoStream) == 0;
                        total.Add(folder, folder ? null : FastSize(item));
                        if (total.HasFolder) return null;
                        if (i + 1 < n && watch.ElapsedMilliseconds > SizeBudgetMs)
                            (over, slowItem) = (true, ShellNative.NameOf(item, SIGDN.ParentRelativeParsing));
                    }
                    finally { Marshal.ReleaseComObject(item); }
                }
                return total.Bytes;
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return null; }
        finally { Marshal.ReleaseComObject(fv); }
    }

    /// <summary>An item's System.Size from its fast properties: what its folder already knows (GPS_FASTPROPERTIESONLY opens no file).</summary>
    static long? FastSize(IShellItem item)
    {
        if (item is not IShellItem2 item2) return null; // the same object: released with the item
        var iid = CommandsNative.IID_IPropertyStore;
        if (item2.GetPropertyStore(CommandsNative.GPS_FASTPROPERTIESONLY, ref iid, out IntPtr ptr) != ShellNative.S_OK || ptr == IntPtr.Zero) return null;
        object? obj = null;
        try
        {
            obj = Marshal.GetObjectForIUnknown(ptr);
            if (obj is not IPropertyStore store) return null;
            var key = CommandsNative.PKEY_Size;
            if (store.GetValue(ref key, out var pv) != ShellNative.S_OK) return null;
            try
            {
                if (pv.vt == 0 || CommandsNative.PropVariantToUInt64(ref pv, out ulong size) != ShellNative.S_OK) return null; // VT_EMPTY: no size
                return (long)Math.Min(size, long.MaxValue);
            }
            finally { CommandsNative.PropVariantClear(ref pv); }
        }
        finally
        {
            if (obj != null) Marshal.ReleaseComObject(obj);
            Marshal.Release(ptr);
        }
    }

    /// <summary>
    /// The view reported a selection change that kept the count and the first item (Ctrl+click swapping the others):
    /// nothing else is raised, but what the selection adds up to may differ.
    /// </summary>
    public event Action? SelectionContentChanged;
}

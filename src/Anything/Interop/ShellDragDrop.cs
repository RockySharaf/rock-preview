using System.Runtime.InteropServices;
using Anything.Explorer;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Anything;

/// <summary>
/// Drag and drop the shell's way, with copying what happens unless a key says otherwise (Windows' keys: Shift moves,
/// Ctrl copies, Alt makes a shortcut; a right-button drag still asks). Files dragged out carry the shell's own data, so
/// Outlook, mail, chat and upload pages, zip folders and phones take them as they take Explorer's, with copy
/// preferred. Drops go to the folder's own drop target, which takes files, Outlook's mails and attachments, and
/// anything else Explorer takes.
/// </summary>
internal static class ShellDragDrop
{
    public const int DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2, DROPEFFECT_LINK = 4;
    public const int MK_RBUTTON = 0x2, MK_SHIFT = 0x4, MK_CONTROL = 0x8, MK_ALT = 0x20;
    const int S_OK = 0;
    const short CF_HDROP = 15;
    const int TYMED_HGLOBAL = 1;

    /// <summary>Drags <paramref name="paths"/> out of the window <paramref name="source"/>; false when the shell couldn't make its data.</summary>
    public static bool DragOut(IntPtr source, IReadOnlyList<string> paths)
    {
        IntPtr data = CreateDataObject(paths);
        if (data == IntPtr.Zero) return false;
        try { SHDoDragDrop(source, data, IntPtr.Zero, DROPEFFECT_COPY | DROPEFFECT_MOVE | DROPEFFECT_LINK, out _); }
        finally { Marshal.Release(data); }
        return true;
    }

    /// <summary>The shell's data object for files and folders anywhere (an IDataObject pointer to release), copy preferred; zero on failure.</summary>
    public static IntPtr CreateDataObject(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return IntPtr.Zero;
        var pidls = new List<IntPtr>(paths.Count);
        try
        {
            foreach (string path in paths)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) != S_OK || pidl == IntPtr.Zero) return IntPtr.Zero;
                pidls.Add(pidl);
            }
            // A shell item array takes whole ID lists, so the files may be in different folders (search results).
            if (SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out IntPtr arrayPtr) != S_OK || arrayPtr == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                var array = (IShellItemArrayBind)Marshal.GetObjectForIUnknown(arrayPtr);
                try
                {
                    var bhid = BHID_DataObject;
                    var iid = IID_IDataObject;
                    if (array.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out IntPtr data) != S_OK || data == IntPtr.Zero) return IntPtr.Zero;
                    SetPreferredEffect(data, DROPEFFECT_COPY);
                    return data;
                }
                finally { Marshal.ReleaseComObject(array); }
            }
            catch (Exception e) when (e is COMException or InvalidCastException) { return IntPtr.Zero; }
            finally { Marshal.Release(arrayPtr); }
        }
        finally { foreach (var p in pidls) Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>
    /// Copy preferred for data that says nothing about it: a drag from Windows' folder view passes over the app's own
    /// windows first, so wherever it is dropped afterwards (the desktop, Explorer), copying is what happens by itself.
    /// </summary>
    public static void PreferCopy(IntPtr data)
    {
        if (data == IntPtr.Zero) return;
        object? obj = null;
        try
        {
            obj = Marshal.GetObjectForIUnknown(data);
            if (obj is not ComTypes.IDataObject dataObject) return;
            var format = PreferredEffectFormat;
            if (dataObject.QueryGetData(ref format) == S_OK) return; // the source chose
        }
        catch (Exception) { return; } // a data object that won't say (E_NOTIMPL and the like) is left as it is
        finally { if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
        SetPreferredEffect(data, DROPEFFECT_COPY);
    }

    static ComTypes.FORMATETC PreferredEffectFormat => new()
    {
        cfFormat = unchecked((short)RegisterClipboardFormat("Preferred DropEffect")),
        dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = ComTypes.TYMED.TYMED_HGLOBAL,
    };

    /// <summary>"Preferred DropEffect": what a drop does when no key is held (Explorer, the desktop and most programs follow it).</summary>
    static void SetPreferredEffect(IntPtr data, int effect)
    {
        object? obj = null;
        try
        {
            obj = Marshal.GetObjectForIUnknown(data);
            if (obj is not ComTypes.IDataObject dataObject) return;
            var format = PreferredEffectFormat;
            IntPtr global = GlobalAlloc(0x0040 /* GPTR: fixed, zeroed */, (UIntPtr)4); // the data object frees it (release = true)
            if (global == IntPtr.Zero) return;
            Marshal.WriteInt32(global, effect);
            var medium = new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_HGLOBAL, unionmember = global };
            bool taken = false;
            try { dataObject.SetData(ref format, ref medium, true); taken = true; }
            catch (Exception) { } // read-only data (E_NOTIMPL, E_ACCESSDENIED...): the drop goes on without it
            finally { if (!taken) GlobalFree(global); }
        }
        catch (Exception) { }
        finally { if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
    }

    /// <summary>A folder's own drop target (what dropping on it in Explorer does), or null.</summary>
    public static IDropTargetNative? FolderTarget(string folder)
    {
        var iidItem = IID_IShellItem;
        if (SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iidItem, out IntPtr itemPtr) != S_OK || itemPtr == IntPtr.Zero) return null;
        try
        {
            var item = (IShellItemBind)Marshal.GetObjectForIUnknown(itemPtr);
            try
            {
                var bhid = BHID_SFUIObject;
                var iid = IID_IDropTarget;
                if (item.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out IntPtr target) != S_OK || target == IntPtr.Zero) return null;
                try { return (IDropTargetNative)Marshal.GetObjectForIUnknown(target); }
                finally { Marshal.Release(target); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return null; }
        finally { Marshal.Release(itemPtr); }
    }

    /// <summary>
    /// Drops on a view of the app's own (the results, the thumbnails) go to the folder <paramref name="folderAt"/> names
    /// under the pointer (screen coordinates), or nowhere. <paramref name="dropSpot"/>, when given, is told after each
    /// move where the drag can drop (the pointer) or that it can't drop there (null), as the folder itself answered;
    /// <paramref name="dragEnded"/> is told when the drag leaves the view or is dropped, so a highlight drawn for it
    /// never stays behind. Undone when the window goes.
    /// </summary>
    public static void AcceptDrops(Control view, Func<Point, string?> folderAt, Action<Point?>? dropSpot = null, Action? dragEnded = null)
        => AcceptDrops(view, (pt, _) => folderAt(pt), dropSpot, dragEnded);

    /// <summary>
    /// As above, with <paramref name="folderAt"/> also given a way to read the file-system paths being dragged, from the
    /// drag's own data so it works for drags from Explorer's view and other programs too (empty when the data has
    /// none, as with Outlook's mails): it can then refuse a drop that would only copy files onto themselves. The paths
    /// are read once per drag, and only when asked for.
    /// </summary>
    public static void AcceptDrops(Control view, Func<Point, Func<IReadOnlyList<string>>, string?> folderAt,
        Action<Point?>? dropSpot = null, Action? dragEnded = null)
    {
        void Register()
        {
            CopyFirstDropTarget? target = null;
            target = new CopyFirstDropTarget(view.Handle, pt => folderAt(pt, target!.DraggedPaths), key => FolderTarget((string)key),
                dragImage: true, spot: dropSpot, ended: dragEnded);
            RegisterDragDrop(view.Handle, target);
        }
        if (view.IsHandleCreated) Register();
        view.HandleCreated += (_, _) => Register();
        view.HandleDestroyed += (_, _) => RevokeDragDrop(view.Handle);
    }

    /// <summary>
    /// The real folder a drop on <paramref name="folder"/> puts files in, to tell whether they would land where they
    /// already are: its own path, or for a library (Documents, Pictures...) the folder it saves into; null when there is
    /// none (This PC, Network, a phone).
    /// </summary>
    public static string? FileSystemFolder(string folder)
    {
        var iid = IID_IShellItem;
        IShellItem? item = null;
        try
        {
            if (ShellNative.SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out item) != S_OK || item == null) return null;
            if (ShellNative.NameOf(item, SIGDN.FileSysPath) is { Length: > 0 } path) return path;
            return folder.EndsWith(".library-ms", StringComparison.OrdinalIgnoreCase) ? LibrarySaveFolder(item) : null;
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return null; }
        finally { if (item != null) Marshal.ReleaseComObject(item); }
    }

    /// <summary>The folder a library's drop target saves into (its default save location), or null.</summary>
    static string? LibrarySaveFolder(IShellItem libraryItem)
    {
        var clsid = CLSID_ShellLibrary;
        var iid = IID_IShellLibrary;
        if (ShellNative.CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out IntPtr ptr) != S_OK || ptr == IntPtr.Zero) return null;
        try
        {
            var library = (IShellLibrary)Marshal.GetObjectForIUnknown(ptr);
            try
            {
                if (library.LoadLibraryFromItem(libraryItem, STGM_READ) != S_OK) return null;
                var iidItem = IID_IShellItem;
                if (library.GetDefaultSaveFolder(DSFT_DETECT, ref iidItem, out IShellItem? saveFolder) != S_OK || saveFolder == null) return null;
                try { return ShellNative.NameOf(saveFolder, SIGDN.FileSysPath); }
                finally { Marshal.ReleaseComObject(saveFolder); }
            }
            finally { Marshal.ReleaseComObject(library); }
        }
        finally { Marshal.Release(ptr); }
    }

    /// <summary>
    /// The file-system paths in a drag's data, or none: when one item has no path (a mail, a file inside a zip, a phone's
    /// photo) there are none, since such a drop can't be copying files onto themselves. Read from the shell's own item
    /// list when the data has one, which makes no files; CF_HDROP is read only when it hasn't (WinForms' own data),
    /// because asking a zip folder's data for it makes it extract the files to disk first.
    /// </summary>
    public static IReadOnlyList<string> DraggedPaths(IntPtr data)
    {
        if (data == IntPtr.Zero) return [];
        object? obj = null;
        try
        {
            obj = Marshal.GetObjectForIUnknown(data);
            if (obj is System.Windows.Forms.IDataObject managed) return managed.GetData(DataFormats.FileDrop) as string[] ?? []; // WinForms' own, from this process
            if (obj is not IDataObjectRaw dataObject) return [];
            var idList = HGlobalFormat(unchecked((short)RegisterClipboardFormat("Shell IDList Array")));
            if (dataObject.QueryGetData(ref idList) == S_OK) return ShellItemPaths(data);
            var hdrop = HGlobalFormat(CF_HDROP);
            if (dataObject.QueryGetData(ref hdrop) != S_OK || dataObject.GetData(ref hdrop, out var medium) != S_OK) return [];
            try { return medium.tymed == TYMED_HGLOBAL ? DropFilePaths(medium.unionmember) : []; }
            finally { ReleaseStgMedium(ref medium); } // frees the memory, or hands it back to the source (pUnkForRelease)
        }
        catch (Exception) { return []; } // a data object that won't say (E_NOTIMPL, a source that went away...) has none
        finally { if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); }
    }

    static ComTypes.FORMATETC HGlobalFormat(short format) => new()
    {
        cfFormat = format,
        dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = ComTypes.TYMED.TYMED_HGLOBAL,
    };

    /// <summary>Each dragged item's file-system path, from the shell's item list; none when one item has no path.</summary>
    static IReadOnlyList<string> ShellItemPaths(IntPtr data)
    {
        var iid = IID_IShellItemArray;
        if (SHCreateShellItemArrayFromDataObject(data, ref iid, out IShellItemArray? array) != S_OK || array == null) return [];
        try
        {
            if (array.GetCount(out uint count) != S_OK) return [];
            var paths = new List<string>((int)Math.Min(count, 4096u));
            for (uint i = 0; i < count; i++)
            {
                if (array.GetItemAt(i, out IShellItem? item) != S_OK || item == null) return [];
                try
                {
                    if (ShellNative.NameOf(item, SIGDN.FileSysPath) is not { Length: > 0 } path) return [];
                    paths.Add(path);
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            return paths;
        }
        finally { Marshal.ReleaseComObject(array); }
    }

    /// <summary>
    /// The paths in a CF_HDROP block (DROPFILES, then the names one after another, each ending in a null, the list in
    /// two), read in one pass: DragQueryFile would walk the list from the start for every name.
    /// </summary>
    internal static IReadOnlyList<string> DropFilePaths(IntPtr hdrop)
    {
        if (hdrop == IntPtr.Zero) return [];
        long size = (long)(ulong)GlobalSize(hdrop);
        IntPtr block = GlobalLock(hdrop);
        if (block == IntPtr.Zero) return [];
        try
        {
            const int DropFilesSize = 20; // pFiles, pt, fNC, fWide
            if (size < DropFilesSize) return [];
            long offset = (uint)Marshal.ReadInt32(block, 0);
            bool wide = Marshal.ReadInt32(block, 16) != 0;
            if (offset < DropFilesSize || offset >= size) return [];
            var bytes = new byte[size - offset];
            Marshal.Copy(block + (nint)offset, bytes, 0, bytes.Length);
            var paths = new List<string>();
            if (wide)
            {
                ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, bytes.Length & ~1));
                for (int end; (end = chars.IndexOf('\0')) > 0; chars = chars[(end + 1)..])
                    paths.Add(new string(chars[..end]));
            }
            else
            {
                for (int start = 0, end; start < bytes.Length && (end = Array.IndexOf(bytes, (byte)0, start)) > start; start = end + 1)
                    paths.Add(Marshal.PtrToStringAnsi(block + (nint)(offset + start), end - start));
            }
            return paths;
        }
        finally { GlobalUnlock(hdrop); }
    }

    /// <summary>
    /// Makes copying the first choice for the drop targets Windows' own controls registered inside <paramref name="root"/>
    /// (the folder view, the navigation pane): each is wrapped, and the wrapper hands every call on. Run again after
    /// the control makes new ones (a folder view per folder).
    /// </summary>
    public static void CopyFirstInside(IntPtr root)
    {
        if (root == IntPtr.Zero) return;
        var windows = new List<IntPtr> { root };
        EnumChildWindows(root, (hwnd, _) => { windows.Add(hwnd); return true; }, IntPtr.Zero);
        foreach (IntPtr hwnd in windows)
        {
            IntPtr registered = GetProp(hwnd, "OleDropTargetInterface"); // the window's IDropTarget, in this process
            if (registered == IntPtr.Zero) continue;
            object obj;
            try { obj = Marshal.GetObjectForIUnknown(registered); }
            catch (ArgumentException) { continue; }
            if (obj is CopyFirstDropTarget || obj is not IDropTargetNative inner) continue;
            if (RevokeDragDrop(hwnd) != S_OK) continue;
            var wrapper = new CopyFirstDropTarget(hwnd, _ => inner, key => (IDropTargetNative)key, dragImage: false, owned: false);
            if (RegisterDragDrop(hwnd, wrapper) != S_OK) RegisterDragDrop(hwnd, inner); // as it was
        }
    }

    /// <summary>The effects a drop may have: copying only, unless a key (or the right button) asks for something else.</summary>
    public static int Allowed(int keys, int effects) =>
        (keys & (MK_SHIFT | MK_CONTROL | MK_ALT | MK_RBUTTON)) == 0 && (effects & DROPEFFECT_COPY) != 0 ? DROPEFFECT_COPY : effects;

    static readonly Guid IID_IDataObject = new("0000010e-0000-0000-C000-000000000046");
    static readonly Guid IID_IDropTarget = new("00000122-0000-0000-C000-000000000046");
    static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    static readonly Guid BHID_SFUIObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");
    static readonly Guid BHID_DataObject = new("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");
    static readonly Guid IID_IShellItemArray = new("b63ea76d-1f85-456f-a19c-48159efa858b");
    static readonly Guid CLSID_ShellLibrary = new("d9b3211d-e57f-4426-aaef-30a806add397");
    static readonly Guid IID_IShellLibrary = new("11a66efa-382e-451a-9234-1e0e12ef3085");
    const uint CLSCTX_INPROC_SERVER = 0x1, STGM_READ = 0x0;
    const int DSFT_DETECT = 1;

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemBind
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemArrayBind
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    }

    /// <summary>IDataObject as far as QueryGetData, with a medium whose pUnkForRelease stays a plain pointer for ReleaseStgMedium.</summary>
    [ComImport, Guid("0000010e-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDataObjectRaw
    {
        [PreserveSig] int GetData(ref ComTypes.FORMATETC format, out STGMEDIUM medium);
        [PreserveSig] int GetDataHere(IntPtr format, IntPtr medium);
        [PreserveSig] int QueryGetData(ref ComTypes.FORMATETC format);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STGMEDIUM { public int tymed; public IntPtr unionmember; public IntPtr pUnkForRelease; }

    /// <summary>IShellLibrary as far as GetDefaultSaveFolder (shobjidl_core.h).</summary>
    [ComImport, Guid("11a66efa-382e-451a-9234-1e0e12ef3085"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLibrary
    {
        [PreserveSig] int LoadLibraryFromItem(IShellItem psiLibrary, uint grfMode);
        [PreserveSig] int LoadLibraryFromKnownFolder(ref Guid kfidLibrary, uint grfMode);
        [PreserveSig] int AddFolder(IntPtr psiLocation);
        [PreserveSig] int RemoveFolder(IntPtr psiLocation);
        [PreserveSig] int GetFolders(int lff, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int ResolveFolder(IntPtr psiFolderToResolve, uint dwTimeout, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetDefaultSaveFolder(int dsft, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")]
    static extern int SHCreateShellItemArrayFromIDLists(uint cidl, IntPtr[] rgpidl, out IntPtr ppsiItemArray);
    [DllImport("shell32.dll")]
    static extern int SHDoDragDrop(IntPtr hwnd, IntPtr pdata, IntPtr pdsrc, int dwEffect, out int pdwEffect);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr global);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr hwnd, string name);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("ole32.dll")] static extern int RegisterDragDrop(IntPtr hwnd, IDropTargetNative target);
    [DllImport("ole32.dll")] static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr global);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr global);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr global);
    [DllImport("shell32.dll")]
    static extern int SHCreateShellItemArrayFromDataObject(IntPtr pdo, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemArray? ppv);
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTL { public int X, Y; }

/// <summary>IDropTarget, with the data object as a plain pointer so it passes from one target to the next untouched.</summary>
[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropTargetNative
{
    [PreserveSig] int DragEnter(IntPtr pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
    [PreserveSig] int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop(IntPtr pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
}

/// <summary>
/// A window's drop target that hands each call to the drop target under the pointer (a folder's, or the one Windows'
/// control registered itself), with copying the first choice (<see cref="ShellDragDrop.Allowed"/>). The target is
/// looked up by a key, so moving within one folder keeps talking to the same one; moving to another leaves the first.
/// </summary>
[ComVisible(true)]
internal sealed class CopyFirstDropTarget(IntPtr hwnd, Func<Point, object?> keyAt, Func<object, IDropTargetNative?> open,
    bool dragImage, bool owned = true, Action<Point?>? spot = null, Action? ended = null) : IDropTargetNative
{
    static readonly object Unset = new();
    IntPtr _data;
    IReadOnlyList<string>? _paths;
    object? _key = Unset;
    IDropTargetNative? _inner;
    IDropTargetHelper? _helper;
    bool _wide; // the target under the pointer takes only what copying isn't: the drop offers every effect

    public int DragEnter(IntPtr pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        ReleaseData();
        _data = pDataObj;
        if (_data != IntPtr.Zero) Marshal.AddRef(_data);
        _key = Unset;
        ShellDragDrop.PreferCopy(pDataObj);
        if (dragImage)
        {
            try { _helper ??= (IDropTargetHelper)new DragDropHelper(); } catch (COMException) { }
            var p = new POINT { X = pt.X, Y = pt.Y };
            _helper?.DragEnter(hwnd, pDataObj, ref p, pdwEffect);
        }
        return Over(grfKeyState, pt, ref pdwEffect);
    }

    public int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect) => Over(grfKeyState, pt, ref pdwEffect);

    int Over(int keys, POINTL pt, ref int effect)
    {
        int all = effect, allowed = ShellDragDrop.Allowed(keys, effect);
        object? key = keyAt(new Point(pt.X, pt.Y));
        int result = ShellDragDrop.DROPEFFECT_NONE;
        _wide = false;
        if (!Equals(key, _key))
        {
            LeaveInner();
            _key = key;
            _inner = key == null ? null : open(key);
            int e = allowed;
            if (_inner != null)
            {
                if (_inner.DragEnter(_data, keys, pt, ref e) >= 0) result = Widen(keys, pt, e, allowed, all);
                else ForgetInner(); // it said no: nothing to leave
            }
        }
        else if (_inner != null)
        {
            int e = allowed;
            if (_inner.DragOver(keys, pt, ref e) >= 0) result = Widen(keys, pt, e, allowed, all);
        }
        effect = result;
        if (_helper != null) { var p = new POINT { X = pt.X, Y = pt.Y }; _helper.DragOver(ref p, effect); }
        if (spot != null)
        {
            try { spot(result != ShellDragDrop.DROPEFFECT_NONE ? new Point(pt.X, pt.Y) : null); }
            catch (Exception) { } // nothing the window throws may reach OLE
        }
        return 0;
    }

    /// <summary>
    /// The file-system paths being dragged over the window now (<see cref="ShellDragDrop.DraggedPaths"/>), read the first
    /// time they are asked for in a drag and let go of when it ends.
    /// </summary>
    public IReadOnlyList<string> DraggedPaths() => _paths ??= ShellDragDrop.DraggedPaths(_data);

    /// <summary>
    /// A place that can't take a copy (Quick access pins with a shortcut, say) is asked again with every effect the
    /// drag allows, so it still takes what it can; the drop then goes the same way.
    /// </summary>
    int Widen(int keys, POINTL pt, int effect, int allowed, int all)
    {
        if (effect != ShellDragDrop.DROPEFFECT_NONE || allowed == all || _inner == null) return effect;
        int e = all;
        if (_inner.DragOver(keys, pt, ref e) < 0 || e == ShellDragDrop.DROPEFFECT_NONE) return ShellDragDrop.DROPEFFECT_NONE;
        _wide = true;
        return e;
    }

    public int DragLeave()
    {
        LeaveInner();
        _helper?.DragLeave();
        ReleaseData();
        Ended();
        return 0;
    }

    public int Drop(IntPtr pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        int all = pdwEffect, allowed = ShellDragDrop.Allowed(grfKeyState, pdwEffect);
        object? key = keyAt(new Point(pt.X, pt.Y));
        int result = ShellDragDrop.DROPEFFECT_NONE;
        try
        {
            if (!Equals(key, _key))
            {
                LeaveInner();
                _key = key;
                _inner = key == null ? null : open(key);
                _wide = false;
                int enter = allowed;
                if (_inner != null)
                {
                    if (_inner.DragEnter(pDataObj, grfKeyState, pt, ref enter) < 0) ForgetInner(); // it said no: nothing to leave
                    else Widen(grfKeyState, pt, enter, allowed, all);
                }
            }
            if (_inner != null)
            {
                int e = _wide ? all : allowed;
                if (_inner.Drop(pDataObj, grfKeyState, pt, ref e) >= 0) result = e;
                ForgetInner(); // Drop ends the drag for it: no DragLeave after
            }
        }
        finally
        {
            pdwEffect = result;
            if (_helper != null) { var p = new POINT { X = pt.X, Y = pt.Y }; _helper.Drop(pDataObj, ref p, result); }
            ReleaseData();
            _key = Unset;
            Ended();
        }
        return 0;
    }

    /// <summary>Tells the window the drag left it or was dropped; nothing it throws may reach OLE.</summary>
    void Ended()
    {
        try { ended?.Invoke(); } catch (Exception) { }
    }

    void LeaveInner()
    {
        if (_inner == null) return;
        try { _inner.DragLeave(); } catch (COMException) { }
        ForgetInner();
    }

    void ForgetInner()
    {
        // A folder's target is this wrapper's to let go of; the one a window registered stays with the window.
        if (owned && _inner != null && Marshal.IsComObject(_inner)) Marshal.ReleaseComObject(_inner);
        _inner = null;
    }

    void ReleaseData()
    {
        if (_data != IntPtr.Zero) Marshal.Release(_data);
        _data = IntPtr.Zero;
        _paths = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [ComImport, Guid("4657278A-411B-11d2-839A-00C04FD918D0")]
    class DragDropHelper;

    /// <summary>Shows the dragged items' picture (and "Copy to ...") over a window that isn't Explorer's.</summary>
    [ComImport, Guid("4657278B-411B-11d2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTargetHelper
    {
        [PreserveSig] int DragEnter(IntPtr hwndTarget, IntPtr pDataObject, ref POINT ppt, int dwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int DragOver(ref POINT ppt, int dwEffect);
        [PreserveSig] int Drop(IntPtr pDataObject, ref POINT ppt, int dwEffect);
        [PreserveSig] int Show(bool fShow);
    }
}

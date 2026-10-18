using System.Runtime.InteropServices;

namespace Anything.Interop;

[ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
    void EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
    void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
    void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
    void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
    [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
    void GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
    void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
}

[StructLayout(LayoutKind.Sequential)]
internal struct CMINVOKECOMMANDINFOEX
{
    public int cbSize;
    public uint fMask;
    public IntPtr hwnd;
    public IntPtr lpVerb;
    public IntPtr lpParameters;
    public IntPtr lpDirectory;
    public int nShow;
    public uint dwHotKey;
    public IntPtr hIcon;
    public IntPtr lpTitle;
    public IntPtr lpVerbW;
    public IntPtr lpParametersW;
    public IntPtr lpDirectoryW;
    public IntPtr lpTitleW;
    public Native.POINT ptInvoke;
}

[ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
}

[ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu2
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
}

[ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu3
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
}

[ComImport, Guid("000214E8-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellExtInit
{
    [PreserveSig] int Initialize(IntPtr pidlFolder, IntPtr pdtobj, IntPtr hkeyProgID);
}

[ComImport, Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IObjectWithSiteForMenu
{
    [PreserveSig] int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? pUnkSite);
    [PreserveSig] int GetSite(ref Guid riid, out IntPtr ppvSite);
}

/// <summary>An extra (non-shell) item placed on top of the Explorer context menu.</summary>
internal sealed record ExtraMenuItem(string Text, Action Invoke);

/// <summary>
/// Shows the real Explorer context menu for a set of file system paths. While the menu is open,
/// owner-draw/submenu messages must be forwarded via <see cref="HandleMenuMessage"/> so that
/// "Send to" and "Open with" populate.
/// </summary>
internal sealed class ShellContextMenu
{
    static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");

    const uint FirstShellId = 1;
    const uint LastShellId = 0x7000;
    const uint FirstExtraId = 0x7100;

    static IContextMenu2? s_menu2;
    static IContextMenu3? s_menu3;

    /// <summary>Called from the owner window's WndProc. Returns true when the message was consumed.</summary>
    public static bool HandleMenuMessage(ref Message m)
    {
        if (s_menu2 is null && s_menu3 is null) return false;
        if (m.Msg is not (Native.WM_INITMENUPOPUP or Native.WM_DRAWITEM or Native.WM_MEASUREITEM or Native.WM_MENUCHAR)) return false;
        try
        {
            if (s_menu3 is not null)
            {
                if (s_menu3.HandleMenuMsg2((uint)m.Msg, m.WParam, m.LParam, out IntPtr result) == 0)
                {
                    m.Result = result;
                    return true;
                }
            }
            else if (s_menu2 is not null && m.Msg != Native.WM_MENUCHAR)
            {
                if (s_menu2.HandleMenuMsg((uint)m.Msg, m.WParam, m.LParam) == 0)
                {
                    m.Result = m.Msg == Native.WM_INITMENUPOPUP ? IntPtr.Zero : new IntPtr(1);
                    return true;
                }
            }
        }
        catch (COMException) { }
        return false;
    }

    /// <summary>
    /// Shows the menu at <paramref name="screenPoint"/>. Returns false when the shell menu could not be built
    /// (the caller then shows a fallback menu). <paramref name="onRename"/> handles the "rename" verb,
    /// which the shell cannot perform outside of an Explorer view.
    /// </summary>
    public static bool Show(IWin32Window owner, IReadOnlyList<string> paths, Point screenPoint, IReadOnlyList<ExtraMenuItem> extras, Action? onRename)
    {
        if (paths.Count == 0) return false;
        var pidls = new List<IntPtr>();
        IntPtr folderPtr = IntPtr.Zero, menuPtr = IntPtr.Zero, hmenu = IntPtr.Zero;
        IShellFolder? folder = null;
        object? menuObj = null;
        try
        {
            foreach (var p in paths)
            {
                if (Native.SHParseDisplayName(p, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                    return false;
                pidls.Add(pidl);
            }

            // Same parent -> bind to the parent folder with child pidls; otherwise use the desktop with absolute pidls.
            string? parent0 = Path.GetDirectoryName(paths[0]);
            bool sameParent = paths.All(p => string.Equals(Path.GetDirectoryName(p), parent0, StringComparison.OrdinalIgnoreCase));
            IntPtr[] children;
            if (sameParent)
            {
                children = new IntPtr[pidls.Count];
                Guid iid = IID_IShellFolder;
                for (int i = 0; i < pidls.Count; i++)
                {
                    if (Native.SHBindToParent(pidls[i], ref iid, out IntPtr pf, out IntPtr child) != 0) return false;
                    children[i] = child; // points inside pidls[i]; freed with it
                    if (i == 0) folderPtr = pf; else Marshal.Release(pf);
                }
            }
            else
            {
                if (Native.SHGetDesktopFolder(out folderPtr) != 0) return false;
                children = pidls.ToArray();
            }

            folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            Guid iidMenu = IID_IContextMenu;
            IntPtr ownerHandle = owner.Handle;
            if (folder.GetUIObjectOf(ownerHandle, (uint)children.Length, children, ref iidMenu, IntPtr.Zero, out menuPtr) != 0 || menuPtr == IntPtr.Zero)
                return false;
            menuObj = Marshal.GetObjectForIUnknown(menuPtr);
            var menu = (IContextMenu)menuObj;

            hmenu = Native.CreatePopupMenu();
            uint flags = Native.CMF_NORMAL | Native.CMF_EXPLORE | Native.CMF_CANRENAME;
            bool shift = Native.IsKeyDown(Keys.ShiftKey);
            if (shift) flags |= Native.CMF_EXTENDEDVERBS;
            int hr = menu.QueryContextMenu(hmenu, 0, FirstShellId, LastShellId, flags);
            if (hr < 0) return false;

            // Everything's own items go on top, right after the default (bold) verb when it is first.
            int defPos = Native.GetMenuDefaultItem(hmenu, 1, Native.GMDI_USEDISABLED);
            uint insertAt = defPos >= 0 && defPos < 3 ? (uint)defPos + 1 : 0;
            for (int i = 0; i < extras.Count; i++)
                Native.InsertMenu(hmenu, insertAt + (uint)i, Native.MF_BYPOSITION | Native.MF_STRING, (UIntPtr)(FirstExtraId + (uint)i), extras[i].Text);
            Native.InsertMenu(hmenu, insertAt + (uint)extras.Count, Native.MF_BYPOSITION | Native.MF_SEPARATOR, UIntPtr.Zero, null);

            s_menu3 = menuObj as IContextMenu3;
            s_menu2 = s_menu3 is null ? menuObj as IContextMenu2 : null;
            int cmd;
            try
            {
                cmd = Native.TrackPopupMenuEx(hmenu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, screenPoint.X, screenPoint.Y, ownerHandle, IntPtr.Zero);
            }
            finally
            {
                s_menu2 = null;
                s_menu3 = null;
            }

            if (cmd <= 0) return true;
            if (cmd >= FirstExtraId && cmd < FirstExtraId + extras.Count)
            {
                extras[cmd - (int)FirstExtraId].Invoke();
                return true;
            }
            if (cmd >= FirstShellId && cmd <= LastShellId)
            {
                uint offset = (uint)cmd - FirstShellId;
                string? verb = GetVerb(menu, offset);
                if (string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase) && onRename is not null)
                {
                    onRename();
                    return true;
                }
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask = Native.CMIC_MASK_UNICODE | Native.CMIC_MASK_PTINVOKE
                            | (shift ? Native.CMIC_MASK_SHIFT_DOWN : 0)
                            | (Native.IsKeyDown(Keys.ControlKey) ? Native.CMIC_MASK_CONTROL_DOWN : 0),
                    hwnd = ownerHandle,
                    lpVerb = (IntPtr)offset,
                    lpVerbW = (IntPtr)offset,
                    nShow = 1, // SW_SHOWNORMAL
                    ptInvoke = new Native.POINT { X = screenPoint.X, Y = screenPoint.Y },
                };
                menu.InvokeCommand(ref info);
            }
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (hmenu != IntPtr.Zero) Native.DestroyMenu(hmenu);
            if (menuObj is not null) Marshal.ReleaseComObject(menuObj);
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (folder is not null) Marshal.ReleaseComObject(folder);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            foreach (var p in pidls) Native.ILFree(p);
        }
    }

    /// <summary>Runs a canonical shell verb ("Windows.ModernShare", "properties"...) on file system paths.</summary>
    public static bool InvokeVerb(IWin32Window owner, IReadOnlyList<string> paths, string verb)
    {
        if (paths.Count == 0) return false;
        var pidls = new List<IntPtr>();
        IntPtr folderPtr = IntPtr.Zero, menuPtr = IntPtr.Zero, hmenu = IntPtr.Zero, verbA = IntPtr.Zero, verbW = IntPtr.Zero;
        IShellFolder? folder = null;
        object? menuObj = null;
        try
        {
            foreach (var p in paths)
            {
                if (Native.SHParseDisplayName(p, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return false;
                pidls.Add(pidl);
            }
            if (Native.SHGetDesktopFolder(out folderPtr) != 0) return false;
            folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            Guid iidMenu = IID_IContextMenu;
            if (folder.GetUIObjectOf(owner.Handle, (uint)pidls.Count, pidls.ToArray(), ref iidMenu, IntPtr.Zero, out menuPtr) != 0 || menuPtr == IntPtr.Zero)
                return false;
            menuObj = Marshal.GetObjectForIUnknown(menuPtr);
            var menu = (IContextMenu)menuObj;
            hmenu = Native.CreatePopupMenu();
            if (menu.QueryContextMenu(hmenu, 0, FirstShellId, LastShellId, Native.CMF_NORMAL) < 0) return false;
            verbA = Marshal.StringToHGlobalAnsi(verb);
            verbW = Marshal.StringToHGlobalUni(verb);
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = Native.CMIC_MASK_UNICODE,
                hwnd = owner.Handle,
                lpVerb = verbA,
                lpVerbW = verbW,
                nShow = 1,
            };
            return menu.InvokeCommand(ref info) >= 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (verbA != IntPtr.Zero) Marshal.FreeHGlobal(verbA);
            if (verbW != IntPtr.Zero) Marshal.FreeHGlobal(verbW);
            if (hmenu != IntPtr.Zero) Native.DestroyMenu(hmenu);
            if (menuObj is not null) Marshal.ReleaseComObject(menuObj);
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (folder is not null) Marshal.ReleaseComObject(folder);
            if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            foreach (var p in pidls) Native.ILFree(p);
        }
    }

    static readonly Guid CLSID_NewMenu = new("D969A300-E7FF-11d0-A93B-00A0C90F2719");

    [DllImport("user32.dll")] static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);

    /// <summary>
    /// Shows Explorer's own "New" menu (Folder, Shortcut, Text Document...) for <paramref name="folder"/>. With
    /// <paramref name="site"/> = that folder's shell view, the shell selects the new item and starts renaming it.
    /// </summary>
    public static bool ShowNewMenu(IWin32Window owner, string folder, object? site, Point screenPoint)
    {
        IntPtr pidl = IntPtr.Zero, hmenu = IntPtr.Zero;
        object? obj = null;
        try
        {
            if (Native.SHParseDisplayName(folder, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return false;
            if (Type.GetTypeFromCLSID(CLSID_NewMenu, throwOnError: false) is not { } type) return false;
            obj = Activator.CreateInstance(type);
            if (obj is not IShellExtInit init || init.Initialize(pidl, IntPtr.Zero, IntPtr.Zero) < 0) return false;
            if (site != null && obj is IObjectWithSiteForMenu ows) ows.SetSite(site);
            var menu = (IContextMenu)obj;
            hmenu = Native.CreatePopupMenu();
            if (menu.QueryContextMenu(hmenu, 0, FirstShellId, LastShellId, Native.CMF_NORMAL) < 0) return false;
            IntPtr sub = GetSubMenu(hmenu, 0); // the menu holds one item, "New", whose submenu fills on WM_INITMENUPOPUP
            if (sub == IntPtr.Zero) return false;

            s_menu3 = obj as IContextMenu3;
            s_menu2 = s_menu3 is null ? obj as IContextMenu2 : null;
            int cmd;
            try { cmd = Native.TrackPopupMenuEx(sub, Native.TPM_RETURNCMD, screenPoint.X, screenPoint.Y, owner.Handle, IntPtr.Zero); }
            finally
            {
                s_menu2 = null;
                s_menu3 = null;
            }
            if (cmd < FirstShellId || cmd > LastShellId) return true;
            uint offset = (uint)cmd - FirstShellId;
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = Native.CMIC_MASK_UNICODE | Native.CMIC_MASK_PTINVOKE,
                hwnd = owner.Handle,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                nShow = 1, // SW_SHOWNORMAL
                ptInvoke = new Native.POINT { X = screenPoint.X, Y = screenPoint.Y },
            };
            menu.InvokeCommand(ref info);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or MissingMethodException)
        {
            return false;
        }
        finally
        {
            if (hmenu != IntPtr.Zero) Native.DestroyMenu(hmenu);
            if (obj is not null) Marshal.ReleaseComObject(obj);
            if (pidl != IntPtr.Zero) Native.ILFree(pidl);
        }
    }

    static string? GetVerb(IContextMenu menu, uint offset)
    {
        IntPtr buf = Marshal.AllocHGlobal(512 * 2);
        try
        {
            Marshal.WriteInt16(buf, 0);
            if (menu.GetCommandString((UIntPtr)offset, Native.GCS_VERBW, IntPtr.Zero, buf, 512) != 0) return null;
            return Marshal.PtrToStringUni(buf);
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }
}

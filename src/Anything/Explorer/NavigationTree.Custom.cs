using System.Runtime.InteropServices;
using Anything.Interop;

namespace Anything.Explorer;

/// <summary>
/// The pane as the user wants it: Quick access with only the folders pinned there (not the ones Windows adds as
/// frequent), and Home, Gallery, OneDrive, Network or Linux taken out with a right-click ("Remove from navigation
/// pane"), brought back in Settings › Defaults.
/// </summary>
internal sealed partial class NavigationTree
{
    /// <summary>The roots that can be taken out, by the key the settings keep them under.</summary>
    public static readonly (string Key, string Name)[] RemovableRoots =
        [("home", "Home"), ("gallery", "Gallery"), ("onedrive", "OneDrive"), ("network", "Network"), ("linux", "Linux")];

    /// <summary>The roots not shown (<see cref="RemovableRoots"/> keys). Set before the tree is created, or RefreshRoots.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlySet<string> HiddenRoots { get; set; } = new HashSet<string>();

    /// <summary>Quick access shows only the folders pinned there, not those Windows adds as frequent.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool PinnedOnly { get; set; } = true;

    /// <summary>"Remove from navigation pane" was chosen on a root: its key.</summary>
    public event Action<string>? RootRemoveRequested;

    readonly List<(IShellItem Item, string Key)> _rootKeys = [];
    PinnedFilter? _pinnedFilter;
    IntPtr _pinnedFilterPtr;

    bool Hidden(string key) => HiddenRoots.Contains(key);

    /// <summary>The filter for Quick access's root (pinned folders only), or none. <paramref name="flags"/>: what it lists.</summary>
    IntPtr QuickAccessFilter(SHCONTF flags)
    {
        if (!PinnedOnly) return IntPtr.Zero;
        if (_pinnedFilterPtr == IntPtr.Zero)
        {
            _pinnedFilter = new PinnedFilter((uint)flags);
            _pinnedFilterPtr = Marshal.GetComInterfaceForObject(_pinnedFilter, typeof(IShellItemFilter));
        }
        return _pinnedFilterPtr;
    }

    void ReleaseFilter()
    {
        if (_pinnedFilterPtr != IntPtr.Zero) Marshal.Release(_pinnedFilterPtr);
        _pinnedFilterPtr = IntPtr.Zero;
        _pinnedFilter = null;
    }

    /// <summary>The removable root an item is (its key), or null.</summary>
    string? RootKeyOf(IntPtr psi)
    {
        if (psi == IntPtr.Zero || _rootKeys.Count == 0) return null;
        try
        {
            var item = (IShellItem)Marshal.GetObjectForIUnknown(psi);
            try
            {
                foreach (var (root, key) in _rootKeys)
                    if (root.Compare(item, 0x10000000 /* SICHINT_CANONICAL */, out int order) == NavTreeNative.S_OK && order == 0) return key;
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException) { }
        return null;
    }

    /// <summary>
    /// The shell's menu for a root that can be taken out, with "Remove from navigation pane" added; null for any other
    /// item (the shell's own menu as it is).
    /// </summary>
    IntPtr WrapRootMenu(IntPtr psi, IntPtr menu, ref Guid riid)
    {
        if (menu == IntPtr.Zero || RootKeyOf(psi) is not { } key) return IntPtr.Zero;
        try
        {
            var inner = Marshal.GetObjectForIUnknown(menu);
            var wrapper = new ExtraItemMenu(inner, "Remove from navigation pane", () => BeginInvoke(() => RootRemoveRequested?.Invoke(key)));
            IntPtr unknown = Marshal.GetIUnknownForObject(wrapper);
            try { return Marshal.QueryInterface(unknown, in riid, out IntPtr ppv) == 0 ? ppv : IntPtr.Zero; }
            finally { Marshal.Release(unknown); }
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException) { return IntPtr.Zero; }
    }

    [ComImport, Guid("2659B475-EEB8-48b7-8F07-B378810F48CF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemFilter
    {
        [PreserveSig] int IncludeItem(IShellItem psi);
        [PreserveSig] int GetEnumFlagsForItem(IShellItem psi, out uint pgrfFlags);
    }

    /// <summary>
    /// Quick access's folders: those pinned there (System.Home.IsPinned), not the frequent ones Windows adds. Only Quick
    /// access's own items are looked at; the folders inside a pinned one all show.
    /// </summary>
    [ComVisible(true)]
    sealed class PinnedFilter(uint flags) : IShellItemFilter
    {
        static readonly Guid QuickAccess = new("679f85cb-0220-4080-b29b-5540cc05aab6");

        [StructLayout(LayoutKind.Sequential)]
        struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem2Bool
        {
            // IShellItem
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IntPtr ppsi);
            [PreserveSig] int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
            // IShellItem2, up to GetBool
            [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int Update(IntPtr pbc);
            [PreserveSig] int GetProperty(ref PROPERTYKEY key, IntPtr ppropvar);
            [PreserveSig] int GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
            [PreserveSig] int GetFileTime(ref PROPERTYKEY key, out long pft);
            [PreserveSig] int GetInt32(ref PROPERTYKEY key, out int pi);
            [PreserveSig] int GetString(ref PROPERTYKEY key, out IntPtr ppsz);
            [PreserveSig] int GetUInt32(ref PROPERTYKEY key, out uint pui);
            [PreserveSig] int GetUInt64(ref PROPERTYKEY key, out ulong pull);
            [PreserveSig] int GetBool(ref PROPERTYKEY key, out int pf);
        }

        [DllImport("propsys.dll", CharSet = CharSet.Unicode)] static extern int PSGetPropertyKeyFromName(string name, out PROPERTYKEY key);

        PROPERTYKEY _isPinned;
        bool _known, _have;

        public int IncludeItem(IShellItem psi)
        {
            try
            {
                if (!_known) { _known = true; _have = PSGetPropertyKeyFromName("System.Home.IsPinned", out _isPinned) == 0; }
                if (!_have || psi is not IShellItem2Bool item) return 0;
                // Only Quick access's own items: a pinned folder's subfolders aren't pinned, and all show.
                if (psi.GetParent(out var parent) != 0 || parent == null) return 0;
                try
                {
                    string? name = ShellNative.NameOf(parent, SIGDN.DesktopAbsoluteParsing);
                    if (name == null || !name.Contains(QuickAccess.ToString(), StringComparison.OrdinalIgnoreCase)) return 0;
                }
                finally { Marshal.ReleaseComObject(parent); }
                var key = _isPinned;
                return item.GetBool(ref key, out int pinned) == 0 && pinned == 0 ? 1 /* S_FALSE: left out */ : 0;
            }
            catch (Exception) { return 0; }
        }

        public int GetEnumFlagsForItem(IShellItem psi, out uint pgrfFlags)
        {
            pgrfFlags = flags; // what the tree lists anyway (folders, for the pane): without it, nothing was listed
            return 0;
        }
    }

    /// <summary>
    /// A shell context menu with one item of ours at its end (after a separator): the shell's own items work as before.
    /// </summary>
    [ComVisible(true)]
    sealed class ExtraItemMenu(object inner, string text, Action run) : IContextMenu, IContextMenu2, IContextMenu3
    {
        uint _offset = uint.MaxValue;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

        public int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags)
        {
            int hr = inner is IContextMenu menu ? menu.QueryContextMenu(hmenu, indexMenu, idCmdFirst, idCmdLast, uFlags) : 0;
            if (hr < 0 || (uFlags & 0x1 /* CMF_DEFAULTONLY */) != 0) return hr;
            uint used = (uint)(hr & 0xFFFF);
            if (idCmdFirst + used > idCmdLast) return hr;
            AppendMenu(hmenu, 0x800 /* MF_SEPARATOR */, UIntPtr.Zero, null);
            AppendMenu(hmenu, 0 /* MF_STRING */, (UIntPtr)(idCmdFirst + used), text);
            _offset = used;
            return (int)(used + 1);
        }

        public int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici)
        {
            long verb = pici.lpVerb.ToInt64();
            if ((verb >> 16) == 0 && (uint)verb == _offset)
            {
                run();
                return 0;
            }
            return inner is IContextMenu menu ? menu.InvokeCommand(ref pici) : unchecked((int)0x80070057);
        }

        public int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax)
        {
            if ((uint)idCmd == _offset) return unchecked((int)0x80004001);
            return inner is IContextMenu menu ? menu.GetCommandString(idCmd, uType, pReserved, pszName, cchMax) : unchecked((int)0x80004001);
        }

        public int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam)
            => inner is IContextMenu2 menu ? menu.HandleMenuMsg(uMsg, wParam, lParam) : unchecked((int)0x80004001);

        public int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult)
        {
            plResult = IntPtr.Zero;
            if (inner is IContextMenu3 menu3) return menu3.HandleMenuMsg2(uMsg, wParam, lParam, out plResult);
            return inner is IContextMenu2 menu2 ? menu2.HandleMenuMsg(uMsg, wParam, lParam) : unchecked((int)0x80004001);
        }
    }
}

using System.Runtime.InteropServices;

namespace Anything.Explorer;

// COM declarations for hosting the Windows Shell's NamespaceTreeControl (the tree Explorer uses for its
// navigation pane, served by explorerframe.dll). GUIDs, vtable order and flag values are taken from the
// Windows SDK 10.0.26100 headers: ShObjIdl_core.h (INameSpaceTreeControl, NSTCSTYLE, NSTCROOTSTYLE,
// NSTCITEMSTATE, NSTCGNI, SHCONTF) and ShObjIdl.h (INameSpaceTreeControl2, INameSpaceTreeControlEvents,
// NSTCSTYLE2, NSTCEHITTEST, NSTCECLICKTYPE, CLSID_NamespaceTreeControl).

[ComImport, Guid("028212A3-B627-47E9-8856-C14265554E4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INameSpaceTreeControl
{
    [PreserveSig] int Initialize(IntPtr hwndParent, ref RECT prc, NSTCSTYLE nsctsFlags);
    [PreserveSig] int TreeAdvise([MarshalAs(UnmanagedType.IUnknown)] object punk, out uint pdwCookie);
    [PreserveSig] int TreeUnadvise(uint dwCookie);
    [PreserveSig] int AppendRoot(IShellItem psiRoot, SHCONTF grfEnumFlags, NSTCROOTSTYLE grfRootStyle, IntPtr pif);
    [PreserveSig] int InsertRoot(int iIndex, IShellItem psiRoot, SHCONTF grfEnumFlags, NSTCROOTSTYLE grfRootStyle, IntPtr pif);
    [PreserveSig] int RemoveRoot(IShellItem psiRoot);
    [PreserveSig] int RemoveAllRoots();
    [PreserveSig] int GetRootItems(out IShellItemArray ppsiaRootItems);
    [PreserveSig] int SetItemState(IShellItem psi, NSTCITEMSTATE nstcisMask, NSTCITEMSTATE nstcisFlags);
    [PreserveSig] int GetItemState(IShellItem psi, NSTCITEMSTATE nstcisMask, out NSTCITEMSTATE pnstcisFlags);
    [PreserveSig] int GetSelectedItems(out IShellItemArray psiaItems);
    [PreserveSig] int GetItemCustomState(IShellItem psi, out int piStateNumber);
    [PreserveSig] int SetItemCustomState(IShellItem psi, int iStateNumber);
    [PreserveSig] int EnsureItemVisible(IShellItem psi);
    [PreserveSig] int SetTheme([MarshalAs(UnmanagedType.LPWStr)] string pszTheme);
    [PreserveSig] int GetNextItem(IShellItem? psi, NSTCGNI nstcgi, out IShellItem ppsiNext);
    [PreserveSig] int HitTest(ref POINT ppt, out IShellItem ppsiOut);
    [PreserveSig] int GetItemRect(IShellItem psi, out RECT prect);
    [PreserveSig] int CollapseAll();
}

[ComImport, Guid("7cc7aed8-290e-49bc-8945-c1401cc9306c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INameSpaceTreeControl2
{
    // INameSpaceTreeControl
    [PreserveSig] int Initialize(IntPtr hwndParent, ref RECT prc, NSTCSTYLE nsctsFlags);
    [PreserveSig] int TreeAdvise([MarshalAs(UnmanagedType.IUnknown)] object punk, out uint pdwCookie);
    [PreserveSig] int TreeUnadvise(uint dwCookie);
    [PreserveSig] int AppendRoot(IShellItem psiRoot, SHCONTF grfEnumFlags, NSTCROOTSTYLE grfRootStyle, IntPtr pif);
    [PreserveSig] int InsertRoot(int iIndex, IShellItem psiRoot, SHCONTF grfEnumFlags, NSTCROOTSTYLE grfRootStyle, IntPtr pif);
    [PreserveSig] int RemoveRoot(IShellItem psiRoot);
    [PreserveSig] int RemoveAllRoots();
    [PreserveSig] int GetRootItems(out IShellItemArray ppsiaRootItems);
    [PreserveSig] int SetItemState(IShellItem psi, NSTCITEMSTATE nstcisMask, NSTCITEMSTATE nstcisFlags);
    [PreserveSig] int GetItemState(IShellItem psi, NSTCITEMSTATE nstcisMask, out NSTCITEMSTATE pnstcisFlags);
    [PreserveSig] int GetSelectedItems(out IShellItemArray psiaItems);
    [PreserveSig] int GetItemCustomState(IShellItem psi, out int piStateNumber);
    [PreserveSig] int SetItemCustomState(IShellItem psi, int iStateNumber);
    [PreserveSig] int EnsureItemVisible(IShellItem psi);
    [PreserveSig] int SetTheme([MarshalAs(UnmanagedType.LPWStr)] string pszTheme);
    [PreserveSig] int GetNextItem(IShellItem? psi, NSTCGNI nstcgi, out IShellItem ppsiNext);
    [PreserveSig] int HitTest(ref POINT ppt, out IShellItem ppsiOut);
    [PreserveSig] int GetItemRect(IShellItem psi, out RECT prect);
    [PreserveSig] int CollapseAll();
    // INameSpaceTreeControl2
    [PreserveSig] int SetControlStyle(NSTCSTYLE nstcsMask, NSTCSTYLE nstcsStyle);
    [PreserveSig] int GetControlStyle(NSTCSTYLE nstcsMask, out NSTCSTYLE pnstcsStyle);
    [PreserveSig] int SetControlStyle2(NSTCSTYLE2 nstcsMask, NSTCSTYLE2 nstcsStyle);
    [PreserveSig] int GetControlStyle2(NSTCSTYLE2 nstcsMask, out NSTCSTYLE2 pnstcsStyle);
}

/// <summary>
/// Sink passed to TreeAdvise. IShellItem / IShellItemArray / IContextMenu arguments are raw pointers so no RCW
/// is created per notification (OnItemAdded fires for every node); wrap them only when needed.
/// </summary>
[ComImport, Guid("93D77985-B3D8-4484-8318-672CDDA002CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INameSpaceTreeControlEvents
{
    [PreserveSig] int OnItemClick(IntPtr psi, NSTCEHITTEST nstceHitTest, NSTCECLICKTYPE nstceClickType);
    [PreserveSig] int OnPropertyItemCommit(IntPtr psi);
    [PreserveSig] int OnItemStateChanging(IntPtr psi, NSTCITEMSTATE nstcisMask, NSTCITEMSTATE nstcisState);
    [PreserveSig] int OnItemStateChanged(IntPtr psi, NSTCITEMSTATE nstcisMask, NSTCITEMSTATE nstcisState);
    [PreserveSig] int OnSelectionChanged(IntPtr psiaSelection);
    [PreserveSig] int OnKeyboardInput(uint uMsg, IntPtr wParam, IntPtr lParam);
    [PreserveSig] int OnBeforeExpand(IntPtr psi);
    [PreserveSig] int OnAfterExpand(IntPtr psi);
    [PreserveSig] int OnBeginLabelEdit(IntPtr psi);
    [PreserveSig] int OnEndLabelEdit(IntPtr psi);
    [PreserveSig] int OnGetToolTip(IntPtr psi, IntPtr pszTip, int cchTip);
    [PreserveSig] int OnBeforeItemDelete(IntPtr psi);
    [PreserveSig] int OnItemAdded(IntPtr psi, int fIsRoot);
    [PreserveSig] int OnItemDeleted(IntPtr psi, int fIsRoot);
    [PreserveSig] int OnBeforeContextMenu(IntPtr psi, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int OnAfterContextMenu(IntPtr psi, IntPtr pcmIn, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int OnBeforeStateImageChange(IntPtr psi);
    [PreserveSig] int OnGetDefaultIconIndex(IntPtr psi, out int piDefaultIcon, out int piOpenIcon);
}

/// <summary>Optional second interface of the TreeAdvise sink; the tree QIs for it to offer custom draw.</summary>
[ComImport, Guid("2D3BA758-33EE-42d5-BB7B-5F3431D86C78"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface INameSpaceTreeControlCustomDraw
{
    [PreserveSig] int PrePaint(IntPtr hdc, ref RECT prc, out IntPtr plres);
    [PreserveSig] int PostPaint(IntPtr hdc, ref RECT prc);
    [PreserveSig] int ItemPrePaint(IntPtr hdc, ref RECT prc, ref NSTCCUSTOMDRAW pnstccdItem, ref uint pclrText, ref uint pclrTextBk, out IntPtr plres);
    [PreserveSig] int ItemPostPaint(IntPtr hdc, ref RECT prc, ref NSTCCUSTOMDRAW pnstccdItem);
}

/// <summary>NSTCCUSTOMDRAW (ShObjIdl.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NSTCCUSTOMDRAW
{
    public IntPtr psi;          // IShellItem*
    public uint uItemState;     // CDIS_*
    public NSTCITEMSTATE nstcis;
    public IntPtr pszText;      // LPCWSTR
    public int iImage;
    public IntPtr himl;
    public int iLevel;
    public int iIndent;
}

[ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleWindow
{
    [PreserveSig] int GetWindow(out IntPtr phwnd);
    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
}

/// <summary>NSTCSTYLE (ShObjIdl_core.h).</summary>
[Flags]
internal enum NSTCSTYLE : uint
{
    HASEXPANDOS = 0x1,
    HASLINES = 0x2,
    SINGLECLICKEXPAND = 0x4,
    FULLROWSELECT = 0x8,
    SPRINGEXPAND = 0x10,
    HORIZONTALSCROLL = 0x20,
    ROOTHASEXPANDO = 0x40,
    SHOWSELECTIONALWAYS = 0x80,
    NOINFOTIP = 0x200,
    EVENHEIGHT = 0x400,
    NOREPLACEOPEN = 0x800,
    DISABLEDRAGDROP = 0x1000,
    NOORDERSTREAM = 0x2000,
    RICHTOOLTIP = 0x4000,
    BORDER = 0x8000,
    NOEDITLABELS = 0x10000,
    TABSTOP = 0x20000,
    FAVORITESMODE = 0x80000,
    AUTOHSCROLL = 0x100000,
    FADEINOUTEXPANDOS = 0x200000,
    EMPTYTEXT = 0x400000,
    CHECKBOXES = 0x800000,
    PARTIALCHECKBOXES = 0x1000000,
    EXCLUSIONCHECKBOXES = 0x2000000,
    DIMMEDCHECKBOXES = 0x4000000,
    NOINDENTCHECKS = 0x8000000,
    ALLOWJUNCTIONS = 0x10000000,
    SHOWTABSBUTTON = 0x20000000,
    SHOWDELETEBUTTON = 0x40000000,
    SHOWREFRESHBUTTON = 0x80000000,
}

/// <summary>NSTCSTYLE2 (ShObjIdl.h).</summary>
[Flags]
internal enum NSTCSTYLE2 : uint
{
    DEFAULT = 0,
    INTERRUPTNOTIFICATIONS = 0x1,
    SHOWNULLSPACEMENU = 0x2,
    DISPLAYPADDING = 0x4,
    DISPLAYPINNEDONLY = 0x8,
    NOSINGLETONAUTOEXPAND = 0x10,
    NEVERINSERTNONENUMERATED = 0x20,
}

/// <summary>NSTCROOTSTYLE (ShObjIdl_core.h).</summary>
[Flags]
internal enum NSTCROOTSTYLE : uint
{
    VISIBLE = 0,
    HIDDEN = 0x1,
    EXPANDED = 0x2,
}

/// <summary>NSTCITEMSTATE (ShObjIdl_core.h).</summary>
[Flags]
internal enum NSTCITEMSTATE : uint
{
    NONE = 0,
    SELECTED = 0x1,
    EXPANDED = 0x2,
    BOLD = 0x4,
    DISABLED = 0x8,
    SELECTEDNOEXPAND = 0x10,
}

/// <summary>NSTCGNI (ShObjIdl_core.h).</summary>
internal enum NSTCGNI : uint
{
    NEXT = 0,
    NEXTVISIBLE = 1,
    PREV = 2,
    PREVVISIBLE = 3,
    PARENT = 4,
    CHILD = 5,
    FIRSTVISIBLE = 6,
    LASTVISIBLE = 7,
}

/// <summary>_NSTCEHITTEST (ShObjIdl.h).</summary>
[Flags]
internal enum NSTCEHITTEST : uint
{
    NOWHERE = 0x1,
    ONITEMICON = 0x2,
    ONITEMLABEL = 0x4,
    ONITEMINDENT = 0x8,
    ONITEMBUTTON = 0x10,
    ONITEMRIGHT = 0x20,
    ONITEMSTATEICON = 0x40,
    ONITEM = 0x46,
    ONITEMTABBUTTON = 0x1000,
}

/// <summary>_NSTCECLICKTYPE (ShObjIdl.h).</summary>
[Flags]
internal enum NSTCECLICKTYPE : uint
{
    LBUTTON = 0x1,
    MBUTTON = 0x2,
    RBUTTON = 0x3,
    BUTTON = 0x3,
    DBLCLICK = 0x4,
}

/// <summary>_SHCONTF (ShObjIdl_core.h).</summary>
[Flags]
internal enum SHCONTF : uint
{
    CHECKING_FOR_CHILDREN = 0x10,
    FOLDERS = 0x20,
    NONFOLDERS = 0x40,
    INCLUDEHIDDEN = 0x80,
    INIT_ON_FIRST_NEXT = 0x100,
    NETPRINTERSRCH = 0x200,
    SHAREABLE = 0x400,
    STORAGE = 0x800,
    NAVIGATION_ENUM = 0x1000,
    FASTITEMS = 0x2000,
    FLATLIST = 0x4000,
    ENABLE_ASYNC = 0x8000,
    INCLUDESUPERHIDDEN = 0x10000,
}

internal static class NavTreeNative
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);

    /// <summary>SICHINT_CANONICAL | SICHINT_TEST_FILESYSPATH_IF_NOT_EQUAL (ShObjIdl_core.h).</summary>
    public const uint SICHINT_CANONICAL_FS = 0x10000000 | 0x20000000;

    public static readonly Guid CLSID_NamespaceTreeControl = new("AE054212-3535-4430-83ED-D501AA6680E6");

    public const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOMOVE = 0x2;
    public const int GW_CHILD = 5;
    public const int CDRF_DODEFAULT = 0x0, CDRF_NOTIFYPOSTPAINT = 0x10, CDRF_NOTIFYITEMDRAW = 0x20;
    public const uint SFGAO_FOLDER = 0x20000000;

    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(int crColor);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] public static extern int FillRect(IntPtr hdc, ref RECT lprc, IntPtr hbr);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHCreateItemFromRelativeName(IShellItem psiParent, string pszName, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetFocus();
    [DllImport("user32.dll")] public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
}

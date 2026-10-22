using System.Runtime.InteropServices;

namespace Anything.Explorer;

// COM declarations for hosting the Windows Shell's own ExplorerBrowser (the file view Explorer itself uses).
// GUIDs and vtable order are taken from the Windows SDK (ShObjIdl_core.h / shlguid.h, 10.0.26100).

[ComImport, Guid("dfd3b6b5-c10c-4be9-85f6-a66969f402f6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExplorerBrowser
{
    [PreserveSig] int Initialize(IntPtr hwndParent, ref RECT prc, ref FOLDERSETTINGS pfs);
    [PreserveSig] int Destroy();
    [PreserveSig] int SetRect(IntPtr phdwp, RECT rcBrowser);
    [PreserveSig] int SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string pszPropertyBag);
    [PreserveSig] int SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string pszEmptyText);
    [PreserveSig] int SetFolderSettings(ref FOLDERSETTINGS pfs);
    [PreserveSig] int Advise(IExplorerBrowserEvents psbe, out uint pdwCookie);
    [PreserveSig] int Unadvise(uint dwCookie);
    [PreserveSig] int SetOptions(EXPLORER_BROWSER_OPTIONS dwFlag);
    [PreserveSig] int GetOptions(out EXPLORER_BROWSER_OPTIONS pdwFlag);
    [PreserveSig] int BrowseToIDList(IntPtr pidl, uint uFlags);
    [PreserveSig] int BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint uFlags);
    [PreserveSig] int FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint dwFlags);
    [PreserveSig] int RemoveAll();
    [PreserveSig] int GetCurrentView(ref Guid riid, out IntPtr ppv);
}

[ComImport, Guid("361bbdc7-e6ee-4e13-be58-58e2240c810f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExplorerBrowserEvents
{
    [PreserveSig] int OnNavigationPending(IntPtr pidlFolder);
    [PreserveSig] int OnViewCreated(IntPtr psv);
    [PreserveSig] int OnNavigationComplete(IntPtr pidlFolder);
    [PreserveSig] int OnNavigationFailed(IntPtr pidlFolder);
}

[ComImport, Guid("e07010ec-bc17-44c0-97b0-46c7c95b9edc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExplorerPaneVisibility
{
    [PreserveSig] int GetPaneState(ref Guid ep, out uint peps);
}

/// <summary>The view asks its host (service SID_SExplorerBrowserFrame = IID_ICommDlgBrowser) before acting on a double-click.</summary>
[ComImport, Guid("000214F1-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICommDlgBrowser
{
    [PreserveSig] int OnDefaultCommand(IntPtr ppshv);
    [PreserveSig] int OnStateChange(IntPtr ppshv, uint uChange);
    [PreserveSig] int IncludeObject(IntPtr ppshv, IntPtr pidl);
}

[ComImport, Guid("10339516-2894-11d2-9039-00C04F8EEB3E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICommDlgBrowser2
{
    [PreserveSig] int OnDefaultCommand(IntPtr ppshv);
    [PreserveSig] int OnStateChange(IntPtr ppshv, uint uChange);
    [PreserveSig] int IncludeObject(IntPtr ppshv, IntPtr pidl);
    [PreserveSig] int Notify(IntPtr ppshv, uint dwNotifyType);
    [PreserveSig] int GetDefaultMenuText(IntPtr ppshv, IntPtr pszText, int cchMax);
    [PreserveSig] int GetViewFlags(out uint pdwFlags);
}

[ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider
{
    [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
}

[ComImport, Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IObjectWithSite
{
    [PreserveSig] int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? pUnkSite);
    [PreserveSig] int GetSite(ref Guid riid, out IntPtr ppvSite);
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
}

[ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
    [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int GetCount(out uint pdwNumItems);
    [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
    [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
}

/// <summary>The folder view's look: colours, item height, font and theme (shobjidl.h).</summary>
[ComImport, Guid("e693cf68-d967-4112-8763-99172aee5e5a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVisualProperties
{
    [PreserveSig] int SetWatermark(IntPtr hbmp, int vpwf);
    [PreserveSig] int SetColor(int vpcf, int cr);
    [PreserveSig] int GetColor(int vpcf, out int pcr);
    [PreserveSig] int SetItemHeight(int cyItemInPixels);
    [PreserveSig] int GetItemHeight(out int cyItemInPixels);
    [PreserveSig] int SetFont(IntPtr plf, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);
    [PreserveSig] int GetFont(IntPtr plf);
    [PreserveSig] int SetTheme([MarshalAs(UnmanagedType.LPWStr)] string? pszSubAppName, [MarshalAs(UnmanagedType.LPWStr)] string? pszSubIdList);
}

[ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    // IOleWindow
    [PreserveSig] int GetWindow(out IntPtr phwnd);
    [PreserveSig] int ContextSensitiveHelp(bool fEnterMode);
    // IShellView
    [PreserveSig] int TranslateAccelerator(ref MSG pmsg);
    [PreserveSig] int EnableModeless(bool fEnable);
    [PreserveSig] int UIActivate(uint uState);
    [PreserveSig] int Refresh();
    [PreserveSig] int CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd);
    [PreserveSig] int DestroyViewWindow();
    [PreserveSig] int GetCurrentInfo(out FOLDERSETTINGS pfs);
    [PreserveSig] int AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
    [PreserveSig] int SaveViewState();
    [PreserveSig] int SelectItem(IntPtr pidlItem, uint uFlags);
    [PreserveSig] int GetItemObject(uint uItem, ref Guid riid, out IntPtr ppv);
}

[ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView2
{
    // IFolderView
    [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
    [PreserveSig] int SetCurrentViewMode(uint viewMode);
    [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
    [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
    [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
    [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetSelectionMarkedItem(out int piItem);
    [PreserveSig] int GetFocusedItem(out int piItem);
    [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
    [PreserveSig] int GetSpacing(ref POINT ppt);
    [PreserveSig] int GetDefaultSpacing(out POINT ppt);
    [PreserveSig] int GetAutoArrange();
    [PreserveSig] int SelectItem(int iItem, uint dwFlags);
    [PreserveSig] int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
    // IFolderView2
    [PreserveSig] int SetGroupBy(IntPtr key, bool fAscending);
    [PreserveSig] int GetGroupBy(IntPtr pkey, out bool pfAscending);
    [PreserveSig] int SetViewProperty(IntPtr pidl, IntPtr propkey, IntPtr propvar);
    [PreserveSig] int GetViewProperty(IntPtr pidl, IntPtr propkey, IntPtr ppropvar);
    [PreserveSig] int SetTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
    [PreserveSig] int SetExtendedTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszPropList);
    [PreserveSig] int SetText(int iType, [MarshalAs(UnmanagedType.LPWStr)] string pwszText);
    [PreserveSig] int SetCurrentFolderFlags(uint dwMask, uint dwFlags);
    [PreserveSig] int GetCurrentFolderFlags(out uint pdwFlags);
    [PreserveSig] int GetSortColumnCount(out int pcColumns);
    [PreserveSig] int SetSortColumns(IntPtr rgSortColumns, int cColumns);
    [PreserveSig] int GetSortColumns(IntPtr rgSortColumns, int cColumns);
    [PreserveSig] int GetItem(int iItem, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetVisibleItem(int iStart, bool fPrevious, out int piItem);
    [PreserveSig] int GetSelectedItem(int iStart, out int piItem);
    [PreserveSig] int GetSelection(bool fNoneImpliesFolder, out IShellItemArray ppsia);
    [PreserveSig] int GetSelectionState(IntPtr pidl, out uint pdwFlags);
    [PreserveSig] int InvokeVerbOnSelection([MarshalAs(UnmanagedType.LPStr)] string pszVerb);
    [PreserveSig] int SetViewModeAndIconSize(int uViewMode, int iImageSize);
    [PreserveSig] int GetViewModeAndIconSize(out int puViewMode, out int piImageSize);
    [PreserveSig] int SetGroupSubsetCount(uint cVisibleRows);
    [PreserveSig] int GetGroupSubsetCount(out uint pcVisibleRows);
    [PreserveSig] int SetRedraw(bool fRedrawOn);
    [PreserveSig] int IsMoveInSameFolder();
    [PreserveSig] int DoRename();
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
    public RECT(Rectangle r) { Left = r.Left; Top = r.Top; Right = r.Right; Bottom = r.Bottom; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr hwnd;
    public int message;
    public IntPtr wParam;
    public IntPtr lParam;
    public int time;
    public POINT pt;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FOLDERSETTINGS
{
    public int ViewMode;
    public uint fFlags;
}

[Flags]
internal enum EXPLORER_BROWSER_OPTIONS : uint
{
    None = 0,
    NavigateOnce = 0x1,
    ShowFrames = 0x2,
    AlwaysNavigate = 0x4,
    NoTravelLog = 0x8,
    NoWrapperWindow = 0x10,
    HtmlSharePointView = 0x20,
    NoBorder = 0x40,
    NoPersistViewState = 0x80,
}

internal enum SIGDN : uint
{
    NormalDisplay = 0,
    ParentRelativeParsing = 0x80018001,
    DesktopAbsoluteParsing = 0x80028000,
    ParentRelativeEditing = 0x80031001,
    DesktopAbsoluteEditing = 0x8004c000,
    FileSysPath = 0x80058000,
    Url = 0x80068000,
}

internal static class ShellNative
{
    public const int S_OK = 0, S_FALSE = 1;
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const uint CDB2GVF_NOSELECTVERB = 0x8, CDB2GVF_NOINCLUDEITEM = 0x10;
    public const uint CDBOSC_SETFOCUS = 0, CDBOSC_SELCHANGE = 2;
    public const int SIATTRIBFLAGS_AND = 0x1, SIATTRIBFLAGS_OR = 0x2;

    public const uint SBSP_ABSOLUTE = 0x0;
    public const uint SBSP_PARENT = 0x2000;

    public const int FVM_AUTO = -1, FVM_ICON = 1, FVM_SMALLICON = 2, FVM_LIST = 3, FVM_DETAILS = 4, FVM_THUMBNAIL = 5, FVM_TILE = 6, FVM_CONTENT = 8;
    public const uint FWF_NOWEBVIEW = 0x10000, FWF_SHOWSELALWAYS = 0x2000, FWF_NOHEADERINALLVIEWS = 0x1000000; // 0x40 is FWF_SINGLESEL

    public const uint SVSI_DESELECT = 0x0, SVSI_SELECT = 0x1, SVSI_EDIT = 0x3, SVSI_DESELECTOTHERS = 0x4, SVSI_ENSUREVISIBLE = 0x8, SVSI_FOCUSED = 0x10;

    public const uint EPS_DEFAULT_ON = 0x1, EPS_DEFAULT_OFF = 0x2, EPS_FORCE = 0x20000;

    public static readonly Guid CLSID_ExplorerBrowser = new("71f96385-ddd6-48d3-a0c1-ae06e8b055fb");
    public static readonly Guid IID_IShellView = new("000214E3-0000-0000-C000-000000000046");
    public static readonly Guid IID_IFolderView2 = new("1af3a467-214f-4298-908e-06b03e0b39f9");
    public static readonly Guid IID_IVisualProperties = new("e693cf68-d967-4112-8763-99172aee5e5a");
    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    public static readonly Guid IID_IExplorerPaneVisibility = new("e07010ec-bc17-44c0-97b0-46c7c95b9edc");
    public static readonly Guid IID_ICommDlgBrowser = new("000214F1-0000-0000-C000-000000000046"); // also SID_SExplorerBrowserFrame
    public static readonly Guid IID_ICommDlgBrowser2 = new("10339516-2894-11d2-9039-00C04F8EEB3E");

    // Explorer panes (shlguid.h)
    public static readonly Guid EP_NavPane = new("cb316b22-25f7-42b8-8a09-540d23a43c2f");
    public static readonly Guid EP_Commands = new("d9745868-ca5f-4a76-91cd-f5a129fbb076");
    public static readonly Guid EP_Commands_Organize = new("72e81700-e3ec-4660-bf24-3c3b7b648806");
    public static readonly Guid EP_Commands_View = new("21f7c32d-eeaa-439b-bb51-37b96fd6a943");
    public static readonly Guid EP_DetailsPane = new("43abf98b-89b8-472d-b9ce-e69b8229f019");
    public static readonly Guid EP_PreviewPane = new("893c63d1-45c8-4d17-be19-223be71be365");
    public static readonly Guid EP_QueryPane = new("65bcde4f-4f07-4f27-83a7-1afca4df7ddd");
    public static readonly Guid EP_AdvQueryPane = new("b4e9db8b-34ba-4c39-b5cc-16a1bd2c411c");
    public static readonly Guid EP_StatusBar = new("65fe56ce-5cfe-4bc4-ad8a-7ae3fe7e8f7c");
    public static readonly Guid EP_Ribbon = new("d27524a8-c9f2-4834-a106-df8889fd4f37");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHGetNameFromIDList(IntPtr pidl, SIGDN sigdnName, out IntPtr ppszName);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, out IntPtr ppidl);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern uint ILGetSize(IntPtr pidl);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern void ILFree(IntPtr pidl);

    [DllImport("ole32.dll", PreserveSig = true)]
    public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    /// <summary>Takes ownership of a CoTaskMem string returned by the shell.</summary>
    public static string? TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static string? NameOf(IntPtr pidl, SIGDN kind)
        => pidl != IntPtr.Zero && SHGetNameFromIDList(pidl, kind, out var p) == S_OK ? TakeString(p) : null;

    public static string? NameOf(IShellItem item, SIGDN kind)
        => item.GetDisplayName(kind, out var p) == S_OK ? TakeString(p) : null;

    public static byte[] CopyPidl(IntPtr pidl)
    {
        int size = (int)ILGetSize(pidl);
        var bytes = new byte[size];
        Marshal.Copy(pidl, bytes, 0, size);
        return bytes;
    }
}

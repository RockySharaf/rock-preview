using System.Runtime.InteropServices;

namespace Anything.Explorer;

// COM declarations for the command bar's View, Sort, New folder and the status bar's selection size (see
// ExplorerHost.Commands.cs). GUIDs, vtable order and struct layouts are from the Windows SDK (ShObjIdl_core.h,
// propsys.h, 10.0.26100); interfaces that are only partly used declare their methods up to the last one used.

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;
    public PROPERTYKEY(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
}

/// <summary>A column of the folder view, as IColumnManager describes it (CM_COLUMNINFO).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CM_COLUMNINFO
{
    public int cbSize;
    public uint dwMask;
    public uint dwState;
    public uint uWidth;
    public uint uDefaultWidth;
    public uint uIdealWidth;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string wszName;
}

/// <summary>The folder view's columns (the view implements it beside IShellView).</summary>
[ComImport, Guid("d8ec27bb-3f3b-4042-b10a-4acfd924d453"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IColumnManager
{
    [PreserveSig] int SetColumnInfo(ref PROPERTYKEY propkey, ref CM_COLUMNINFO pcmci);
    [PreserveSig] int GetColumnInfo(ref PROPERTYKEY propkey, ref CM_COLUMNINFO pcmci);
    [PreserveSig] int GetColumnCount(uint dwFlags, out uint puCount);
    [PreserveSig] int GetColumns(uint dwFlags, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] PROPERTYKEY[] rgkeyOrder, uint cColumns);
}

/// <summary>A property's description: here only its view flags (PDVF_SORTDESCENDING: the column sorts descending first).</summary>
[ComImport, Guid("6f79d558-3e96-4549-a1d1-7d75d2288814"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyDescription
{
    [PreserveSig] int GetPropertyKey(out PROPERTYKEY pkey);
    [PreserveSig] int GetCanonicalName(out IntPtr ppszName);
    [PreserveSig] int GetPropertyType(out ushort pvartype);
    [PreserveSig] int GetDisplayName(out IntPtr ppszName);
    [PreserveSig] int GetEditInvitation(out IntPtr ppszInvite);
    [PreserveSig] int GetTypeFlags(uint mask, out uint ppdtFlags);
    [PreserveSig] int GetViewFlags(out uint ppdvFlags);
}

/// <summary>IShellItem2, up to GetPropertyStore: the item's properties without opening the file (GPS_FASTPROPERTIESONLY).</summary>
[ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem2
{
    // IShellItem
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IntPtr ppsi);
    [PreserveSig] int GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
    // IShellItem2
    [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public ushort vt;
    public ushort r1, r2, r3;
    public IntPtr p1, p2;
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint cProps);
    [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
    [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
}

/// <summary>IFileOperation, up to PerformOperations: a new folder made the way Explorer makes one, with an undo record.</summary>
[ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    [PreserveSig] int Advise(IntPtr pfops, out uint pdwCookie);
    [PreserveSig] int Unadvise(uint dwCookie);
    [PreserveSig] int SetOperationFlags(uint dwOperationFlags);
    [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
    [PreserveSig] int SetProgressDialog(IntPtr popd);
    [PreserveSig] int SetProperties(IntPtr pproparray);
    [PreserveSig] int SetOwnerWindow(IntPtr hwndOwner);
    [PreserveSig] int ApplyPropertiesToItem(IntPtr psiItem);
    [PreserveSig] int ApplyPropertiesToItems(IntPtr punkItems);
    [PreserveSig] int RenameItem(IntPtr psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
    [PreserveSig] int RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
    [PreserveSig] int MoveItem(IntPtr psiItem, IntPtr psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsItem);
    [PreserveSig] int MoveItems(IntPtr punkItems, IntPtr psiDestinationFolder);
    [PreserveSig] int CopyItem(IntPtr psiItem, IntPtr psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IntPtr pfopsItem);
    [PreserveSig] int CopyItems(IntPtr punkItems, IntPtr psiDestinationFolder);
    [PreserveSig] int DeleteItem(IntPtr psiItem, IntPtr pfopsItem);
    [PreserveSig] int DeleteItems(IntPtr punkItems);
    [PreserveSig] int NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                              [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IntPtr pfopsItem);
    [PreserveSig] int PerformOperations();
    [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
}

internal static class CommandsNative
{
    public const uint SVGIO_BACKGROUND = 0x0;
    public const uint CM_ENUM_VISIBLE = 0x2, CM_MASK_NAME = 0x8;
    public const uint PDVF_SORTDESCENDING = 0x10;
    public const int GPS_FASTPROPERTIESONLY = 0x8;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FOF_SILENT = 0x4, FOF_NOERRORUI = 0x400, FOF_NOCONFIRMMKDIR = 0x200, FOFX_ADDUNDORECORD = 0x20000000;

    public static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");
    public static readonly Guid IID_IPropertyDescription = new("6f79d558-3e96-4549-a1d1-7d75d2288814");
    public static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    public static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");

    /// <summary>System.Size (propkey.h).</summary>
    public static readonly PROPERTYKEY PKEY_Size = new(new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), 12);

    [DllImport("propsys.dll")] public static extern int PSGetPropertyDescription(ref PROPERTYKEY propkey, ref Guid riid, out IntPtr ppv);
    [DllImport("propsys.dll")] public static extern int PropVariantToUInt64(ref PROPVARIANT propvar, out ulong pullRet);
    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT pvar);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr hMenu, int nPos);
    [DllImport("user32.dll")] public static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);
}

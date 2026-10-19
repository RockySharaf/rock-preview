using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Anything;

/// <summary>.lnk files through the shell's own ShellLink object, and Rock Preview's taskbar identity.</summary>
internal static class Shortcuts
{
    /// <summary>
    /// Rock Preview's AppUserModelID. The process and its shortcuts carry it, so the taskbar groups every Rock Preview
    /// window under one button, including the File Explorer button it takes over.
    /// </summary>
    /// <summary>
    /// Rock Preview's AppUserModelID: its windows, shortcuts and notifications share it. Windows shows the name of the
    /// Start menu shortcut carrying it ("Rock Preview"), or this ID itself when there is none (the portable copy).
    /// </summary>
    public const string AppId = "RockPreview";

    const uint SLDF_RUNAS_USER = 0x2000;

    /// <summary>Writes a shortcut to <paramref name="exe"/> (replacing <paramref name="lnk"/> if it exists).</summary>
    public static void Create(string lnk, string exe, string arguments, string description, bool runAsAdmin = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var link = (IShellLinkW)new ShellLinkObject();
        try
        {
            link.SetPath(exe);
            if (arguments.Length > 0) link.SetArguments(arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetDescription(description);
            link.SetIconLocation(exe, 0);
            if (link is IShellLinkDataList data && data.GetFlags(out uint flags) >= 0)
                data.SetFlags(runAsAdmin ? flags | SLDF_RUNAS_USER : flags & ~SLDF_RUNAS_USER);
            if (link is IPropertyStore store) SetString(store, PKEY_AppUserModel_ID, AppId);
            ((IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    /// <summary>A shortcut's target file, its arguments, and the shell's parsing name for the target ("::{CLSID}" for shell objects).</summary>
    public static (string Path, string Arguments, string? ParsingName)? Read(string lnk)
    {
        object? link = null;
        try
        {
            link = new ShellLinkObject();
            ((IPersistFile)link).Load(lnk, 0);
            var shellLink = (IShellLinkW)link;
            var path = new StringBuilder(1024);
            shellLink.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(2048);
            shellLink.GetArguments(args, args.Capacity);
            string? parsing = null;
            shellLink.GetIDList(out IntPtr pidl);
            if (pidl != IntPtr.Zero)
            {
                try
                {
                    if (SHGetNameFromIDList(pidl, 0x80028000 /* SIGDN_DESKTOPABSOLUTEPARSING */, out IntPtr name) == 0)
                    {
                        parsing = Marshal.PtrToStringUni(name);
                        Marshal.FreeCoTaskMem(name);
                    }
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
            return (path.ToString(), args.ToString(), parsing);
        }
        catch (Exception) { return null; }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }

    /// <summary>The AppUserModelID a shortcut carries, or null.</summary>
    public static string? ReadAppId(string lnk)
    {
        object? link = null;
        try
        {
            link = new ShellLinkObject();
            ((IPersistFile)link).Load(lnk, 0 /* STGM_READ */);
            if (link is not IPropertyStore store) return null;
            var key = PKEY_AppUserModel_ID;
            if (store.GetValue(ref key, out var pv) < 0) return null;
            try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.pointer) : null; }
            finally { PropVariantClear(ref pv); }
        }
        catch (Exception) { return null; }
        finally { if (link != null) Marshal.ReleaseComObject(link); }
    }

    /// <summary>Gives this process Rock Preview's AppUserModelID (before any window is created).</summary>
    public static void UseAppIdForThisProcess()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppId); } catch (Exception) { }
    }

    static readonly PropertyKey PKEY_AppUserModel_ID = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

    static void SetString(IPropertyStore store, PropertyKey key, string value)
    {
        var pv = new PropVariant { vt = 31 /* VT_LPWSTR */, pointer = Marshal.StringToCoTaskMemUni(value) };
        try
        {
            if (store.SetValue(ref key, ref pv) >= 0) store.Commit();
        }
        finally { Marshal.FreeCoTaskMem(pv.pointer); }
    }

    // ---- COM -------------------------------------------------------------------------------------

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLinkObject { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("45e2b4ae-b1c3-11d0-b92f-00a0c90312e1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkDataList
    {
        [PreserveSig] int AddDataBlock(IntPtr pDataBlock);
        [PreserveSig] int CopyDataBlock(uint dwSig, out IntPtr ppDataBlock);
        [PreserveSig] int RemoveDataBlock(uint dwSig);
        [PreserveSig] int GetFlags(out uint pdwFlags);
        [PreserveSig] int SetFlags(uint dwFlags);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey { public Guid fmtid; public uint pid; }

    /// <summary>PROPVARIANT, only as a VT_LPWSTR here (24 bytes on x64).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdnName, out IntPtr ppszName);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)] static extern void SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);
}

using System.Runtime.InteropServices;

namespace Anything.Interop;

/// <summary>
/// Renames through the shell's own copy engine (IFileOperation), as File Explorer does: the shell asks about a name
/// that's taken (offering "Name (2)"), shows its own errors and elevation prompts, and puts the rename on its undo
/// list. Deletes go the same way. GUIDs, flags and the vtable order are from the Windows SDK (ShObjIdl_core.h,
/// shellapi.h, ShlObj_core.h, 10.0.26100).
/// </summary>
internal static class ShellFileOperation
{
    public enum Outcome
    {
        /// <summary>Renamed.</summary>
        Done,
        /// <summary>Not renamed, and the shell has already told the user why (or they cancelled its prompt).</summary>
        NotDone,
        /// <summary>Not renamed before the shell was asked (the item couldn't be found): the caller says so.</summary>
        Failed,
        /// <summary>The copy engine couldn't be created: the caller renames the old way.</summary>
        Unavailable,
    }

    /// <summary>Renames <paramref name="fullPath"/> to <paramref name="newName"/> in its folder; <paramref name="error"/> is the HRESULT of a failure.</summary>
    public static Outcome Rename(IntPtr owner, string fullPath, string newName, out int error)
    {
        error = 0;
        var clsid = CLSID_FileOperation;
        var iid = IID_IFileOperation;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out IntPtr opPtr) != S_OK || opPtr == IntPtr.Zero)
            return Outcome.Unavailable;
        IntPtr itemPtr = IntPtr.Zero;
        IFileOperation? op = null;
        try
        {
            op = (IFileOperation)Marshal.GetObjectForIUnknown(opPtr);
            var iidItem = IID_IShellItem;
            int hr = SHCreateItemFromParsingName(fullPath, IntPtr.Zero, ref iidItem, out itemPtr);
            if (hr != S_OK || itemPtr == IntPtr.Zero) { error = hr; return Outcome.Failed; }
            if ((hr = op.SetOperationFlags(FOF_ALLOWUNDO | FOFX_ADDUNDORECORD)) < 0
                || (hr = op.SetOwnerWindow(owner)) < 0
                || (hr = op.RenameItem(itemPtr, newName, IntPtr.Zero)) < 0)
            {
                error = hr;
                return Outcome.Failed;
            }
            hr = op.PerformOperations();
            bool aborted = op.GetAnyOperationsAborted(out bool any) >= 0 && any;
            if (hr >= 0 && !aborted) return Outcome.Done;
            error = hr;
            return Outcome.NotDone; // its own error box or prompt was shown (or cancelled): nothing more to say
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            error = e.HResult;
            return Outcome.Failed;
        }
        finally
        {
            if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr);
            if (op != null) Marshal.ReleaseComObject(op);
            Marshal.Release(opPtr);
        }
    }

    /// <summary>
    /// Deletes <paramref name="paths"/> the way File Explorer's Delete does: to the Recycle Bin (on the undo list), or
    /// for <paramref name="permanent"/> (Shift+Delete) for good. The shell asks first as the user set it up (the Recycle
    /// Bin's "Display delete confirmation dialog"; always for a permanent delete), and warns once when an item can't go to
    /// the Recycle Bin (a network share, too big). <paramref name="error"/> is the HRESULT of a failure.
    /// <paramref name="skipped"/> lists the paths that still exist but couldn't be handed to the shell (a share that's
    /// down, say), with the reason: they were left alone, and the caller says so. Paths already gone are left out quietly.
    /// </summary>
    public static Outcome Delete(IntPtr owner, IReadOnlyList<string> paths, bool permanent, out int error, out List<(string Path, int Error)> skipped)
    {
        error = 0;
        skipped = [];
        if (paths.Count == 0) return Outcome.NotDone;
        var clsid = CLSID_FileOperation;
        var iid = IID_IFileOperation;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out IntPtr opPtr) != S_OK || opPtr == IntPtr.Zero)
            return Outcome.Unavailable;
        var pidls = new List<IntPtr>(paths.Count);
        IntPtr arrayPtr = IntPtr.Zero;
        IFileOperation? op = null;
        try
        {
            op = (IFileOperation)Marshal.GetObjectForIUnknown(opPtr);
            foreach (string p in paths)
            {
                int parsed = SHParseDisplayName(p, IntPtr.Zero, out IntPtr pidl, 0, out _);
                if (parsed >= 0 && pidl != IntPtr.Zero) { pidls.Add(pidl); continue; }
                if (pidl != IntPtr.Zero) ILFree(pidl);
                if (parsed >= 0) parsed = unchecked((int)0x80004005); // E_FAIL: no ID list and no reason
                // Gone already (deleted elsewhere): nothing to delete. Anything else was not deleted, and is reported.
                if (!Anything.Core.FileOpRules.IsGone(parsed, File.Exists(p) || Directory.Exists(p))) skipped.Add((p, parsed));
            }
            if (pidls.Count == 0)
            {
                error = skipped.Count > 0 ? skipped[0].Error : unchecked((int)0x80070002); // ERROR_FILE_NOT_FOUND
                return Outcome.Failed;
            }
            int hr = SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out arrayPtr);
            if (hr != S_OK || arrayPtr == IntPtr.Zero) { error = hr; return Outcome.Failed; }
            // Never FOF_NOCONFIRMATION or FOF_SILENT: the shell's own confirmation and progress, as in Explorer.
            uint flags = permanent ? FOF_WANTNUKEWARNING : FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE | FOFX_ADDUNDORECORD | FOF_WANTNUKEWARNING;
            if ((hr = op.SetOperationFlags(flags)) < 0
                || (hr = op.SetOwnerWindow(owner)) < 0
                || (hr = op.DeleteItems(arrayPtr)) < 0)
            {
                error = hr;
                return Outcome.Failed;
            }
            hr = op.PerformOperations();
            bool aborted = op.GetAnyOperationsAborted(out bool any) >= 0 && any;
            if (hr >= 0 && !aborted) return Outcome.Done;
            error = hr;
            return Outcome.NotDone; // cancelled at the shell's question, or its own error box was shown
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            error = e.HResult;
            return Outcome.Failed;
        }
        finally
        {
            if (arrayPtr != IntPtr.Zero) Marshal.Release(arrayPtr);
            foreach (IntPtr pidl in pidls) ILFree(pidl);
            if (op != null) Marshal.ReleaseComObject(op);
            Marshal.Release(opPtr);
        }
    }

    const int S_OK = 0;
    const uint CLSCTX_ALL = 0x17; // CLSCTX_INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER
    const uint FOF_ALLOWUNDO = 0x0040;
    const uint FOF_WANTNUKEWARNING = 0x4000;
    const uint FOFX_RECYCLEONDELETE = 0x00080000;
    const uint FOFX_ADDUNDORECORD = 0x20000000;

    static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    static readonly Guid IID_IFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation
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
        [PreserveSig] int MoveItem(IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName, IntPtr pfopsItem);
        [PreserveSig] int MoveItems(IntPtr punkItems, IntPtr psiDestinationFolder);
        [PreserveSig] int CopyItem(IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszCopyName, IntPtr pfopsItem);
        [PreserveSig] int CopyItems(IntPtr punkItems, IntPtr psiDestinationFolder);
        [PreserveSig] int DeleteItem(IntPtr psiItem, IntPtr pfopsItem);
        [PreserveSig] int DeleteItems(IntPtr punkItems);
        [PreserveSig] int NewItem(IntPtr psiDestinationFolder, uint dwFileAttributes, IntPtr pszName, IntPtr pszTemplateName, IntPtr pfopsItem);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }

    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    static extern int SHCreateShellItemArrayFromIDLists(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] rgpidl, out IntPtr ppsiItemArray);
}

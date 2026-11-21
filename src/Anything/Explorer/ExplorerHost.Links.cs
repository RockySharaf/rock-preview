using System.Runtime.InteropServices;
using System.Text;
using Anything.Core;

namespace Anything.Explorer;

/// <summary>
/// Shortcuts to folders: double-clicked (or Enter), File Explorer opens the folder in the same tab. Left to the
/// shortcut's own default action, it could open a File Explorer window, or another Rock Preview one.
/// </summary>
internal sealed partial class ExplorerHost
{
    static readonly Guid BHID_SFUIObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");

    /// <summary>How long a double-click waits to learn whether a shortcut's folder is there (a share that is down never says).</summary>
    static readonly TimeSpan ShortcutTargetWait = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The view's default command on one selected shortcut whose target is a folder (or a place like This PC or a
    /// library): the tab opens the target. A shortcut to a file, a broken one, one whose folder doesn't answer in a moment,
    /// or more than one item: false, and the view does what it always did.
    /// </summary>
    bool OpenShortcutFolder()
    {
        if (ShortcutFolderTarget() is not { } target) return false;
        BeginInvoke(() => // not inside the view's own callback
        {
            if (target.Path != null) Navigate(target.Path);
            else if (target.Pidl != null) Navigate(target.Pidl, historyNavigation: false);
        });
        return true;
    }

    /// <summary>The selected shortcut's folder, by its path or (a place not on disk) by its item; null when there is none to open.</summary>
    (string? Path, byte[]? Pidl)? ShortcutFolderTarget()
    {
        var fv = GetView<IFolderView2>(ShellNative.IID_IFolderView2);
        if (fv == null) return null;
        IShellLinkW? link = null;
        try
        {
            if (fv.GetSelection(false, out var items) != ShellNative.S_OK || items == null) return null;
            try
            {
                if (items.GetCount(out uint n) != ShellNative.S_OK || n != 1) return null;
                if (items.GetItemAt(0, out var item) != ShellNative.S_OK || item == null) return null;
                try
                {
                    // From the attributes the view holds: a folder or file selected is never read for this.
                    if (item.GetAttributes(FolderTabs.SfgaoLink, out uint attrs) < 0 || !FolderTabs.IsSingleShortcut((int)n, attrs)) return null;
                    var bhid = BHID_SFUIObject;
                    var iid = typeof(IShellLinkW).GUID;
                    if (item.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var ptr) != ShellNative.S_OK || ptr == IntPtr.Zero) return null;
                    try { link = Marshal.GetObjectForIUnknown(ptr) as IShellLinkW; }
                    finally { Marshal.Release(ptr); }
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            finally { Marshal.ReleaseComObject(items); }
            return link == null ? null : FolderTarget(link);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException) { return null; }
        finally
        {
            if (link != null) Marshal.ReleaseComObject(link);
            Marshal.ReleaseComObject(fv);
        }
    }

    /// <summary>
    /// Where a shortcut points, when that is a folder. Read as the shortcut stores it: the shortcut isn't resolved, so no
    /// window asks to look for a moved target and nothing searches the disk for one.
    /// </summary>
    static (string? Path, byte[]? Pidl)? FolderTarget(IShellLinkW link)
    {
        var buffer = new StringBuilder(1024);
        if (link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0) == ShellNative.S_OK && buffer.Length > 0)
        {
            string path = Environment.ExpandEnvironmentVariables(buffer.ToString());
            // A folder on a share that is down keeps Directory.Exists waiting: after a moment the shortcut is the view's.
            return FolderTabs.Within(() => Directory.Exists(path), ShortcutTargetWait) == true ? (path, null) : null;
        }
        // No path: a place that isn't on disk (This PC, a library, a phone), opened by its item.
        if (link.GetIDList(out var pidl) != ShellNative.S_OK || pidl == IntPtr.Zero) return null;
        try
        {
            var iid = ShellNative.IID_IShellItem;
            if (ShellNative.SHCreateItemFromIDList(pidl, ref iid, out var target) != ShellNative.S_OK || target == null) return null;
            try
            {
                const uint mask = ShellSelection.SfgaoFolder | ShellSelection.SfgaoStream;
                return target.GetAttributes(mask, out uint attrs) >= 0 && FolderTabs.CanOpenInTab(attrs) ? (null, ShellNative.CopyPidl(pidl)) : null;
            }
            finally { Marshal.ReleaseComObject(target); }
        }
        finally { ShellNative.ILFree(pidl); }
    }

    /// <summary>IShellLinkW (ShObjIdl_core.h), up to the two methods used here.</summary>
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        [PreserveSig] int GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
    }
}

using System.Runtime.InteropServices;
using Anything.Core;
using Microsoft.Win32;

namespace Anything.Explorer;

/// <summary>
/// The roots File Explorer lists in its pane after OneDrive: other cloud storage (Dropbox, Google Drive, iCloud Drive,
/// another OneDrive account) and a linked phone. They are the shell's roots on the desktop (Explorer\Desktop\NameSpace,
/// the user's and the machine's) whose class is pinned to the pane (System.IsPinnedToNameSpaceTree), read from the
/// registry: nothing on the network is asked.
/// </summary>
internal sealed partial class NavigationTree
{
    const string NameSpaceKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace";

    /// <summary>
    /// Adds the pinned roots the pane doesn't add itself, in Explorer's order. One that doesn't open (its provider was
    /// uninstalled, it fails to load) is left out without a word.
    /// </summary>
    void AddProviderRoots(SHCONTF flags)
    {
        var extra = FolderTabs.ExtraRoots(ReadNameSpaceRoots(), FolderTabs.PaneRoots);
        if (extra.Count == 0 || _tree == null) return;
        // The folders of the roots there already, and OneDrive's though it was taken out: the root its app registers for
        // the same account is the same folder, and would bring it back under another name.
        var folders = _roots.Select(FolderOf).ToList();
        if (Hidden("onedrive") && CreateItem("shell:OneDrive") is { } oneDrive)
        {
            try { folders.Add(FolderOf(oneDrive)); }
            finally { Marshal.ReleaseComObject(oneDrive); }
        }
        foreach (var clsid in extra)
        {
            var item = CreateItem("::{" + clsid.ToString("D") + "}");
            if (item == null) continue;
            bool added = false;
            try
            {
                string? folder = FolderOf(item);
                added = FolderTabs.IsExtraRootFolder(folder, folders) && IsUsableRoot(item)
                        && _tree.AppendRoot(item, flags, NSTCROOTSTYLE.VISIBLE, IntPtr.Zero) == NavTreeNative.S_OK;
                if (added) folders.Add(folder);
            }
            catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException) { added = false; }
            if (added) _roots.Add(item);
            else Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>The item's folder on disk, or null for a place that isn't one (Home, a phone).</summary>
    static string? FolderOf(IShellItem item)
    {
        try { return ShellNative.NameOf(item, SIGDN.FileSysPath); }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException) { return null; }
    }

    /// <summary>The roots registered on the desktop, the user's first, with what their classes say about the pane.</summary>
    static List<FolderTabs.NameSpaceRoot> ReadNameSpaceRoots()
    {
        var roots = new List<FolderTabs.NameSpaceRoot>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var ns = hive.OpenSubKey(NameSpaceKey);
                if (ns == null) continue;
                foreach (var name in ns.GetSubKeyNames())
                {
                    if (!Guid.TryParse(name, out var clsid) || FolderTabs.PaneRoots.Contains(clsid)) continue;
                    roots.Add(new(name,
                        ClassValue(Registry.CurrentUser, name, "System.IsPinnedToNameSpaceTree"), ClassValue(Registry.LocalMachine, name, "System.IsPinnedToNameSpaceTree"),
                        ClassValue(Registry.CurrentUser, name, "SortOrderIndex"), ClassValue(Registry.LocalMachine, name, "SortOrderIndex")));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return roots;
    }

    /// <summary>A value of a shell class, as the user's or the machine's classes set it (null when unset).</summary>
    static object? ClassValue(RegistryKey hive, string clsid, string value)
    {
        try
        {
            using var key = hive.OpenSubKey(@"Software\Classes\CLSID\" + clsid);
            return key?.GetValue(value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }
}

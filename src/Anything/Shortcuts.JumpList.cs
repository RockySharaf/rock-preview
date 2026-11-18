using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Anything;

/// <summary>
/// The taskbar button's jump list (right-click it): the folders and files opened lately, as File Explorer's shows
/// them, the newest at the bottom, nearest the button. A folder opens in Rock Preview, a file in its own app.
/// </summary>
internal static partial class Shortcuts
{
    /// <summary>How many recent items the jump list shows.</summary>
    public const int JumpListItems = 15;

    /// <summary>
    /// Writes the jump list's Recent group: <paramref name="recent"/> newest first (as Windows' Recent folder has them),
    /// shown oldest first so the newest is at the bottom. Items the user removed from the list stay out. False when
    /// Windows refused it.
    /// </summary>
    public static bool UpdateJumpList(IReadOnlyList<string> recent, string exe)
    {
        ICustomDestinationList? list = null;
        try
        {
            list = (ICustomDestinationList)new DestinationListObject();
            if (!StorePackage.IsInstalled) list.SetAppID(AppId); // the Store's copy's taskbar button is its package's
            var iidArray = typeof(IObjectArray).GUID;
            list.BeginList(out _, ref iidArray, out object removedObj);
            var removed = Targets(removedObj as IObjectArray);
            var items = (IObjectCollection)new EnumerableObjectCollectionObject();
            int added = 0;
            foreach (string path in recent.Take(JumpListItems).Reverse())
            {
                bool folder = Directory.Exists(path);
                string target = folder ? exe : path, args = folder ? "\"" + path.TrimEnd('\\') + (path.Length <= 3 ? "\\\\" : "") + "\"" : "";
                if (removed.Contains(target + "|" + args)) continue;
                var link = (IShellLinkW)new ShellLinkObject();
                link.SetPath(target);
                if (args.Length > 0) link.SetArguments(args);
                link.SetDescription(path);
                if (folder) link.SetIconLocation(Path.Combine(Environment.SystemDirectory, "imageres.dll"), 3);
                string name = Path.GetFileName(path.TrimEnd('\\'));
                if (link is IPropertyStore store) SetString(store, PKEY_Title, name.Length > 0 ? name : path);
                items.AddObject(link);
                added++;
            }
            if (added > 0) list.AppendCategory("Recent", (IObjectArray)items);
            list.CommitList();
            return true;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException or FileNotFoundException)
        {
            try { list?.AbortList(); } catch (COMException) { }
            return false;
        }
        finally
        {
            if (list != null) Marshal.ReleaseComObject(list);
        }
    }

    /// <summary>"target|arguments" of each link the user removed from the jump list.</summary>
    static HashSet<string> Targets(IObjectArray? array)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (array == null || array.GetCount(out uint n) < 0) return set;
        var iid = typeof(IShellLinkW).GUID;
        for (uint i = 0; i < n; i++)
        {
            if (array.GetAt(i, ref iid, out object o) < 0 || o is not IShellLinkW link) continue;
            var path = new StringBuilder(1024);
            var args = new StringBuilder(2048);
            try
            {
                link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
                link.GetArguments(args, args.Capacity);
                set.Add(path + "|" + args);
            }
            catch (COMException) { }
        }
        return set;
    }

    static readonly PropertyKey PKEY_Title = new() { fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), pid = 2 };

    [ComImport, Guid("77f10cf0-3db5-4966-b520-b7c54fd35ed6")]
    class DestinationListObject { }

    [ComImport, Guid("2d3468c1-36a7-43b6-ac24-d3f02fd9607a")]
    class EnumerableObjectCollectionObject { }

    [ComImport, Guid("6332debf-87b5-4670-90c0-5e57b408a49e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void BeginList(out uint pcMinSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray poa);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string? pszAppID);
        void AbortList();
    }

    [ComImport, Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint pcObjects);
        [PreserveSig] int GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, Guid("5632b1a4-e38a-400a-928a-d4cd63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectCollection
    {
        // IObjectArray
        [PreserveSig] int GetCount(out uint pcObjects);
        [PreserveSig] int GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        // IObjectCollection
        void AddObject([MarshalAs(UnmanagedType.Interface)] object punk);
        void AddFromArray(IObjectArray poaSource);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }
}

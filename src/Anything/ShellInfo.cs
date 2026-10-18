using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Anything.Interop;

namespace Anything;

/// <summary>
/// Shell icon (system image list) and type-name lookups, cached per extension. Files whose icon is
/// embedded in the file itself (exe, ico, lnk, ...) get their real icon resolved lazily on a background STA thread.
/// </summary>
internal static class ShellInfo
{
    static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "ico", "lnk", "cur", "ani", "url", "msc", "scr", "cpl", "appref-ms", "website",
    };

    static readonly Dictionary<string, int> s_extIcons = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> s_typeNames = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, int> s_realIcons = new(StringComparer.OrdinalIgnoreCase);
    static readonly BlockingCollection<string> s_queue = new(new ConcurrentQueue<string>(), 4096);
    static readonly ConcurrentDictionary<string, byte> s_pending = new(StringComparer.OrdinalIgnoreCase);
    static int s_folderIcon = -1;
    static string? s_folderType;
    static Thread? s_worker;

    /// <summary>Raised (on a background thread) when real icons were resolved; listeners should repaint.</summary>
    public static event Action? IconsResolved;

    public static IntPtr SmallImageList { get; private set; }
    public static Size SmallIconSize { get; private set; } = new(16, 16);

    public static void Initialize()
    {
        if (SmallImageList != IntPtr.Zero) return;
        var info = new Native.SHFILEINFO();
        SmallImageList = Native.SHGetFileInfo(".txt", Native.FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(info),
            Native.SHGFI_SYSICONINDEX | Native.SHGFI_SMALLICON | Native.SHGFI_USEFILEATTRIBUTES);
        if (SmallImageList != IntPtr.Zero && Native.ImageList_GetIconSize(SmallImageList, out int cx, out int cy))
            SmallIconSize = new Size(cx, cy);
    }

    static int QueryIndex(string path, uint attrs, bool useAttrs)
    {
        var info = new Native.SHFILEINFO();
        uint flags = Native.SHGFI_SYSICONINDEX | Native.SHGFI_SMALLICON | (useAttrs ? Native.SHGFI_USEFILEATTRIBUTES : 0);
        IntPtr r = Native.SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf(info), flags);
        return r == IntPtr.Zero ? -1 : info.iIcon;
    }

    /// <summary>Icon index in the system image list. <paramref name="fullPath"/> is only used for per-file icons.</summary>
    public static int GetIconIndex(string extension, bool isDirectory, bool isRoot, Func<string> fullPath)
    {
        if (isDirectory && !isRoot)
        {
            if (s_folderIcon < 0) s_folderIcon = QueryIndex("folder", Native.FILE_ATTRIBUTE_DIRECTORY, true);
            return s_folderIcon;
        }
        if (isRoot || PerFileIconExtensions.Contains(extension))
        {
            string path = fullPath();
            if (s_realIcons.TryGetValue(path, out int real)) return real;
            Enqueue(path);
            if (isRoot)
            {
                if (s_folderIcon < 0) s_folderIcon = QueryIndex("folder", Native.FILE_ATTRIBUTE_DIRECTORY, true);
                return s_folderIcon;
            }
        }
        if (!s_extIcons.TryGetValue(extension, out int idx))
        {
            idx = QueryIndex(extension.Length == 0 ? "file" : "file." + extension, Native.FILE_ATTRIBUTE_NORMAL, true);
            s_extIcons[extension] = idx;
        }
        return idx;
    }

    public static string GetTypeName(string extension, bool isDirectory)
    {
        if (isDirectory)
        {
            return s_folderType ??= QueryType("folder", Native.FILE_ATTRIBUTE_DIRECTORY) is { Length: > 0 } t ? t : "File folder";
        }
        if (!s_typeNames.TryGetValue(extension, out var name))
        {
            name = QueryType(extension.Length == 0 ? "file" : "file." + extension, Native.FILE_ATTRIBUTE_NORMAL);
            if (string.IsNullOrEmpty(name)) name = extension.Length == 0 ? "File" : extension.ToUpperInvariant() + " File";
            s_typeNames[extension] = name;
        }
        return name;
    }

    static string QueryType(string path, uint attrs)
    {
        var info = new Native.SHFILEINFO();
        Native.SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf(info), Native.SHGFI_TYPENAME | Native.SHGFI_USEFILEATTRIBUTES);
        return info.szTypeName ?? "";
    }

    static void Enqueue(string path)
    {
        if (!s_pending.TryAdd(path, 0)) return;
        if (s_worker is null)
        {
            s_worker = new Thread(Worker) { IsBackground = true, Name = "Rock Preview icon resolver", Priority = ThreadPriority.BelowNormal };
            s_worker.SetApartmentState(ApartmentState.STA);
            s_worker.Start();
        }
        if (!s_queue.TryAdd(path)) s_pending.TryRemove(path, out _);
    }

    static void Worker()
    {
        int sinceNotify = 0;
        foreach (var path in s_queue.GetConsumingEnumerable())
        {
            int idx = -1;
            try { idx = QueryIndex(path, 0, false); } catch { }
            if (idx < 0)
            {
                string ext = Path.GetExtension(path).TrimStart('.');
                idx = QueryIndex(ext.Length == 0 ? "file" : "file." + ext, Native.FILE_ATTRIBUTE_NORMAL, true);
            }
            s_realIcons[path] = idx;
            s_pending.TryRemove(path, out _);
            sinceNotify++;
            if (s_queue.Count == 0 || sinceNotify >= 32)
            {
                sinceNotify = 0;
                IconsResolved?.Invoke();
            }
        }
    }
}

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Anything;

/// <summary>
/// Dates accessed for the rows on screen when they aren't indexed: read from the disk off the UI thread (a sleeping
/// disk or a slow share must not freeze the window), remembered for a while, like Everything's file information cache.
/// </summary>
internal static class DiskDates
{
    static readonly ConcurrentDictionary<string, long> s_cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, byte> s_queued = new(StringComparer.OrdinalIgnoreCase);
    static readonly BlockingCollection<string> s_queue = new(new ConcurrentQueue<string>(), 4096);
    static Thread? s_worker;
    static long s_cachedAt = Environment.TickCount64;

    /// <summary>Raised (on the reader thread) after dates came in; windows repaint their rows.</summary>
    public static event Action? Resolved;

    /// <summary>The date accessed of <paramref name="path"/> if read already (0 = none); else queues it and returns false.</summary>
    public static bool TryGet(string path, out long accessed)
    {
        // Dates change: what was read is kept a minute.
        if (Environment.TickCount64 - Interlocked.Read(ref s_cachedAt) > 60_000)
        {
            Interlocked.Exchange(ref s_cachedAt, Environment.TickCount64);
            s_cache.Clear();
        }
        if (s_cache.TryGetValue(path, out accessed)) return true;
        if (s_queued.TryAdd(path, 0))
        {
            EnsureWorker();
            if (!s_queue.TryAdd(path)) s_queued.TryRemove(path, out _);
        }
        return false;
    }

    static void EnsureWorker()
    {
        if (s_worker != null) return;
        lock (s_queue)
        {
            if (s_worker != null) return;
            s_worker = new Thread(Worker) { IsBackground = true, Name = "Rock Preview date reader", Priority = ThreadPriority.BelowNormal };
            s_worker.Start();
        }
    }

    static void Worker()
    {
        foreach (var path in s_queue.GetConsumingEnumerable())
        {
            long accessed = 0;
            try
            {
                if (GetFileAttributesEx(LongPath(path), 0, out var d)) accessed = d.LastAccessTime;
            }
            catch (Exception) { }
            s_cache[path] = accessed;
            s_queued.TryRemove(path, out _);
            if (s_queue.Count == 0) Resolved?.Invoke();
        }
    }

    static string LongPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct AttributeData
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesExW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetFileAttributesEx(string fileName, int infoLevel, out AttributeData data);
}

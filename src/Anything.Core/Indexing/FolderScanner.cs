using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;

namespace Anything.Core.Indexing;

/// <summary>A scanned directory: its full path and its (non-excluded) children.</summary>
public sealed class ScannedDirectory(string fullPath)
{
    public string FullPath { get; } = fullPath;
    public List<ScannedEntry> Entries { get; } = [];
    /// <summary>Index id of this folder when scanning progressively (set before the folder is scanned).</summary>
    public int IndexId { get; set; } = -1;
}

public struct ScannedEntry
{
    public string Name;
    public FileAttributes Attributes;
    public long Size;
    public long Modified;
    public long Created;
    /// <summary>Non-null for folders that were recursed into (reparse points are listed but not followed).</summary>
    public ScannedDirectory? Directory;
}

/// <summary>
/// The root of a scan could not be listed (drive gone, share offline, no access), or the network failed partway, so
/// the scan has nothing trustworthy to replace the indexed copy with.
/// </summary>
public sealed class RootUnavailableException : IOException
{
    public RootUnavailableException(string path, int error) : this(path, error, FolderScanner.IsOffline(path, error)) { }

    public RootUnavailableException(string path, int error, bool offline)
        : base($"{path} isn't available ({new System.ComponentModel.Win32Exception(error).Message})")
    {
        Path = path;
        Error = error;
        Offline = offline;
    }

    public string Path { get; }
    public int Error { get; }
    /// <summary>The network or the drive went away (as opposed to the folder being deleted or access being denied).</summary>
    public bool Offline { get; }
}

/// <summary>
/// Recursive directory scanner built on FindFirstFileExW (FindExInfoBasic + large fetch), parallelized across
/// subfolders with a bounded number of worker threads. Does not follow reparse points; ignores inaccessible folders.
/// </summary>
public static unsafe class FolderScanner
{
    public static int MaxParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>Errors meaning the drive or the network went away (not a deleted or protected folder).</summary>
    public static bool IsOfflineError(int error) => error is
        3 /* ERROR_PATH_NOT_FOUND: also what a missing drive gives */ or 15 /* ERROR_INVALID_DRIVE */ or 21 /* ERROR_NOT_READY */
        or 51 /* ERROR_REM_NOT_LIST */ or 53 /* ERROR_BAD_NETPATH */ or 55 /* ERROR_DEV_NOT_EXIST */ or 59 /* ERROR_UNEXP_NET_ERR */
        or 64 /* ERROR_NETNAME_DELETED */ or 67 /* ERROR_BAD_NET_NAME */ or 121 /* ERROR_SEM_TIMEOUT */
        or 58 /* ERROR_BAD_NET_RESP */ or 240 /* ERROR_VC_DISCONNECTED */ or 1167 /* ERROR_DEVICE_NOT_CONNECTED */
        or 1201 /* ERROR_CONNECTION_UNAVAIL */ or 1203 /* ERROR_NO_NET_OR_BAD_PATH */ or 1222 /* ERROR_NO_NETWORK */
        or 1225 /* ERROR_CONNECTION_REFUSED */ or 1231 /* ERROR_NETWORK_UNREACHABLE */ or 1232 /* ERROR_HOST_UNREACHABLE */
        or 1234 /* ERROR_PORT_UNREACHABLE */ or 1236 /* ERROR_CONNECTION_ABORTED */ or 1256 /* ERROR_HOST_DOWN */
        or 1311 /* ERROR_NO_LOGON_SERVERS */ or 2250 /* ERROR_NOT_CONNECTED */;

    /// <summary>
    /// Whether <paramref name="error"/> listing <paramref name="path"/> means its drive or share went away. "Path not
    /// found" means that only when the drive or share itself is missing; with the drive there, the folder was deleted.
    /// </summary>
    public static bool IsOffline(string path, int error)
    {
        if (!IsOfflineError(error)) return false;
        if (error != 3) return true;
        string? volume;
        try { volume = System.IO.Path.GetPathRoot(path); }
        catch (ArgumentException) { return true; }
        if (string.IsNullOrEmpty(volume) || string.Equals(volume.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        return ProbeRoot(volume.TrimEnd('\\')) is int e && e != 0 && IsOfflineError(e);
    }

    /// <summary>
    /// Waits for <see cref="ProbeRoot"/> on another thread, so a cancelled caller returns at once instead of waiting out
    /// a network timeout (half a minute on an unreachable server); the probe itself finishes in the background.
    /// </summary>
    public static int ProbeRoot(string path, CancellationToken ct)
    {
        if (!ct.CanBeCanceled) return ProbeRoot(path);
        var probe = Task.Run(() => ProbeRoot(path));
        try { probe.Wait(ct); }
        catch (AggregateException) { }
        catch (OperationCanceledException)
        {
            // Usually the probe is about to close its handle (an eject waits for that); a dead server's isn't waited for.
            try { probe.Wait(500); } catch (AggregateException) { }
            throw;
        }
        return probe.IsCompletedSuccessfully ? probe.Result : 59 /* ERROR_UNEXP_NET_ERR */;
    }

    /// <summary>
    /// Asks, at most every few seconds per scan, whether the scan's root still answers: many folders failing at once
    /// (a dropped connection) share one probe instead of each asking a dead server.
    /// </summary>
    sealed class RootCheck(string root)
    {
        readonly object _gate = new();
        long _checkedAt = long.MinValue;
        int _error;

        public int Error()
        {
            lock (_gate)
            {
                if (Environment.TickCount64 - _checkedAt > 5000)
                {
                    _error = ProbeRoot(root);
                    _checkedAt = Environment.TickCount64;
                }
                return _error;
            }
        }
    }

    /// <summary>
    /// Scans <paramref name="rootPath"/> recursively. <paramref name="counter"/> is incremented per entry found.
    /// When <paramref name="onScanned"/> is given the scan is progressive: it is invoked once per folder, after the
    /// folder's entries are read and before its subfolders are queued, so a callback that adds the entries to the
    /// index (and sets each subfolder's <see cref="ScannedDirectory.IndexId"/>) always sees parents before children.
    /// </summary>
    /// <remarks>
    /// Throws <see cref="RootUnavailableException"/> when the root itself can't be listed, and, with
    /// <paramref name="failOnBrokenConnection"/>, when the drive or network goes away partway (a half-read share must
    /// not replace the full copy in the index). Unreadable subfolders are otherwise skipped.
    /// </remarks>
    public static ScannedDirectory Scan(string rootPath, ExclusionFilter filter, CancellationToken ct, StrongBox<long>? counter = null,
        Action<ScannedDirectory>? onScanned = null, int rootIndexId = -1, bool failOnBrokenConnection = false)
    {
        var root = new ScannedDirectory(rootPath.Length == 2 && rootPath[1] == ':' ? rootPath : rootPath.TrimEnd('\\')) { IndexId = rootIndexId };
        // Before any worker starts: a root that can't be listed must not look like an empty folder.
        int rootError;
        using (Drives.QuietErrors()) rootError = ProbeRoot(root.FullPath, ct);
        ct.ThrowIfCancellationRequested();
        if (rootError != 0) throw new RootUnavailableException(root.FullPath, rootError);
        var check = failOnBrokenConnection ? new RootCheck(root.FullPath) : null;
        var queue = new ConcurrentQueue<ScannedDirectory>();
        // Not disposed: after a cancel, workers still blocked in a slow network call may touch it when they wake up.
        var available = new SemaphoreSlim(0);
        int pending = 1;
        int finished = 0;
        int workers = MaxParallelism;
        var done = new CountdownEvent(workers);
        queue.Enqueue(root);
        available.Release();
        Exception? failure = null;

        void Worker()
        {
            using var quiet = Drives.QuietErrors(); // no "Insert a disk" box for an emptied card reader
            try
            {
                while (true)
                {
                    available.Wait(ct);
                    if (Volatile.Read(ref finished) != 0) return;
                    if (!queue.TryDequeue(out var dir)) continue;
                    try
                    {
                        ScanOne(dir, filter, ct, counter, sub =>
                        {
                            Interlocked.Increment(ref pending);
                            queue.Enqueue(sub);
                            available.Release();
                        }, onScanned, check);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (RootUnavailableException) { throw; }
                    catch { /* unreadable folder: skip */ }
                    if (Interlocked.Decrement(ref pending) == 0)
                    {
                        Volatile.Write(ref finished, 1);
                        available.Release(workers);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { failure = ex; Volatile.Write(ref finished, 1); try { available.Release(workers); } catch { } }
            finally { done.Signal(); }
        }

        for (int i = 0; i < workers; i++)
            new Thread(Worker) { IsBackground = true, Name = "Rock Preview folder scan" }.Start();
        // Cancelling waits briefly for the workers to close their find handles (an eject needs the drive let go of), but
        // not for one stuck in a network call: it is left to finish on its own, and whatever it reads afterwards is
        // thrown away (callers check the token before using it).
        try { done.Wait(ct); }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref finished, 1);
            try { done.Wait(2000); } catch (ObjectDisposedException) { }
            throw;
        }
        ct.ThrowIfCancellationRequested();
        if (failure != null) throw failure;
        return root;
    }

    /// <summary>0 when <paramref name="path"/> can be listed (an empty drive counts), else the Win32 error.</summary>
    internal static int ProbeRoot(string path)
    {
        using var quiet = Drives.QuietErrors();
        Win32FindData data;
        nint h = Native.FindFirstFileEx(Native.LongPath(path + "\\*"), Native.FindExInfoBasic, &data, 0, 0, 0);
        if (h != Native.InvalidHandle) { Native.FindClose(h); return 0; }
        int error = Marshal.GetLastPInvokeError();
        return error is Native.ERROR_FILE_NOT_FOUND or Native.ERROR_NO_MORE_FILES ? 0 : error;
    }

    /// <summary>
    /// Lists one folder. With <paramref name="check"/> (a drive or share that can go away), a listing that fails or ends
    /// early is read once more; if that fails too and the root no longer answers, the whole scan stops (its tree would be
    /// incomplete), else just this folder is skipped as unreadable (a broken DFS link, one folder timing out).
    /// </summary>
    static void ScanOne(ScannedDirectory dir, ExclusionFilter filter, CancellationToken ct, StrongBox<long>? counter,
        Action<ScannedDirectory> enqueue, Action<ScannedDirectory>? onScanned, RootCheck? check)
    {
        var entries = new List<ScannedEntry>();
        int error = List(dir, filter, ct, counter, entries);
        if (error != 0 && check != null && error != Native.ERROR_ACCESS_DENIED)
        {
            Thread.Sleep(300); // a reconnect blip usually clears in that time
            ct.ThrowIfCancellationRequested();
            entries.Clear();
            error = List(dir, filter, ct, counter, entries);
            if (error != 0 && error != Native.ERROR_ACCESS_DENIED)
            {
                int rootError = check.Error();
                if (rootError != 0 && IsOfflineError(rootError)) throw new RootUnavailableException(dir.FullPath, rootError, offline: true);
            }
        }
        if (error != 0 && entries.Count == 0) return;
        // Progressive scans queue subfolders only after onScanned has given them index ids.
        dir.Entries.AddRange(entries);
        if (onScanned != null) onScanned(dir);
        foreach (var e in entries)
            if (e.Directory != null) enqueue(e.Directory);
        if (onScanned != null) dir.Entries.Clear(); // already in the index; free the memory as we go
    }

    /// <summary>Reads one folder's entries into <paramref name="into"/>. 0 when the listing was complete, else the error.</summary>
    static int List(ScannedDirectory dir, ExclusionFilter filter, CancellationToken ct, StrongBox<long>? counter, List<ScannedEntry> into)
    {
        Win32FindData data;
        nint h = Native.FindFirstFileEx(Native.LongPath(dir.FullPath + "\\*"), Native.FindExInfoBasic, &data, 0, 0, Native.FIND_FIRST_EX_LARGE_FETCH);
        if (h == Native.InvalidHandle)
        {
            int error = Marshal.GetLastPInvokeError();
            return error == Native.ERROR_FILE_NOT_FOUND ? 0 : error; // FILE_NOT_FOUND: an empty folder (or drive root)
        }
        int found = 0;
        try
        {
            do
            {
                char* fn = data.FileName;
                if (fn[0] == '.' && (fn[1] == '\0' || (fn[1] == '.' && fn[2] == '\0'))) continue;
                var attrs = (FileAttributes)data.FileAttributes;
                string name = new(fn);
                bool isDir = (attrs & FileAttributes.Directory) != 0;
                string? fullPath = isDir ? dir.FullPath + "\\" + name : null;
                if (!filter.IsEmpty && filter.Excludes(name, attrs, fullPath)) continue;
                var entry = new ScannedEntry
                {
                    Name = name,
                    Attributes = attrs,
                    Size = isDir ? -1 : ((long)data.FileSizeHigh << 32) | data.FileSizeLow,
                    Modified = data.LastWriteTime,
                    Created = data.CreationTime,
                };
                if (isDir && (attrs & FileAttributes.ReparsePoint) == 0) entry.Directory = new ScannedDirectory(fullPath!);
                into.Add(entry);
                if ((++found & 1023) == 0) ct.ThrowIfCancellationRequested();
            }
            while (Native.FindNextFile(h, &data));
            int end = Marshal.GetLastPInvokeError();
            return end == Native.ERROR_NO_MORE_FILES ? 0 : end;
        }
        finally
        {
            Native.FindClose(h);
            if (counter != null) Interlocked.Add(ref counter.Value, found);
        }
    }

    /// <summary>
    /// Adds a scanned tree below <paramref name="parentId"/> (or as a new root when it is <see cref="FileIndex.NoParent"/>),
    /// parents before children. <paramref name="onAdded"/> receives (parentId, name, id) for every added child.
    /// Returns the id of the entry created for <paramref name="dir"/> itself.
    /// </summary>
    public static int AddTree(FileIndex.BulkAdder b, ScannedDirectory dir, string name, int parentId, FileAttributes attributes,
        long modified, long created, Action<int, string, int>? onAdded)
    {
        int topId = b.Add(name, parentId, attributes | FileAttributes.Directory, -1, modified, created);
        if (parentId != FileIndex.NoParent) onAdded?.Invoke(parentId, name, topId);
        var stack = new Stack<(ScannedDirectory Dir, int Id)>();
        stack.Push((dir, topId));
        while (stack.Count > 0)
        {
            var (d, id) = stack.Pop();
            foreach (var e in d.Entries)
            {
                int cid = b.Add(e.Name, id, e.Attributes, e.Size, e.Modified, e.Created);
                onAdded?.Invoke(id, e.Name, cid);
                if (e.Directory != null) stack.Push((e.Directory, cid));
            }
        }
        return topId;
    }
}

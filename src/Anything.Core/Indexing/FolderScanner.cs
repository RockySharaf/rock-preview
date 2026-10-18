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
/// Recursive directory scanner built on FindFirstFileExW (FindExInfoBasic + large fetch), parallelized across
/// subfolders with a bounded number of worker threads. Does not follow reparse points; ignores inaccessible folders.
/// </summary>
public static unsafe class FolderScanner
{
    public static int MaxParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>
    /// Scans <paramref name="rootPath"/> recursively. <paramref name="counter"/> is incremented per entry found.
    /// When <paramref name="onScanned"/> is given the scan is progressive: it is invoked once per folder, after the
    /// folder's entries are read and before its subfolders are queued, so a callback that adds the entries to the
    /// index (and sets each subfolder's <see cref="ScannedDirectory.IndexId"/>) always sees parents before children.
    /// </summary>
    public static ScannedDirectory Scan(string rootPath, ExclusionFilter filter, CancellationToken ct, StrongBox<long>? counter = null,
        Action<ScannedDirectory>? onScanned = null, int rootIndexId = -1)
    {
        var root = new ScannedDirectory(rootPath.Length == 2 && rootPath[1] == ':' ? rootPath : rootPath.TrimEnd('\\')) { IndexId = rootIndexId };
        var queue = new ConcurrentQueue<ScannedDirectory>();
        using var available = new SemaphoreSlim(0);
        int pending = 1;
        int finished = 0;
        int workers = MaxParallelism;
        queue.Enqueue(root);
        available.Release();
        Exception? failure = null;

        void Worker()
        {
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
                        }, onScanned);
                    }
                    catch (OperationCanceledException) { throw; }
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
        }

        var threads = new Thread[workers];
        for (int i = 0; i < workers; i++)
        {
            threads[i] = new Thread(Worker) { IsBackground = true, Name = "Rock Preview folder scan" };
            threads[i].Start();
        }
        foreach (var t in threads) t.Join();
        ct.ThrowIfCancellationRequested();
        if (failure != null) throw failure;
        return root;
    }

    static void ScanOne(ScannedDirectory dir, ExclusionFilter filter, CancellationToken ct, StrongBox<long>? counter,
        Action<ScannedDirectory> enqueue, Action<ScannedDirectory>? onScanned)
    {
        Win32FindData data;
        nint h = Native.FindFirstFileEx(Native.LongPath(dir.FullPath + "\\*"), Native.FindExInfoBasic, &data, 0, 0, Native.FIND_FIRST_EX_LARGE_FETCH);
        if (h == Native.InvalidHandle) return;
        // Progressive scans queue subfolders only after onScanned has given them index ids.
        List<ScannedDirectory>? deferred = onScanned != null ? [] : null;
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
                if (isDir && (attrs & FileAttributes.ReparsePoint) == 0)
                {
                    entry.Directory = new ScannedDirectory(fullPath!);
                    if (deferred != null) deferred.Add(entry.Directory);
                    else enqueue(entry.Directory);
                }
                dir.Entries.Add(entry);
                if ((++found & 1023) == 0) ct.ThrowIfCancellationRequested();
            }
            while (Native.FindNextFile(h, &data));
        }
        finally
        {
            Native.FindClose(h);
            if (counter != null) Interlocked.Add(ref counter.Value, found);
        }
        if (onScanned != null)
        {
            onScanned(dir);
            dir.Entries.Clear(); // already in the index; free the memory as we go
            foreach (var sub in deferred!) enqueue(sub);
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

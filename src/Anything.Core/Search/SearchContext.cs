using System.Collections.Concurrent;
using Anything.Core.Indexing;

namespace Anything.Core.Search;

/// <summary>
/// Folder path prefixes ("C:\dir\") keyed by folder id. Built lazily and shared across searches while the
/// index version is unchanged, so typing a path query only pays for path construction once.
/// Concurrent fills write identical strings, which is a benign race.
/// </summary>
internal sealed class FolderPathCache
{
    public FileIndex Index { get; }
    public long Version { get; }
    public int Count { get; }
    readonly string?[] _prefix;

    public FolderPathCache(FileIndex index)
    {
        Index = index;
        Version = index.Version;
        Count = index.Count;
        _prefix = new string?[Count];
    }

    public bool IsValidFor(FileIndex index) =>
        ReferenceEquals(index, Index) && index.Version == Version && index.Count == Count;

    [ThreadStatic] static List<int>? t_stack;

    /// <summary>Path of a folder with a trailing backslash ("C:\" for a root, "C:\dir\" for a folder).</summary>
    public string Prefix(int folder)
    {
        var p = _prefix[folder];
        if (p != null) return p;
        var stack = t_stack ??= new List<int>(64);
        stack.Clear();
        int cur = folder;
        while (cur != FileIndex.NoParent && _prefix[cur] == null && stack.Count < 4096)
        {
            stack.Add(cur);
            cur = Index.Parent(cur);
        }
        string basePrefix = cur == FileIndex.NoParent || stack.Count >= 4096 ? "" : _prefix[cur]!;
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            int id = stack[i];
            basePrefix = string.Concat(basePrefix, Index.Name(id), "\\");
            _prefix[id] = basePrefix;
        }
        return basePrefix;
    }

    /// <summary>Containing folder path without trailing separator ("" for roots), matching FileIndex.GetParentPath.</summary>
    public string ParentPath(int id)
    {
        int parent = Index.Parent(id);
        if (parent == FileIndex.NoParent) return "";
        string p = Prefix(parent);
        return Index.Parent(parent) == FileIndex.NoParent ? p : p[..^1];
    }
}

/// <summary>Per-search state shared by all matchers (read under the index read lock).</summary>
internal sealed class SearchContext
{
    public FileIndex Index { get; }
    public FolderPathCache Paths { get; }
    public int Count { get; }
    public DateTime Now { get; }

    public bool NeedChildCounts { get; set; }
    /// <summary>The query or the sort uses run history (runcount:, dr:, Run Count, Date Run).</summary>
    public bool NeedRuns { get; set; }
    /// <summary>Run history by id, once <see cref="Prepare"/> has run with <see cref="NeedRuns"/>.</summary>
    public RunLookup? Runs { get; private set; }
    public bool Dupe { get; set; }
    /// <summary>The query's content terms, resolved before the lock (null = it never looks inside files).</summary>
    internal Content.Search.ContentPrefetch? Content { get; set; }
    /// <summary>Ids of the prefetch view's documents in this index (null = none, or stale).</summary>
    internal Content.DocIdMap? ContentMap { get; set; }
    /// <summary>Some plain word also matches inside files (results are split: name matches first).</summary>
    internal bool ContentExpanded { get; set; }
    /// <summary>The id map doesn't cover every matching document (a cleared index, segments newer than the map).</summary>
    internal bool ContentStale { get; set; }
    /// <summary>While set, plain words test names only (the name-first partition re-tests the same matcher).</summary>
    internal bool NameOnlyPass;
    public int[]? ChildFiles { get; private set; }
    public int[]? ChildFolders { get; private set; }

    readonly List<Matcher> _preparers = [];
    int[]? _depth; // folder depth + 1, 0 = unknown

    public SearchContext(FileIndex index, FolderPathCache paths, DateTime now)
    {
        Index = index;
        Paths = paths;
        Count = index.Count;
        Now = now;
    }

    public void AddPreparer(Matcher m) => _preparers.Add(m);

    public void Prepare(CancellationToken ct)
    {
        if (NeedRuns) Runs = Index.RunHistory.Lookup(Index);
        if (NeedChildCounts)
        {
            var files = new int[Count];
            var folders = new int[Count];
            var idx = Index;
            for (int id = 0; id < Count; id++)
            {
                if (!idx.IsAlive(id)) continue;
                int p = idx.Parent(id);
                if ((uint)p >= (uint)Count) continue;
                if (idx.IsDirectory(id)) folders[p]++; else files[p]++;
            }
            ChildFiles = files;
            ChildFolders = folders;
        }
        foreach (var m in _preparers)
        {
            ct.ThrowIfCancellationRequested();
            m.Prepare(this, ct);
        }
    }

    [ThreadStatic] static char[]? t_pathBuffer;

    /// <summary>Full path of an entry as a span over a thread-local buffer (valid until the next call on this thread).</summary>
    public ReadOnlySpan<char> FullPath(int id)
    {
        string name = Index.Name(id);
        int parent = Index.Parent(id);
        string prefix = parent == FileIndex.NoParent ? "" : Paths.Prefix(parent);
        int len = prefix.Length + name.Length + (parent == FileIndex.NoParent ? 1 : 0);
        var buf = t_pathBuffer;
        if (buf == null || buf.Length < len) t_pathBuffer = buf = new char[Math.Max(len, 1024)];
        prefix.CopyTo(buf);
        name.CopyTo(0, buf, prefix.Length, name.Length);
        if (parent == FileIndex.NoParent) buf[len - 1] = '\\';
        return buf.AsSpan(0, len);
    }

    // Date accessed read from the disk when it isn't indexed (Everything does the same, slowly): once per entry and
    // search; a drive or share that doesn't answer is given up on for the rest of the search.
    ConcurrentDictionary<int, long>? _diskAccessed;
    ConcurrentDictionary<int, bool>? _offlineRoots;

    /// <summary>
    /// Date accessed (FILETIME UTC, 0 = unknown): from the index when indexed, else (or when the index doesn't know it:
    /// a drive read through an older service) read from the disk.
    /// </summary>
    public long Accessed(int id)
    {
        if (Index.HasDateAccessed && Index.Accessed(id) is not 0 and var indexed) return indexed;
        var memo = _diskAccessed ?? Interlocked.CompareExchange(ref _diskAccessed, new ConcurrentDictionary<int, long>(), null) ?? _diskAccessed!;
        if (memo.TryGetValue(id, out long known)) return known;
        int root = id;
        for (int guard = 0; Index.Parent(root) != FileIndex.NoParent && guard < 4096; guard++) root = Index.Parent(root);
        var offline = _offlineRoots ?? Interlocked.CompareExchange(ref _offlineRoots, new ConcurrentDictionary<int, bool>(), null) ?? _offlineRoots!;
        long value = 0;
        if (!offline.ContainsKey(root) && !Native.TryGetAccessed(Index.GetFullPath(id), out value, out int error) && FolderScanner.IsOfflineError(error))
            offline[root] = true;
        memo[id] = value;
        return value;
    }

    /// <summary>Number of ancestors (0 for roots).</summary>
    public int Depth(int id)
    {
        int parent = Index.Parent(id);
        return parent == FileIndex.NoParent ? 0 : FolderDepth(parent) + 1;
    }

    int FolderDepth(int folder)
    {
        var cache = _depth ?? Interlocked.CompareExchange(ref _depth, new int[Count], null) ?? _depth!;
        if ((uint)folder >= (uint)cache.Length) return Index.Depth(folder);
        int v = cache[folder];
        if (v != 0) return v - 1;
        int parent = Index.Parent(folder);
        int d = parent == FileIndex.NoParent ? 0 : FolderDepth(parent) + 1;
        cache[folder] = d + 1;
        return d;
    }
}

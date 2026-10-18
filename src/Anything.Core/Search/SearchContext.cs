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
    public bool Dupe { get; set; }
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

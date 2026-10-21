using System.Runtime.InteropServices;

namespace Anything.Core.Indexing;

/// <summary>
/// Per-volume map from MFT record number (FRN without sequence) to index ids. Records with several hard links
/// own several ids: the first is kept in a flat array, the rest in a side dictionary.
/// </summary>
public sealed class RecordMap
{
    int[] _primary;
    Dictionary<long, List<int>>? _extra;

    public RecordMap(int capacity = 1024)
    {
        _primary = new int[Math.Max(capacity, 16)];
        Array.Fill(_primary, -1);
    }

    public int Get(long rec) => (ulong)rec < (ulong)_primary.Length ? _primary[rec] : -1;

    public void Set(long rec, int id)
    {
        if (rec < 0 || rec > int.MaxValue) return;
        if (rec >= _primary.Length)
        {
            int old = _primary.Length;
            Array.Resize(ref _primary, (int)Math.Min(int.MaxValue, Math.Max(rec + 1, (long)old * 2)));
            Array.Fill(_primary, -1, old, _primary.Length - old);
        }
        _primary[rec] = id;
    }

    public void Add(long rec, int id)
    {
        if (Get(rec) < 0) { Set(rec, id); return; }
        _extra ??= [];
        if (!_extra.TryGetValue(rec, out var list)) _extra[rec] = list = new List<int>(1);
        list.Add(id);
    }

    public bool HasExtra(long rec) => _extra != null && _extra.ContainsKey(rec);

    /// <summary>Translates stored ids after the index was compacted (old id → new id, -1 = removed).</summary>
    public void Remap(int[] remap)
    {
        for (int i = 0; i < _primary.Length; i++)
        {
            int id = _primary[i];
            if (id >= 0) _primary[i] = id < remap.Length ? remap[id] : -1;
        }
        if (_extra == null) return;
        foreach (var rec in _extra.Keys.ToArray())
        {
            var list = _extra[rec];
            for (int i = list.Count - 1; i >= 0; i--)
            {
                int id = list[i] < remap.Length ? remap[list[i]] : -1;
                if (id < 0) list.RemoveAt(i); else list[i] = id;
            }
            if (list.Count == 0) _extra.Remove(rec);
            else if (Get(rec) < 0) { _primary[rec] = list[0]; list.RemoveAt(0); if (list.Count == 0) _extra.Remove(rec); }
        }
    }

    /// <summary>All ids (primary first) currently mapped for a record.</summary>
    public void GetAll(long rec, List<int> into)
    {
        into.Clear();
        int p = Get(rec);
        if (p >= 0) into.Add(p);
        if (_extra != null && _extra.TryGetValue(rec, out var list)) into.AddRange(list);
    }

    public void RemoveAll(long rec)
    {
        if ((ulong)rec < (ulong)_primary.Length) _primary[rec] = -1;
        _extra?.Remove(rec);
    }

    public void RemoveId(long rec, int id)
    {
        if (Get(rec) == id)
        {
            if (_extra != null && _extra.TryGetValue(rec, out var list) && list.Count > 0)
            {
                _primary[rec] = list[0];
                list.RemoveAt(0);
                if (list.Count == 0) _extra.Remove(rec);
            }
            else if ((ulong)rec < (ulong)_primary.Length) _primary[rec] = -1;
        }
        else if (_extra != null && _extra.TryGetValue(rec, out var list))
        {
            list.Remove(id);
            if (list.Count == 0) _extra.Remove(rec);
        }
    }

    /// <summary>Inverse map (index id → record number, -1 if none) sized for <paramref name="indexCount"/> slots.</summary>
    public long[] BuildInverse(int indexCount)
    {
        var inv = new long[indexCount];
        Array.Fill(inv, -1L);
        for (int rec = 0; rec < _primary.Length; rec++)
        {
            int id = _primary[rec];
            if ((uint)id < (uint)indexCount) inv[id] = rec;
        }
        if (_extra != null)
            foreach (var (rec, list) in _extra)
                foreach (int id in list)
                    if ((uint)id < (uint)indexCount) inv[id] = rec;
        return inv;
    }
}

/// <summary>
/// Raw per-record data collected from the MFT (or the USN enumeration fallback), indexed by record number,
/// then turned into <see cref="FileIndex"/> entries in parent-before-child order by <see cref="BuildInto"/>.
/// </summary>
public sealed class VolumeRecords
{
    public const int RootRecord = 5;
    public const int FirstUserRecord = 16;
    const byte FlagInUse = 1, FlagDirectory = 2;

    string?[] _names;
    long[] _parents;
    long[] _sizes;
    long[] _created;
    long[] _modified;
    long[] _accessed;
    uint[] _attrs;
    byte[] _flags;
    int _length;
    readonly int _recordLimit;
    List<(int Rec, long Parent, string Name)>? _extraLinks;
    HashSet<(int Rec, long Parent, string Name)>? _extraLinkSet;
    Dictionary<int, (long Parent, string Name)>? _dosFallback;
    List<(int Base, bool HasData, long Size, MftFileName[] Names)>? _laterExtensions;

    /// <param name="recordLimit">
    /// Record numbers at or above it are ignored: the size of the MFT the records come from, so a corrupt or crafted
    /// record can't make the arrays grow to a record number the volume doesn't have.
    /// </param>
    public VolumeRecords(int capacity = 4096, int recordLimit = int.MaxValue)
    {
        _recordLimit = Math.Max(recordLimit, RootRecord + 1);
        capacity = Math.Clamp(capacity, 64, Math.Max(_recordLimit, 64));
        _names = new string?[capacity];
        _parents = new long[capacity];
        _sizes = new long[capacity];
        _created = new long[capacity];
        _modified = new long[capacity];
        _accessed = new long[capacity];
        _attrs = new uint[capacity];
        _flags = new byte[capacity];
    }

    /// <summary>One past the highest record number seen.</summary>
    public int Length => _length;

    /// <summary>The records carry dates accessed (read from the file table; not from the USN enumeration or an older service).</summary>
    public bool HasAccessTimes { get; private set; }

    public bool IsInUse(int rec) => rec < _length && (_flags[rec] & FlagInUse) != 0;
    public bool IsDirectory(int rec) => rec < _length && (_flags[rec] & FlagDirectory) != 0;
    public string? NameOf(int rec) => rec < _length ? _names[rec] : null;
    public long ParentOf(int rec) => rec < _length ? _parents[rec] : -1;
    public long SizeOf(int rec) => rec < _length ? _sizes[rec] : 0;

    void Ensure(int rec)
    {
        if (rec >= _names.Length)
        {
            int n = (int)Math.Min(Math.Max(rec + 1L, _names.Length * 2L), Math.Max(_recordLimit, rec + 1L));
            Array.Resize(ref _names, n);
            Array.Resize(ref _parents, n);
            Array.Resize(ref _sizes, n);
            Array.Resize(ref _created, n);
            Array.Resize(ref _modified, n);
            Array.Resize(ref _accessed, n);
            Array.Resize(ref _attrs, n);
            Array.Resize(ref _flags, n);
        }
        if (rec >= _length) _length = rec + 1;
    }

    /// <summary>
    /// Merges one parsed FILE record. Extension records are folded into their base record (those whose base lies
    /// beyond the records seen so far wait for <see cref="FinishMerge"/>).
    /// </summary>
    public void Merge(MftRecord r)
    {
        if (!r.InUse) return;
        long target = r.BaseRecord != 0 ? r.BaseRecord : r.RecordNumber;
        // Both numbers come from the disk: outside the MFT means corrupt (or crafted), not a reason to allocate.
        if (r.RecordNumber < 0 || r.RecordNumber >= _recordLimit || target < 0 || target >= _recordLimit) return;
        int t = (int)target;
        if (r.BaseRecord != 0 && t >= _names.Length)
        {
            // Its base comes later in the MFT: keep just this record's data until then, so the arrays only ever grow
            // with the records actually read, whatever base reference a record claims.
            (_laterExtensions ??= []).Add((t, r.HasData, r.DataSize, [.. r.FileNames]));
            return;
        }
        Ensure(t);
        if (r.BaseRecord == 0)
        {
            _flags[t] |= FlagInUse;
            if (r.IsDirectory) _flags[t] |= FlagDirectory;
            if (r.HasStandardInformation)
            {
                _attrs[t] = (uint)r.Attributes;
                _created[t] = r.CreationTime;
                _modified[t] = r.ModificationTime;
                _accessed[t] = r.AccessTime;
                HasAccessTimes = true;
            }
        }
        MergeData(t, r.HasData, r.DataSize, CollectionsMarshal.AsSpan(r.FileNames));
    }

    /// <summary>Folds in the extension records <see cref="Merge"/> held back, now that every base record has been read.</summary>
    public void FinishMerge()
    {
        var later = _laterExtensions;
        _laterExtensions = null;
        if (later == null) return;
        // A base that never turned up (or isn't in use) owns nothing: its extensions are dropped, as BuildInto would.
        foreach (var (t, hasData, size, names) in later)
            if (IsInUse(t)) MergeData(t, hasData, size, names);
    }

    void MergeData(int t, bool hasData, long size, ReadOnlySpan<MftFileName> names)
    {
        if (hasData) _sizes[t] = size;
        foreach (var fn in names)
        {
            if (fn.Namespace == FileNameNamespace.Dos)
            {
                if (_names[t] == null)
                {
                    _dosFallback ??= [];
                    _dosFallback.TryAdd(t, (fn.ParentRecord, fn.Name));
                }
                continue;
            }
            AddName(t, fn.ParentRecord, fn.Name);
        }
    }

    void AddName(int rec, long parent, string name)
    {
        if (_names[rec] == null)
        {
            _names[rec] = name;
            _parents[rec] = parent;
            return;
        }
        if (_parents[rec] == parent && string.Equals(_names[rec], name, StringComparison.Ordinal)) return;
        _extraLinks ??= [];
        // A set, not a scan of the list: a volume with millions of hard links (real or crafted) would take hours.
        _extraLinkSet ??= [.. _extraLinks];
        if (_extraLinkSet.Add((rec, parent, name))) _extraLinks.Add((rec, parent, name));
    }

    /// <summary>Sets a record from a USN enumeration record (fallback path: no sizes or dates).</summary>
    public void SetFromUsn(long rec, long parent, string name, FileAttributes attributes)
    {
        if (rec < 0 || rec >= _recordLimit) return;
        int t = (int)rec;
        Ensure(t);
        _flags[t] |= FlagInUse;
        if ((attributes & FileAttributes.Directory) != 0) _flags[t] |= FlagDirectory;
        _attrs[t] = (uint)attributes & 0x0007FFFF;
        _sizes[t] = -1;
        AddName(t, parent, name);
    }

    /// <summary>Marks the root directory record (the USN enumeration does not always return it).</summary>
    public void EnsureRoot()
    {
        Ensure(RootRecord);
        _flags[RootRecord] |= FlagInUse | FlagDirectory;
    }

    /// <summary>
    /// Adds every reachable record under the root (record 5) to <paramref name="index"/> in breadth-first order,
    /// so parents always precede children. System metafiles (records &lt; 16) and orphans are skipped,
    /// exclusions are applied (an excluded folder drops its subtree). Returns the new root id and the record map.
    /// When <paramref name="abandoned"/> (asked under the index's write lock) says so, adds nothing and returns RootId -1.
    /// </summary>
    public (int RootId, RecordMap Map, int Count) BuildInto(FileIndex index, string rootName, ExclusionFilter filter, Action<int>? progress = null,
        Func<bool>? abandoned = null)
    {
        FinishMerge();
        int n = Math.Max(_length, RootRecord + 1);
        Ensure(RootRecord);
        if (_dosFallback != null)
            foreach (var (rec, link) in _dosFallback)
                if (_names[rec] == null) { _names[rec] = link.Name; _parents[rec] = link.Parent; }

        // Child lists (linked through link indices). Link i < n is the primary name of record i.
        int extra = _extraLinks?.Count ?? 0;
        var head = new int[n];
        Array.Fill(head, -1);
        var next = new int[n + extra];
        for (int rec = n - 1; rec >= FirstUserRecord; rec--)
        {
            if ((_flags[rec] & FlagInUse) == 0 || _names[rec] == null) continue;
            long p = _parents[rec];
            if ((ulong)p >= (ulong)n || p == rec) continue;
            next[rec] = head[p];
            head[p] = rec;
        }
        for (int i = 0; i < extra; i++)
        {
            var l = _extraLinks![i];
            if (l.Rec < FirstUserRecord || (_flags[l.Rec] & FlagInUse) == 0 || (ulong)l.Parent >= (ulong)n || l.Parent == l.Rec) continue;
            next[n + i] = head[l.Parent];
            head[l.Parent] = n + i;
        }

        var map = new RecordMap(n);
        int rootId = -1, count = 0;
        bool needPaths = filter.NeedsFolderPaths;
        string rootPath = rootName.TrimEnd('\\') + "\\";
        index.BulkAdd(b =>
        {
            if (abandoned?.Invoke() == true) return;
            rootId = b.Add(rootName.TrimEnd('\\'), FileIndex.NoParent, FileAttributes.Directory, -1, _modified[RootRecord], _created[RootRecord], _accessed[RootRecord]);
            map.Set(RootRecord, rootId);
            var visited = new bool[n];
            visited[RootRecord] = true;
            var queue = new Queue<(int Rec, int Id, string? Path)>();
            queue.Enqueue((RootRecord, rootId, rootPath));
            while (queue.Count > 0)
            {
                var (dirRec, dirId, dirPath) = queue.Dequeue();
                for (int link = head[dirRec]; link >= 0; link = next[link])
                {
                    int child; string name;
                    if (link < n) { child = link; name = _names[link]!; }
                    else { var l = _extraLinks![link - n]; child = l.Rec; name = l.Name; }
                    bool isDir = (_flags[child] & FlagDirectory) != 0;
                    var attrs = (FileAttributes)_attrs[child];
                    if (isDir) attrs |= FileAttributes.Directory;
                    string? childPath = isDir && needPaths && dirPath != null ? ExclusionFilter.Join(dirPath, name) : null;
                    if (!filter.IsEmpty && filter.Excludes(name, attrs, childPath)) continue;
                    if (isDir && visited[child]) continue; // directories cannot be hard-linked; guards against corrupt cycles
                    int id = b.Add(name, dirId, attrs, isDir ? -1 : _sizes[child], _modified[child], _created[child], _accessed[child]);
                    map.Add(child, id);
                    if (++count % 65536 == 0) progress?.Invoke(count);
                    if (isDir)
                    {
                        visited[child] = true;
                        queue.Enqueue((child, id, childPath));
                    }
                }
            }
        });
        progress?.Invoke(count);
        return (rootId, map, count);
    }

    // ---- transfer: the Rock Preview service sends the records it read to the app ---------------------

    /// <summary>
    /// Writes the records (metadata only: names, parents, sizes, dates, attributes) for <see cref="ReadFrom"/>. Dates
    /// accessed go along only when the app asked for them (<paramref name="accessTimes"/>: service protocol 2).
    /// </summary>
    public void WriteTo(BinaryWriter w, bool accessTimes = true)
    {
        int n = _length;
        w.Write(n);
        for (int i = 0; i < n; i++)
        {
            string? name = _names[i];
            w.Write(name != null);
            if (name != null) w.Write(name);
        }
        w.Write(MemoryMarshal.AsBytes(_parents.AsSpan(0, n)));
        w.Write(MemoryMarshal.AsBytes(_sizes.AsSpan(0, n)));
        w.Write(MemoryMarshal.AsBytes(_created.AsSpan(0, n)));
        w.Write(MemoryMarshal.AsBytes(_modified.AsSpan(0, n)));
        if (accessTimes)
        {
            w.Write(HasAccessTimes);
            if (HasAccessTimes) w.Write(MemoryMarshal.AsBytes(_accessed.AsSpan(0, n)));
        }
        w.Write(MemoryMarshal.AsBytes(_attrs.AsSpan(0, n)));
        w.Write(_flags.AsSpan(0, n));
        var links = _extraLinks ?? [];
        w.Write(links.Count);
        foreach (var (rec, parent, name) in links) { w.Write(rec); w.Write(parent); w.Write(name); }
        var dos = _dosFallback ?? [];
        w.Write(dos.Count);
        foreach (var (rec, (parent, name)) in dos) { w.Write(rec); w.Write(parent); w.Write(name); }
    }

    /// <summary>Reads records written by <see cref="WriteTo"/> (with the same <paramref name="accessTimes"/>).</summary>
    public static VolumeRecords ReadFrom(BinaryReader r, bool accessTimes = true)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 1 << 30) throw new InvalidDataException("invalid record count");
        var v = new VolumeRecords(n);
        for (int i = 0; i < n; i++) v._names[i] = r.ReadBoolean() ? r.ReadString() : null;
        ReadExactly(r, MemoryMarshal.AsBytes(v._parents.AsSpan(0, n)));
        ReadExactly(r, MemoryMarshal.AsBytes(v._sizes.AsSpan(0, n)));
        ReadExactly(r, MemoryMarshal.AsBytes(v._created.AsSpan(0, n)));
        ReadExactly(r, MemoryMarshal.AsBytes(v._modified.AsSpan(0, n)));
        if (accessTimes && r.ReadBoolean())
        {
            ReadExactly(r, MemoryMarshal.AsBytes(v._accessed.AsSpan(0, n)));
            v.HasAccessTimes = true;
        }
        ReadExactly(r, MemoryMarshal.AsBytes(v._attrs.AsSpan(0, n)));
        ReadExactly(r, v._flags.AsSpan(0, n));
        v._length = n;
        int links = r.ReadInt32();
        if (links < 0 || links > n * 8 + 1024) throw new InvalidDataException("invalid link count");
        if (links > 0)
        {
            v._extraLinks = new(links);
            for (int i = 0; i < links; i++) v._extraLinks.Add((r.ReadInt32(), r.ReadInt64(), r.ReadString()));
        }
        int dos = r.ReadInt32();
        if (dos < 0 || dos > n) throw new InvalidDataException("invalid short-name count");
        if (dos > 0)
        {
            v._dosFallback = new(dos);
            for (int i = 0; i < dos; i++) v._dosFallback[r.ReadInt32()] = (r.ReadInt64(), r.ReadString());
        }
        return v;
    }

    static void ReadExactly(BinaryReader r, Span<byte> into)
    {
        while (into.Length > 0)
        {
            int got = r.Read(into);
            if (got <= 0) throw new EndOfStreamException();
            into = into[got..];
        }
    }
}

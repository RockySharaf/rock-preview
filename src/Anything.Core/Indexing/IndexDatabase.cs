using System.IO.Compression;
using System.Text;

namespace Anything.Core.Indexing;

/// <summary>What the database stores about one root besides its entries.</summary>
public sealed class DbRoot
{
    public required string Path { get; init; }
    public RootKind Kind { get; init; }
    public ulong JournalId { get; init; }
    public long NextUsn { get; init; }
    /// <summary><see cref="ExclusionFilter.Stamp"/> of the rules the entries were built with (0 = unknown).</summary>
    public ulong RulesStamp { get; init; }
    /// <summary>Its entries carry dates accessed: saved too (version 3), when the index has them.</summary>
    public bool HasDateAccessed { get; init; }
    public required int RootId { get; init; }
    /// <summary>Index id → NTFS record number (-1 if unknown). Only for NTFS roots.</summary>
    public long[]? IdToFrn { get; init; }
}

/// <summary>A root restored from the database into the index.</summary>
public sealed class LoadedRoot
{
    public required string Path { get; init; }
    public RootKind Kind { get; init; }
    public ulong JournalId { get; init; }
    public long NextUsn { get; init; }
    /// <summary>The stamp it was saved with; 0 for databases from before stamps (taken as current).</summary>
    public ulong RulesStamp { get; init; }
    /// <summary>Its entries were saved with dates accessed (version 3 and later).</summary>
    public bool HasDateAccessed { get; init; }
    public int RootId { get; init; }
    /// <summary>Index ids of the root's entries in saved order (entry 0 is the root).</summary>
    public required int[] Ids { get; init; }
    /// <summary>Record number per entry (parallel to <see cref="Ids"/>) for NTFS roots, else null.</summary>
    public long[]? Frns { get; init; }
}

/// <summary>
/// Binary database: 8-byte magic + int32 version, then a Brotli-compressed body with, per root, its path, kind,
/// journal id/next USN, (version 2) the stamp of its exclusion rules, (version 3) a flags byte, and its live entries
/// (parent index, name, attributes, size, modified, created[, accessed when flagged][, FRN]). Folder sizes aren't
/// saved: they are worked out from the files when loaded. Written to a temp file and moved into place. Versions 1
/// and 2 load too.
/// </summary>
public static class IndexDatabase
{
    static ReadOnlySpan<byte> Magic => "ANYTHGDB"u8;
    public const int Version = 3;
    const byte FlagDateAccessed = 1;
    const int EndMarker = 0x21444E45; // "END!"

    public static void Save(string path, FileIndex index, IReadOnlyList<DbRoot> roots)
    {
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            fs.Write(Magic);
            fs.Write(BitConverter.GetBytes(Version));
            using var brotli = new BrotliStream(fs, CompressionLevel.Fastest, leaveOpen: true);
            using var buffered = new BufferedStream(brotli, 1 << 20);
            using var w = new BinaryWriter(buffered, Encoding.UTF8, leaveOpen: true);

            index.Lock.EnterReadLock();
            try
            {
                int count = index.Count;
                // Root of every live entry (memoized walk up the parent chain).
                var rootOf = new int[count];
                Array.Fill(rootOf, -2);
                var chain = new List<int>();
                for (int id = 0; id < count; id++)
                {
                    if (rootOf[id] != -2 || !index.IsAlive(id)) continue;
                    chain.Clear();
                    int cur = id, root = -1;
                    while (true)
                    {
                        if ((uint)cur >= (uint)count || !index.IsAlive(cur)) { root = -1; break; }
                        if (rootOf[cur] != -2) { root = rootOf[cur]; break; }
                        chain.Add(cur);
                        int p = index.Parent(cur);
                        if (p == FileIndex.NoParent) { root = cur; break; }
                        if (chain.Count > 4096) { root = -1; break; } // cycle guard
                        cur = p;
                    }
                    foreach (int c in chain) rootOf[c] = root;
                }

                var local = new int[count];
                var ids = new List<int>();
                var valid = roots.Where(r => r.RootId >= 0 && r.RootId < count && index.IsAlive(r.RootId)).ToList();
                w.Write(valid.Count);
                foreach (var r in valid)
                {
                    ids.Clear();
                    ids.Add(r.RootId);
                    for (int id = 0; id < count; id++)
                        if (id != r.RootId && rootOf[id] == r.RootId) ids.Add(id);
                    for (int i = 0; i < ids.Count; i++) local[ids[i]] = i;

                    w.Write(r.Path);
                    w.Write((byte)r.Kind);
                    w.Write(r.JournalId);
                    w.Write(r.NextUsn);
                    w.Write(r.RulesStamp);
                    bool accessed = r.HasDateAccessed && index.HasDateAccessed;
                    w.Write(accessed ? FlagDateAccessed : (byte)0);
                    w.Write(ids.Count);
                    bool frns = r.Kind == RootKind.Ntfs;
                    for (int i = 0; i < ids.Count; i++)
                    {
                        int id = ids[i];
                        int p = index.Parent(id);
                        w.Write(i == 0 ? -1 : local[p]);
                        w.Write(index.Name(id));
                        w.Write((uint)index.Attributes(id));
                        w.Write(index.IsDirectory(id) ? -1L : index.Size(id));
                        w.Write(index.Modified(id));
                        w.Write(index.Created(id));
                        if (accessed) w.Write(index.Accessed(id));
                        if (frns) w.Write(r.IdToFrn != null && id < r.IdToFrn.Length ? r.IdToFrn[id] : -1L);
                    }
                }
                w.Write(EndMarker);
            }
            finally { index.Lock.ExitReadLock(); }
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Loads the database into <paramref name="index"/>. Returns null (and leaves the index untouched) when the file
    /// is missing, has an unknown version or is corrupt.
    /// </summary>
    public static List<LoadedRoot>? Load(string path, FileIndex index)
    {
        if (!File.Exists(path)) return null;
        List<RawRoot> raw;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            Span<byte> header = stackalloc byte[12];
            fs.ReadExactly(header);
            int version = BitConverter.ToInt32(header[8..]);
            if (!header[..8].SequenceEqual(Magic) || version is < 1 or > Version) return null;
            using var brotli = new BrotliStream(fs, CompressionMode.Decompress);
            using var buffered = new BufferedStream(brotli, 1 << 20);
            using var r = new BinaryReader(buffered, Encoding.UTF8);
            int rootCount = r.ReadInt32();
            if (rootCount < 0 || rootCount > 1024) return null;
            raw = new List<RawRoot>(rootCount);
            for (int ri = 0; ri < rootCount; ri++)
            {
                var root = new RawRoot
                {
                    Path = r.ReadString(),
                    Kind = (RootKind)r.ReadByte(),
                    JournalId = r.ReadUInt64(),
                    NextUsn = r.ReadInt64(),
                    RulesStamp = version >= 2 ? r.ReadUInt64() : 0,
                };
                if (root.Kind is not (RootKind.Ntfs or RootKind.Folder)) return null;
                byte flags = version >= 3 ? r.ReadByte() : (byte)0;
                if ((flags & ~FlagDateAccessed) != 0) return null;
                int n = r.ReadInt32();
                if (n < 1 || n > 200_000_000) return null;
                bool frns = root.Kind == RootKind.Ntfs;
                root.Parents = new int[n]; root.Names = new string[n]; root.Attrs = new uint[n];
                root.Sizes = new long[n]; root.Modified = new long[n]; root.Created = new long[n];
                root.Accessed = (flags & FlagDateAccessed) != 0 ? new long[n] : null;
                root.Frns = frns ? new long[n] : null;
                for (int i = 0; i < n; i++)
                {
                    int p = r.ReadInt32();
                    if (i == 0 ? p != -1 : (p < 0 || p >= n || p == i)) return null;
                    root.Parents[i] = p;
                    root.Names[i] = r.ReadString();
                    root.Attrs[i] = r.ReadUInt32();
                    root.Sizes[i] = r.ReadInt64();
                    root.Modified[i] = r.ReadInt64();
                    root.Created[i] = r.ReadInt64();
                    if (root.Accessed != null) root.Accessed[i] = r.ReadInt64();
                    if (frns) root.Frns![i] = r.ReadInt64();
                }
                if (!ParentsAreAcyclic(root.Parents)) return null;
                raw.Add(root);
            }
            if (r.ReadInt32() != EndMarker) return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // corrupt or unreadable: caller rescans
        }

        var result = new List<LoadedRoot>(raw.Count);
        long total = index.Count;
        foreach (var root in raw) total += root.Names.Length;
        index.EnsureCapacity((int)Math.Min(int.MaxValue - 64, total + total / 32));
        index.BulkAdd(b =>
        {
            foreach (var root in raw)
            {
                int n = root.Names.Length;
                var ids = new int[n];
                List<int>? forward = null;
                for (int i = 0; i < n; i++)
                {
                    int p = root.Parents[i];
                    int parentId = p < 0 ? FileIndex.NoParent : p < i ? ids[p] : FileIndex.NoParent;
                    if (p > i) (forward ??= []).Add(i);
                    ids[i] = b.Add(root.Names[i], parentId, (FileAttributes)root.Attrs[i], root.Sizes[i], root.Modified[i], root.Created[i],
                        root.Accessed?[i] ?? 0);
                }
                if (forward != null)
                    foreach (int i in forward) b.SetParent(ids[i], ids[root.Parents[i]]);
                result.Add(new LoadedRoot
                {
                    Path = root.Path,
                    Kind = root.Kind,
                    JournalId = root.JournalId,
                    NextUsn = root.NextUsn,
                    RulesStamp = root.RulesStamp,
                    // Only what the index keeps: dates loaded while date accessed isn't indexed are dropped.
                    HasDateAccessed = root.Accessed != null && index.HasDateAccessed,
                    RootId = ids[0],
                    Ids = ids,
                    Frns = root.Frns,
                });
            }
        });
        return result;
    }

    /// <summary>True when every entry's parent chain reaches entry 0 (no cycles from forward references).</summary>
    static bool ParentsAreAcyclic(int[] parents)
    {
        var state = new byte[parents.Length]; // 0 unknown, 1 on current path, 2 reaches root
        state[0] = 2;
        var path = new List<int>();
        for (int i = 1; i < parents.Length; i++)
        {
            if (state[i] == 2) continue;
            path.Clear();
            int cur = i;
            while (state[cur] == 0) { state[cur] = 1; path.Add(cur); cur = parents[cur]; }
            if (state[cur] == 1) return false;
            foreach (int x in path) state[x] = 2;
        }
        return true;
    }

    sealed class RawRoot
    {
        public string Path = "";
        public RootKind Kind;
        public ulong JournalId;
        public long NextUsn;
        public ulong RulesStamp;
        public int[] Parents = [];
        public string[] Names = [];
        public uint[] Attrs = [];
        public long[] Sizes = [];
        public long[] Modified = [];
        public long[] Created = [];
        public long[]? Accessed;
        public long[]? Frns;
    }
}

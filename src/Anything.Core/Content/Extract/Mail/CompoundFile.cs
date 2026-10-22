using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Compound File Binary (OLE) reader: FAT, DIFAT, mini-FAT and directory, with visited sets. The header's sector shifts
/// are validated (9 or 12, mini 6), at most 2^20 sectors are addressed, FAT sectors are loaded only when a chain needs
/// them, every chain is followed with a visited bitset (a loop ends the chain), and the directory's sibling tree is walked
/// iteratively with a visited bitset too. Stream sizes are checked against the sectors there are.
/// </summary>
internal sealed class CompoundFile
{
    /// <summary>Most sectors addressed.</summary>
    public const int MaxSectors = 1 << 20;
    /// <summary>Most directory bytes read (131,072 entries).</summary>
    public const int MaxDirectoryBytes = 16 << 20;

    const uint EndOfChain = 0xFFFFFFFE, FreeSector = 0xFFFFFFFF;
    const int NoStream = -1;
    static ReadOnlySpan<byte> Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Kind of a directory entry.</summary>
    public enum EntryType : byte { Unknown = 0, Storage = 1, Stream = 2, Root = 5 }

    readonly IByteSource _source;
    readonly ExtractBudget _budget;
    readonly int _sectorSize, _sectorCount, _perSector;
    readonly uint _miniCutoff;
    readonly bool _version3;
    readonly List<uint> _fatSectors;
    readonly uint[]?[] _fat;
    readonly byte[] _directory;
    readonly int _entries;
    List<uint>? _miniFatSectors, _miniContainer;
    readonly uint _firstMiniFat;
    readonly int _miniFatCount;
    readonly Dictionary<int, uint[]> _miniFat = [];

    CompoundFile(IByteSource source, ExtractBudget budget, int sectorSize, int sectorCount, uint miniCutoff, bool version3,
        List<uint> fatSectors, uint firstDirectory, uint firstMiniFat, int miniFatCount)
    {
        _source = source;
        _budget = budget;
        _sectorSize = sectorSize;
        _sectorCount = sectorCount;
        _perSector = sectorSize / 4;
        _miniCutoff = miniCutoff;
        _version3 = version3;
        _fatSectors = fatSectors;
        _fat = new uint[]?[fatSectors.Count];
        _firstMiniFat = firstMiniFat;
        _miniFatCount = miniFatCount;
        _directory = ReadChain(firstDirectory, MaxDirectoryBytes, exact: false) ?? throw new InvalidDataException("No directory.");
        _entries = _directory.Length / 128;
        if (_entries == 0 || Type(0) != EntryType.Root) throw new InvalidDataException("No root entry.");
    }

    /// <summary>The file starts with the compound file signature.</summary>
    public static bool IsCompoundFile(IByteSource source)
    {
        Span<byte> head = stackalloc byte[8];
        return source.TryRead(0, head) && head.SequenceEqual(Signature);
    }

    /// <summary>Opens a compound file; null when its header or directory isn't valid.</summary>
    public static CompoundFile? TryOpen(IByteSource source, ExtractBudget budget)
    {
        try { return Open(source, budget); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Opens a compound file; InvalidDataException when its header or directory isn't valid.</summary>
    public static CompoundFile Open(IByteSource source, ExtractBudget budget)
    {
        source.PreferExactRanges();
        var h = source.ReadBytes(0, 512, 512) ?? throw new InvalidDataException("Too short.");
        if (!h.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("Not a compound file.");
        int major = U16(h, 0x1A), byteOrder = U16(h, 0x1C), shift = U16(h, 0x1E), miniShift = U16(h, 0x20);
        if (byteOrder != 0xFFFE || shift is not (9 or 12) || miniShift != 6 || major is not (3 or 4))
            throw new InvalidDataException("Bad sector sizes.");
        int sectorSize = 1 << shift;
        long sectors = (source.Length - sectorSize + sectorSize - 1) / sectorSize;
        if (sectors <= 0) throw new InvalidDataException("No sectors.");
        int sectorCount = (int)Math.Min(sectors, MaxSectors);
        uint fatCount = U32(h, 0x2C), firstDirectory = U32(h, 0x30), miniCutoff = U32(h, 0x38), firstMiniFat = U32(h, 0x3C);
        uint miniFatCount = U32(h, 0x40), firstDifat = U32(h, 0x44), difatCount = U32(h, 0x48);
        if (miniCutoff != 4096) throw new InvalidDataException("Bad mini stream cutoff.");
        int perSector = sectorSize / 4;
        long maxFat = (sectorCount + perSector - 1) / perSector + 1;
        if (fatCount == 0 || fatCount > maxFat) throw new InvalidDataException("Bad FAT sector count.");

        // FAT sector ids: 109 in the header, then the DIFAT chain (each sector ends with the next one's id).
        var fatSectors = new List<uint>((int)fatCount);
        for (int i = 0; i < 109 && fatSectors.Count < fatCount; i++)
        {
            uint s = U32(h, 0x4C + i * 4);
            if (s >= sectorCount) throw new InvalidDataException("Bad FAT sector.");
            fatSectors.Add(s);
        }
        var seen = new BitArray(sectorCount);
        uint difat = firstDifat;
        var block = new byte[sectorSize];
        for (uint n = 0; fatSectors.Count < fatCount && n < difatCount; n++)
        {
            budget.Check();
            if (difat >= sectorCount || seen[(int)difat]) throw new InvalidDataException("Bad DIFAT chain.");
            seen[(int)difat] = true;
            if (!source.TryRead((difat + 1L) * sectorSize, block)) throw new InvalidDataException("DIFAT outside the file.");
            for (int i = 0; i < perSector - 1 && fatSectors.Count < fatCount; i++)
            {
                uint s = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i * 4));
                if (s >= sectorCount) throw new InvalidDataException("Bad FAT sector.");
                fatSectors.Add(s);
            }
            difat = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(sectorSize - 4));
        }
        if (fatSectors.Count < fatCount) throw new InvalidDataException("Missing FAT sectors.");
        if (miniFatCount > sectorCount) throw new InvalidDataException("Bad mini FAT count.");
        return new CompoundFile(source, budget, sectorSize, sectorCount, miniCutoff, major == 3, fatSectors, firstDirectory, firstMiniFat, (int)miniFatCount);
    }

    static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    /// <summary>The root storage's entry id.</summary>
    public int Root => 0;

    /// <summary>Directory entries there are.</summary>
    public int EntryCount => _entries;

    public EntryType Type(int id) => id >= 0 && id < _entries ? (EntryType)_directory[id * 128 + 66] : EntryType.Unknown;

    /// <summary>An entry's name ("" when invalid).</summary>
    public string Name(int id)
    {
        if (id < 0 || id >= _entries) return "";
        int length = BinaryPrimitives.ReadUInt16LittleEndian(_directory.AsSpan(id * 128 + 64));
        length = Math.Clamp(length, 0, 64);
        var name = Encoding.Unicode.GetString(_directory, id * 128, length & ~1);
        return name.TrimEnd('\0');
    }

    /// <summary>A stream's size in bytes (version 3 files use only the low 32 bits).</summary>
    public long Size(int id)
    {
        if (id < 0 || id >= _entries) return 0;
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(_directory.AsSpan(id * 128 + 120));
        if (_version3) size &= 0xFFFFFFFF;
        return (long)Math.Min(size, long.MaxValue);
    }

    uint StartSector(int id) => BinaryPrimitives.ReadUInt32LittleEndian(_directory.AsSpan(id * 128 + 116));
    int Sibling(int id, int field) => (int)BinaryPrimitives.ReadUInt32LittleEndian(_directory.AsSpan(id * 128 + field)) is int v && v >= 0 && v < _entries ? v : NoStream;

    /// <summary>
    /// The entries directly inside a storage, in directory-tree order: an iterative walk of its sibling tree with a
    /// visited bitset, so a cycle or a shared node can't loop.
    /// </summary>
    public List<int> Children(int storage)
    {
        var list = new List<int>();
        if (storage < 0 || storage >= _entries || Type(storage) is not (EntryType.Storage or EntryType.Root)) return list;
        var visited = new BitArray(_entries);
        var stack = new Stack<int>();
        int node = Sibling(storage, 76);
        while (node != NoStream || stack.Count > 0)
        {
            while (node != NoStream && !visited[node])
            {
                visited[node] = true;
                stack.Push(node);
                node = Sibling(node, 68); // left
            }
            if (stack.Count == 0) break;
            node = stack.Pop();
            if ((list.Count & 255) == 0) _budget.Check();
            list.Add(node);
            node = Sibling(node, 72); // right
        }
        return list;
    }

    /// <summary>The child of a storage with this name (case ignored, as compound files compare names); -1 when none.</summary>
    public int Find(int storage, string name)
    {
        foreach (var id in Children(storage))
            if (string.Equals(Name(id), name, StringComparison.OrdinalIgnoreCase)) return id;
        return -1;
    }

    /// <summary>An Office file encrypted with a password keeps its package in an EncryptedPackage stream.</summary>
    public bool HasEncryptedPackage() => Find(Root, "EncryptedPackage") >= 0 || Find(Root, "EncryptionInfo") >= 0;

    /// <summary>A stream's bytes, at most max (the rest left unread); null when its chain is broken.</summary>
    public byte[]? ReadStream(int id, long max)
    {
        if (Type(id) != EntryType.Stream) return null;
        long size = Math.Min(Size(id), max);
        if (size <= 0) return [];
        uint start = StartSector(id);
        return Size(id) < _miniCutoff ? ReadMini(start, (int)size) : ReadChain(start, size, exact: true);
    }

    uint Next(uint sector)
    {
        int k = (int)(sector / (uint)_perSector);
        if (k >= _fat.Length) return FreeSector;
        var table = _fat[k];
        if (table == null)
        {
            var bytes = _source.ReadBytes((_fatSectors[k] + 1L) * _sectorSize, _sectorSize, _sectorSize) ?? throw new InvalidDataException("FAT outside the file.");
            table = new uint[_perSector];
            for (int i = 0; i < _perSector; i++) table[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
            _fat[k] = table;
        }
        return table[sector % (uint)_perSector];
    }

    /// <summary>The sector ids of a chain, at most count, following the FAT with a visited bitset.</summary>
    List<uint>? Chain(uint start, long count)
    {
        var list = new List<uint>((int)Math.Min(count, 4096));
        var visited = new BitArray(_sectorCount);
        uint s = start;
        while (list.Count < count && s != EndOfChain)
        {
            if (s >= _sectorCount || visited[(int)s]) return list.Count > 0 ? list : null;
            visited[(int)s] = true;
            list.Add(s);
            if ((list.Count & 1023) == 0) _budget.Check();
            s = Next(s);
        }
        return list;
    }

    /// <summary>
    /// The bytes of a chain of regular sectors: with exact, exactly size bytes or null when the chain is too short;
    /// without, as many sectors as the chain has, at most size bytes.
    /// </summary>
    byte[]? ReadChain(uint start, long size, bool exact)
    {
        long sectors = (size + _sectorSize - 1) / _sectorSize;
        var chain = Chain(start, sectors);
        if (chain == null || (exact && chain.Count < sectors)) return null;
        long length = Math.Min(size, (long)chain.Count * _sectorSize);
        var data = new byte[length];
        for (int i = 0; i < chain.Count; i++)
        {
            int at = i * _sectorSize, n = (int)Math.Min(_sectorSize, length - at);
            if (n <= 0) break;
            if (_source.ReadFully((chain[i] + 1L) * _sectorSize, data.AsSpan(at, n)) < n)
            {
                if (exact) return null;
                Array.Resize(ref data, at);
                break;
            }
        }
        return data;
    }

    /// <summary>A stream of the mini stream (64-byte sectors inside the root entry's stream, chained by the mini FAT).</summary>
    byte[]? ReadMini(uint start, int size)
    {
        _miniContainer ??= Chain(StartSector(Root), (Size(Root) + _sectorSize - 1) / _sectorSize) ?? [];
        _miniFatSectors ??= _miniFatCount == 0 ? [] : Chain(_firstMiniFat, _miniFatCount) ?? [];
        int miniSectors = (int)Math.Min((long)_miniContainer.Count * _sectorSize / 64, int.MaxValue);
        var data = new byte[size];
        var visited = new BitArray(Math.Max(1, miniSectors));
        uint m = start;
        for (int at = 0; at < size; at += 64)
        {
            if (m >= miniSectors || visited[(int)m]) return null;
            visited[(int)m] = true;
            long offset = (long)m * 64;
            int container = (int)(offset / _sectorSize), within = (int)(offset % _sectorSize);
            int n = Math.Min(64, size - at);
            if (_source.ReadFully((_miniContainer[container] + 1L) * _sectorSize + within, data.AsSpan(at, n)) < n) return null;
            m = NextMini(m);
        }
        return data;
    }

    uint NextMini(uint m)
    {
        int k = (int)(m / (uint)_perSector);
        if (_miniFatSectors == null || k >= _miniFatSectors.Count) return EndOfChain;
        if (!_miniFat.TryGetValue(k, out var table))
        {
            var bytes = _source.ReadBytes((_miniFatSectors[k] + 1L) * _sectorSize, _sectorSize, _sectorSize);
            if (bytes == null) return EndOfChain;
            table = new uint[_perSector];
            for (int i = 0; i < _perSector; i++) table[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
            _miniFat[k] = table;
        }
        return table[m % (uint)_perSector];
    }
}

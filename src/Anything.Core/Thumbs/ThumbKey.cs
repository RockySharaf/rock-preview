using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Anything.Core.Content.Store;

namespace Anything.Core.Thumbs;

/// <summary>
/// What a file was when its picture was made: its length and last-write time (FILETIME ticks, UTC, as FileIndex.Modified
/// holds it); a folder has length 0. Both 0 means "not known yet": the worker stats the file and makes the real key.
/// </summary>
public readonly record struct ThumbStamp(long Length, long LastWrite)
{
    /// <summary>Not known (the index had no entry): the worker reads the file's stamp before it looks anything up.</summary>
    public static ThumbStamp Unknown => default;

    /// <summary>Whether the stamp still has to be read from the file.</summary>
    public bool IsUnknown => Length == 0 && LastWrite == 0;

    /// <summary>A folder's stamp: no length, its last-write time.</summary>
    public static ThumbStamp ForFolder(long lastWrite) => new(0, lastWrite);
}

/// <summary>The two master sizes: one stored picture per file and class, scaled down to whatever the grid shows.</summary>
public static class ThumbSizeClass
{
    /// <summary>The class for grid thumbnails up to 256 px.</summary>
    public const int Small = 256;
    /// <summary>The class for grid thumbnails over 256 px.</summary>
    public const int Large = 512;

    /// <summary>The class a grid thumbnail size is made from: 256 when the size is 256 or less, else 512.</summary>
    public static int For(int displaySize) => displaySize <= Small ? Small : Large;
}

/// <summary>
/// One picture's identity: the full path (upper-case invariant), the stamp and the size class, with two independent
/// hashes of the same bytes. <see cref="Hash64"/> (the first 8 bytes of a SHA-256) finds the record and
/// <see cref="Check32"/> (a CRC-32C) proves it belongs to this key, so the store never names a file. Immutable and safe to
/// share between threads; making one costs about a microsecond (fine on the UI thread for a screen of cells).
/// </summary>
public readonly record struct ThumbKey
{
    /// <summary>The full path, upper-case invariant (the form that is hashed).</summary>
    public string Path { get; }
    /// <summary>The file's length and last-write time when the picture was asked for.</summary>
    public ThumbStamp Stamp { get; }
    /// <summary>256 or 512 (<see cref="ThumbSizeClass"/>).</summary>
    public int SizeClass { get; }
    /// <summary>The store's lookup hash.</summary>
    public ulong Hash64 { get; }
    /// <summary>The second, independent hash a stored record must also match.</summary>
    public uint Check32 { get; }

    ThumbKey(string path, ThumbStamp stamp, int sizeClass, ulong hash64, uint check32)
    {
        Path = path;
        Stamp = stamp;
        SizeClass = sizeClass;
        Hash64 = hash64;
        Check32 = check32;
    }

    /// <summary>The key of <paramref name="path"/> at <paramref name="stamp"/> in the class for <paramref name="sizeClass"/> (any display size is rounded to its class).</summary>
    public static ThumbKey Of(string path, ThumbStamp stamp, int sizeClass)
    {
        string upper = (path ?? "").ToUpperInvariant();
        int cls = ThumbSizeClass.For(sizeClass);
        var (hash, check) = HashOf(upper, stamp, cls);
        return new ThumbKey(upper, stamp, cls, hash, check);
    }

    /// <summary>The same file and stamp in another size class.</summary>
    public ThumbKey WithSizeClass(int sizeClass) => Of(Path, Stamp, sizeClass);

    /// <summary>The same file in the same class with another stamp (the worker read the real one).</summary>
    public ThumbKey WithStamp(ThumbStamp stamp) => Of(Path, stamp, SizeClass);

    /// <summary>
    /// The hashes of the key's bytes: the path as UTF-16, then the length, the last-write time and the class, all
    /// little-endian. The layout is part of the store's format: changing it orphans every stored picture.
    /// </summary>
    internal static (ulong Hash64, uint Check32) HashOf(string upperPath, ThumbStamp stamp, int sizeClass)
    {
        int pathBytes = upperPath.Length * 2;
        int total = pathBytes + 8 + 8 + 4;
        byte[]? rented = total > 1024 ? ArrayPool<byte>.Shared.Rent(total) : null;
        Span<byte> bytes = rented != null ? rented.AsSpan(0, total) : stackalloc byte[total];
        try
        {
            MemoryMarshal.AsBytes(upperPath.AsSpan()).CopyTo(bytes);
            if (!BitConverter.IsLittleEndian)
                for (int i = 0; i < pathBytes; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
            BinaryPrimitives.WriteInt64LittleEndian(bytes[pathBytes..], stamp.Length);
            BinaryPrimitives.WriteInt64LittleEndian(bytes[(pathBytes + 8)..], stamp.LastWrite);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(pathBytes + 16)..], sizeClass);
            Span<byte> sha = stackalloc byte[32];
            SHA256.HashData(bytes, sha);
            ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(sha);
            // 0 marks an empty slot in the in-flight marker; a real key never hashes to it.
            if (hash == 0) hash = 1;
            return (hash, Crc32C.Compute(bytes));
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Hash64:x16}/{Check32:x8} ({SizeClass})";
}

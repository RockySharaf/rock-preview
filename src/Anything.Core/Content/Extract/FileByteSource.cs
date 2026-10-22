using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Anything.Core.Indexing;
using Microsoft.Win32.SafeHandles;

namespace Anything.Core.Content.Extract;

/// <summary>
/// A file through a small cache of read ranges, \\?\ path, positioned ReadFile on a synchronous handle; counts the bytes
/// actually read. How much one read fetches adapts to the reader: exact ranges (rounded to 512 bytes, at least 4 KB,
/// growing like blocks while reads go on sequentially) for header readers (<see cref="ExactRanges"/>); otherwise 64 KB
/// aligned blocks on local disks, and on network shares 16 KB that doubles while reads go on sequentially, up to 256 KB.
/// Neighbouring ranges are merged, and at most 32 ranges and 2 MB are kept. <see cref="BytesRead"/> and the network
/// throttle count the bytes fetched; the read budget (MaxBytesRead) is charged with the bytes the reader asked for, and on
/// a share with the bytes fetched when that is more (see <see cref="Charge"/>).
/// </summary>
internal sealed class FileByteSource : IByteSource
{
    const int LocalBlock = 64 << 10, NetworkFirst = 16 << 10, NetworkMost = 256 << 10, ExactLeast = 4 << 10;
    const int Sector = 512, MaxRanges = 32, MaxMerged = 256 << 10;
    const long CacheBytes = 2 << 20, DirectAbove = 1 << 20;

    readonly SafeFileHandle _handle;
    readonly ExtractBudget _budget;
    readonly bool _network;
    readonly string? _share;
    readonly NetworkThrottle? _throttle;
    readonly List<Range> _ranges = []; // most recently used last
    long _read, _asked, _charged, _cached, _sequentialEnd = -1;
    int _networkBlock = NetworkFirst, _exactRun = ExactLeast;
    bool _disposed;

    sealed class Range(long start, byte[] data, int length)
    {
        public long Start = start;
        public byte[] Data = data;
        public int Length = length;
        public long End => Start + Length;
    }

    FileByteSource(SafeFileHandle handle, long length, ExtractBudget budget, bool network, string? share, NetworkThrottle? throttle)
    {
        _handle = handle;
        Length = length;
        _budget = budget;
        _network = network;
        _share = share;
        _throttle = throttle;
    }

    /// <summary>
    /// Opens for reading with Read|Write|Delete sharing and SetFileTime(-1) when allowed; returns a status when it can't.
    /// Whether the file is on a network share comes from its volume (<see cref="IsNetworkPath"/>), not the path's spelling.
    /// </summary>
    /// <returns>
    /// Null when opened. Otherwise AccessDenied (no permission to read), or TimedOut standing for temporary trouble (in
    /// use, offline, any other I/O error) that the caller sorts out by <paramref name="win32Error"/> and never records.
    /// </returns>
    public static DocStatus? TryOpen(string fullPath, ExtractBudget budget, out FileByteSource? source, out int win32Error)
        => TryOpen(fullPath, budget, IsNetworkPath(fullPath), null, out source, out win32Error);

    /// <summary>A path with each '/' written as a backslash (Native.LongPath adds the \\?\ prefix, after which Windows takes no forward slashes).</summary>
    internal static string Backslashes(string path) => path.Contains('/') ? path.Replace('/', '\\') : path;

    /// <summary>
    /// Whether a path is on a network share: \\server\share and \\?\UNC\ paths, and drive letters Windows reports as
    /// remote (a mapped Z:, from GetDriveType without touching the network). \\?\C:\ and \\?\Volume{…}\ paths are local.
    /// </summary>
    internal static bool IsNetworkPath(string fullPath)
    {
        var p = fullPath.AsSpan();
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) p = p[4..];
        else if (p.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        if (p.Length < 2 || p[1] != ':' || !char.IsAsciiLetter(p[0])) return false;
        try { return Drives.KindOf(p[..2].ToString()) == DriveKind.Network; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// <see cref="TryOpen(string, ExtractBudget, out FileByteSource?, out int)"/> for a file on a network share or not.
    /// A share that refused a write-attributes open once is opened read-only directly for a day (Date Accessed can't be
    /// kept there anyway, and the refused open costs a round trip and an audit event per file).
    /// </summary>
    public static DocStatus? TryOpen(string fullPath, ExtractBudget budget, bool network, NetworkThrottle? throttle,
        out FileByteSource? source, out int win32Error)
    {
        source = null;
        fullPath = Backslashes(fullPath);
        string longPath = Native.LongPath(fullPath);
        string? share = network ? ShareOf(fullPath) : null;
        bool tryWrite = share == null || WriteAttributeMemory.ShouldTry(share, Environment.TickCount64);
        SafeFileHandle? handle = null;
        if (tryWrite)
        {
            handle = Native.CreateFile(longPath, ReaderNative.GENERIC_READ | ReaderNative.FILE_WRITE_ATTRIBUTES, ReaderNative.FILE_SHARE_ALL, 0,
                ReaderNative.OPEN_EXISTING, ReaderNative.FILE_FLAG_OPEN_NO_RECALL, 0);
            if (handle.IsInvalid)
            {
                win32Error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                handle = null;
                if (win32Error != ReaderNative.ERROR_ACCESS_DENIED) return DocStatus.TimedOut;
            }
            else
            {
                ReaderNative.KeepAccessTime(handle);
                if (share != null) WriteAttributeMemory.Allowed(share);
            }
        }
        if (handle == null)
        {
            handle = Native.CreateFile(longPath, ReaderNative.GENERIC_READ, ReaderNative.FILE_SHARE_ALL, 0, ReaderNative.OPEN_EXISTING,
                ReaderNative.FILE_FLAG_OPEN_NO_RECALL, 0);
            if (handle.IsInvalid)
            {
                win32Error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                return win32Error == ReaderNative.ERROR_ACCESS_DENIED ? DocStatus.AccessDenied : DocStatus.TimedOut;
            }
            // Reading works without write-attributes access: remember that for the share (a local file's ACL is its own).
            if (tryWrite && share != null) WriteAttributeMemory.Denied(share, Environment.TickCount64);
        }
        if (!ReaderNative.TryStat(handle, out long length, out _, out _))
        {
            win32Error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return win32Error == ReaderNative.ERROR_ACCESS_DENIED ? DocStatus.AccessDenied : DocStatus.TimedOut;
        }
        win32Error = 0;
        source = new FileByteSource(handle, length, budget, network, share, throttle);
        return null;
    }

    /// <summary>The share a network path is on, as "\\SERVER\SHARE" (UNC) or "Z:" (a mapped letter); null for others.</summary>
    internal static string? ShareOf(string fullPath)
    {
        var p = fullPath.AsSpan();
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..].ToString();
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        if (p.Length >= 2 && p[1] == ':') return p[..2].ToString().ToUpperInvariant();
        if (!p.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var rest = p[2..];
        int server = rest.IndexOfAny('\\', '/');
        if (server <= 0) return null;
        int share = rest[(server + 1)..].IndexOfAny('\\', '/');
        var shareName = share < 0 ? rest : rest[..(server + 1 + share)];
        return (@"\\" + shareName.ToString()).ToUpperInvariant();
    }

    /// <inheritdoc/>
    public long Length { get; }

    /// <inheritdoc/>
    public long BytesRead => _read;

    /// <summary>
    /// Reads exactly what is asked (at least 4 KB, 512-byte aligned) instead of whole blocks: header readers (pictures,
    /// PSD, zip directories) that seek between small structures set it.
    /// </summary>
    public bool ExactRanges { get; set; }

    /// <summary>Size, date modified and attributes of the open file now (the second stat of stat-read-stat).</summary>
    public bool TryStat(out long size, out long modifiedUtc, out FileAttributes attributes)
    {
        bool ok = ReaderNative.TryStat(_handle, out size, out modifiedUtc, out uint a);
        attributes = (FileAttributes)a;
        return ok;
    }

    /// <inheritdoc/>
    public int ReadAt(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _budget.Check();
        if (offset >= Length || buffer.IsEmpty) return 0;
        int n = (int)Math.Min(buffer.Length, Length - offset);
        int done = 0;
        while (done < n)
        {
            long at = offset + done;
            var hit = Find(at);
            if (hit != null)
            {
                int from = (int)(at - hit.Start), count = Math.Min(n - done, hit.Length - from);
                hit.Data.AsSpan(from, count).CopyTo(buffer[done..]);
                done += count;
                Touch(hit);
                continue;
            }
            int got = Fetch(at, offset + n, buffer[done..n]);
            if (got <= 0) break; // the file got shorter since it was opened
            done += got;
        }
        Charge(done);
        return done;
    }

    /// <summary>
    /// Charges the read budget. Locally with the bytes the reader asked for: a file is fetched in 64 KB blocks, so a reader
    /// that goes between small objects (a scanned PDF's pages beside their pictures) fetches about ten times what it reads,
    /// and a 1,509-page scan that needed 7.4 MB reached the 64 MB limit. On a share the limit bounds what crosses the
    /// network too: the larger of the bytes asked for and the bytes fetched.
    /// </summary>
    void Charge(int asked)
    {
        _asked += asked;
        long due = _network ? Math.Max(_asked, _read) : _asked;
        if (due <= _charged) return;
        long now = due - _charged;
        _charged = due;
        _budget.Read(now);
    }

    Range? Find(long at)
    {
        for (int i = _ranges.Count - 1; i >= 0; i--)
            if (at >= _ranges[i].Start && at < _ranges[i].End) return _ranges[i];
        return null;
    }

    void Touch(Range r)
    {
        if (_ranges[^1] == r) return;
        _ranges.Remove(r);
        _ranges.Add(r);
    }

    /// <summary>Reads the range around [at, end) that the policy chooses; copies what the caller asked for and returns its length.</summary>
    int Fetch(long at, long end, Span<byte> into)
    {
        long start, stop;
        if (ExactRanges)
        {
            // A read that goes on where the last one stopped (a compound file's sector chain, PSD layer records) grows as
            // blocks do, up to 64 KB locally and 256 KB on a share (16 KB at once there: one round trip per 4 KB costs
            // far more than the bytes); a header reader that seeks still reads only what it asks for.
            _exactRun = at == _sequentialEnd
                ? Math.Min(Math.Max(_exactRun * 2, _network ? NetworkFirst : ExactLeast * 2), _network ? NetworkMost : LocalBlock)
                : ExactLeast;
            start = at / Sector * Sector;
            stop = Math.Max(end, start + _exactRun);
        }
        else if (_network)
        {
            _networkBlock = at == _sequentialEnd ? Math.Min(_networkBlock * 2, NetworkMost) : NetworkFirst;
            start = at / (4 << 10) * (4 << 10);
            stop = Math.Max(end, at + _networkBlock);
        }
        else
        {
            start = at / LocalBlock * LocalBlock;
            stop = Math.Max(end, start + LocalBlock);
        }
        stop = Math.Min((stop + Sector - 1) / Sector * Sector, Length);
        // Never read again what is cached on either side.
        foreach (var r in _ranges)
        {
            if (r.End <= at && r.End > start) start = r.End;
            if (r.Start > at && r.Start < stop) stop = r.Start;
        }
        _sequentialEnd = stop;

        // A big read goes straight into the caller's buffer, uncached.
        if (end - at > DirectAbove)
        {
            int direct = ReadDisk(at, into[..(int)Math.Min(into.Length, stop - at)]);
            return direct;
        }

        int length = (int)(stop - start);
        var data = ArrayPool<byte>.Shared.Rent(length);
        int got;
        try { got = ReadDisk(start, data.AsSpan(0, length)); }
        catch { ArrayPool<byte>.Shared.Return(data); throw; }
        if (got <= at - start) { ArrayPool<byte>.Shared.Return(data); return 0; }
        int count = (int)Math.Min(into.Length, got - (at - start));
        data.AsSpan((int)(at - start), count).CopyTo(into);
        Keep(new Range(start, data, got));
        return count;
    }

    /// <summary>
    /// Reads from the file until the span is full or the file ends; counts the bytes fetched and charges the throttle
    /// (ReadAt charges the budget). On a share a big read (one straight into the caller's buffer, a whole text file) goes
    /// a network block at a time, each paid for at the share's speed limit first, so it is spread over time instead of
    /// taken in one rush.
    /// </summary>
    int ReadDisk(long offset, Span<byte> span)
    {
        int total = 0;
        while (total < span.Length)
        {
            int piece = _network ? Math.Min(span.Length - total, NetworkMost) : span.Length - total;
            if (_network && _throttle != null && _share != null) _budget.Waited(_throttle(_share, piece, _budget.Token));
            int got = 0;
            while (got < piece)
            {
                int n = ReaderNative.ReadAt(_handle, span.Slice(total + got, piece - got), offset + total + got, out int error);
                if (n < 0) throw new ContentIoException(error, "The file couldn't be read.");
                if (n == 0) break;
                got += n;
            }
            total += got;
            if (got < piece) break; // the file ends here
        }
        _read += total;
        return total;
    }

    /// <summary>Keeps a range, merged with a neighbour it touches, and drops the least recently used past the limits.</summary>
    void Keep(Range range)
    {
        foreach (var r in _ranges)
        {
            if (r.End == range.Start && r.Length + range.Length <= MaxMerged) { Merge(r, range, append: true); return; }
            if (range.End == r.Start && r.Length + range.Length <= MaxMerged) { Merge(r, range, append: false); return; }
        }
        _ranges.Add(range);
        _cached += range.Length;
        Trim();
    }

    void Merge(Range kept, Range next, bool append)
    {
        int total = kept.Length + next.Length;
        var data = ArrayPool<byte>.Shared.Rent(total);
        var first = append ? kept : next;
        var second = append ? next : kept;
        first.Data.AsSpan(0, first.Length).CopyTo(data);
        second.Data.AsSpan(0, second.Length).CopyTo(data.AsSpan(first.Length));
        ArrayPool<byte>.Shared.Return(kept.Data);
        ArrayPool<byte>.Shared.Return(next.Data);
        _cached += next.Length;
        kept.Start = first.Start;
        kept.Data = data;
        kept.Length = total;
        Touch(kept);
        Trim();
    }

    void Trim()
    {
        while (_ranges.Count > 1 && (_ranges.Count > MaxRanges || _cached > CacheBytes))
        {
            var old = _ranges[0];
            _ranges.RemoveAt(0);
            _cached -= old.Length;
            ArrayPool<byte>.Shared.Return(old.Data);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var r in _ranges) ArrayPool<byte>.Shared.Return(r.Data);
        _ranges.Clear();
        _handle.Dispose();
    }
}

/// <summary>
/// Shares that refused an open with FILE_WRITE_ATTRIBUTES (read-only shares, typical for print archives): opened
/// read-only directly, and tried again a day later.
/// </summary>
internal static class WriteAttributeMemory
{
    /// <summary>How long a refusal is remembered.</summary>
    public const long ReprobeMs = 24L * 60 * 60 * 1000;

    static readonly ConcurrentDictionary<string, long> s_deniedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether to ask for write-attributes access on this share now.</summary>
    public static bool ShouldTry(string share, long nowMs)
        => !s_deniedAt.TryGetValue(share, out long at) || nowMs - at >= ReprobeMs;

    /// <summary>The share refused write-attributes access but allowed reading.</summary>
    public static void Denied(string share, long nowMs) => s_deniedAt[share] = nowMs;

    /// <summary>The share allowed write-attributes access (a re-probe after a day succeeded).</summary>
    public static void Allowed(string share) => s_deniedAt.TryRemove(share, out _);

    /// <summary>Forgets everything (tests).</summary>
    internal static void Clear() => s_deniedAt.Clear();
}

/// <summary>
/// A window of another byte source (an EXIF block inside a JPEG segment, a zip entry's bytes): offsets are relative to
/// its start and nothing outside it can be read, so offsets in the block stay bounded by the block.
/// </summary>
internal sealed class ByteSourceSlice(IByteSource parent, long start, long length) : IByteSource
{
    /// <inheritdoc/>
    public long Length { get; } = Math.Max(0, Math.Min(length, parent.Length - start));

    /// <inheritdoc/>
    public long BytesRead => parent.BytesRead;

    /// <inheritdoc/>
    public int ReadAt(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length || buffer.IsEmpty) return 0;
        int n = (int)Math.Min(buffer.Length, Length - offset);
        return parent.ReadAt(start + offset, buffer[..n]);
    }

    /// <summary>The parent is not owned.</summary>
    public void Dispose() { }
}

/// <summary>Small helpers readers use on byte sources: exact reads, numbers in either byte order, bounded copies.</summary>
internal static class SourceBytes
{
    /// <summary>Reads until the buffer is full or the source ends; the count read.</summary>
    public static int ReadFully(this IByteSource source, long offset, Span<byte> buffer)
    {
        if (offset < 0) return 0;
        int total = 0;
        while (total < buffer.Length)
        {
            int n = source.ReadAt(offset + total, buffer[total..]);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>Reads exactly buffer.Length bytes or returns false.</summary>
    public static bool TryRead(this IByteSource source, long offset, Span<byte> buffer)
        => offset >= 0 && offset <= source.Length - buffer.Length && source.ReadFully(offset, buffer) == buffer.Length;

    /// <summary>A copy of [offset, offset + count) when it lies inside the source and count is at most max; null otherwise.</summary>
    public static byte[]? ReadBytes(this IByteSource source, long offset, long count, long max)
    {
        if (count < 0 || count > max || offset < 0 || offset > source.Length - count) return null;
        var bytes = new byte[count];
        return source.ReadFully(offset, bytes) == count ? bytes : null;
    }

    public static bool TryU16(this IByteSource s, long offset, bool bigEndian, out int value)
    {
        Span<byte> b = stackalloc byte[2];
        if (!s.TryRead(offset, b)) { value = 0; return false; }
        value = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);
        return true;
    }

    public static bool TryU32(this IByteSource s, long offset, bool bigEndian, out uint value)
    {
        Span<byte> b = stackalloc byte[4];
        if (!s.TryRead(offset, b)) { value = 0; return false; }
        value = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
        return true;
    }

    public static bool TryU64(this IByteSource s, long offset, bool bigEndian, out ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        if (!s.TryRead(offset, b)) { value = 0; return false; }
        value = bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b);
        return true;
    }

    /// <summary>Switches a file source to exact-range reads for a header reader (other sources ignore it).</summary>
    public static void PreferExactRanges(this IByteSource source)
    {
        if (source is FileByteSource file) file.ExactRanges = true;
    }
}

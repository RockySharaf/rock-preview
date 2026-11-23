using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using Anything.Core.Content.Indexing;

namespace Anything.Core.Thumbs;

/// <summary>A key named by a marker slot: the picture a worker was making when the program last stopped unexpectedly.</summary>
public readonly record struct InFlightEntry(ulong Hash64, uint Check32);

/// <summary>
/// Thumbs\inflight.bin: one 32-byte slot per worker thread naming the key it is decoding or extracting, written before
/// the work and cleared after it, so a crash inside a decoder or a shell extension (which .NET can't catch) leaves the
/// key behind and the store records it as Crashed at the next start. Follows Content\Indexing\CrashMarker: the file is
/// mapped into memory (a slot write is a memory write, and the OS cache keeps it when the process dies), the header holds
/// the boot id so slots left by an earlier boot (a power cut) blame nothing, and a slot belongs to one thread instance:
/// <see cref="Leave"/> clears it only while it still carries that instance's epoch, so an abandoned thread that returns
/// late never clears its successor's slot. The file stays open with FileShare.Read, which also keeps a second process
/// from using the same store. Thread-safe; nothing here throws to callers.
/// </summary>
public sealed class ThumbMarker : IDisposable
{
    /// <summary>Slots: decode threads, shell threads and abandoned shell threads all fit.</summary>
    public const int Slots = 64;
    const int HeaderBytes = 64, SlotBytes = 32, FileBytes = 4096;
    internal static ReadOnlySpan<byte> Magic => "RPTHMRK1"u8;
    const ushort Version = 1;

    readonly FileStream? _stream;
    readonly MemoryMappedFile? _map;
    readonly MemoryMappedViewAccessor? _view;
    readonly object _gate = new();
    readonly bool[] _taken = new bool[Slots];
    readonly uint[] _epochs = new uint[Slots];
    readonly List<(int Slot, InFlightEntry Key)> _leftovers = [];
    uint _epoch;
    bool _quiet, _disposed;

    ThumbMarker(FileStream? stream, MemoryMappedFile? map, MemoryMappedViewAccessor? view)
    {
        _stream = stream;
        _map = map;
        _view = view;
    }

    /// <summary>A marker with no file (a store that stores nothing, tests): slots work, nothing survives.</summary>
    public static ThumbMarker InMemory() => new(null, null, null);

    /// <summary>Whether slots are written to a file.</summary>
    public bool HasFile => _view != null;

    /// <summary>
    /// Opens or creates the marker at <paramref name="path"/> and returns the keys a session of this boot left in its
    /// slots. Null when the file can't be opened (another process uses this store, or the folder can't be written).
    /// </summary>
    public static ThumbMarker? Open(string path, out IReadOnlyList<InFlightEntry> leftovers) => Open(path, out leftovers, out _);

    /// <summary>
    /// <see cref="Open(string, out IReadOnlyList{InFlightEntry})"/>, also saying why it failed: <paramref name="inUse"/>
    /// is true when another process holds the file (its store is open there), false for any other reason.
    /// </summary>
    public static ThumbMarker? Open(string path, out IReadOnlyList<InFlightEntry> leftovers, out bool inUse)
    {
        leftovers = [];
        inUse = false;
        FileStream? stream = null;
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 1, FileOptions.None);
            var old = new byte[FileBytes];
            int n = 0;
            while (n < old.Length)
            {
                int got = stream.Read(old, n, old.Length - n);
                if (got <= 0) break;
                n += got;
            }
            if (stream.Length < FileBytes) stream.SetLength(FileBytes);
            map = MemoryMappedFile.CreateFromFile(stream, null, FileBytes, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            view = map.CreateViewAccessor(0, FileBytes, MemoryMappedFileAccess.ReadWrite);
            var marker = new ThumbMarker(stream, map, view);
            var found = new List<InFlightEntry>();
            if (n >= HeaderBytes + Slots * SlotBytes && old.AsSpan(0, 8).SequenceEqual(Magic)
                && BinaryPrimitives.ReadUInt16LittleEndian(old.AsSpan(8)) == Version
                && CrashMarker.SameBoot(BinaryPrimitives.ReadUInt32LittleEndian(old.AsSpan(12)), BinaryPrimitives.ReadInt64LittleEndian(old.AsSpan(16))))
            {
                for (int i = 0; i < Slots; i++)
                    if (ReadSlot(old.AsSpan(HeaderBytes + i * SlotBytes)) is { } e)
                    {
                        // A new worker must not overwrite a key that hasn't made it into a pack yet.
                        marker._taken[i] = true;
                        marker._leftovers.Add((i, e));
                        if (!found.Contains(e)) found.Add(e);
                    }
            }
            // Slots naming keys stay until the store has recorded them (ClearLeftovers); anything else is wiped now.
            marker.WriteHeader(clearSlots: found.Count == 0);
            leftovers = found;
            return marker;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION: someone else has the file open.
            inUse = e is IOException && (e.HResult & 0xFFFF) is 32 or 33;
            view?.Dispose();
            map?.Dispose();
            stream?.Dispose();
            return null;
        }
    }

    /// <summary>A slot for one worker-thread instance; -1 when all are taken (then that worker works unmarked).</summary>
    public int Acquire()
    {
        lock (_gate)
        {
            for (int i = 0; i < Slots; i++)
                if (!_taken[i]) { _taken[i] = true; return i; }
            return -1;
        }
    }

    /// <summary>Gives a slot back (its thread ended) and clears it.</summary>
    public void Release(int slot)
    {
        if ((uint)slot >= Slots) return;
        lock (_gate)
        {
            _taken[slot] = false;
            _epochs[slot] = 0;
            ClearSlot(slot);
        }
    }

    /// <summary>Names the key a slot's thread starts making; returns the epoch to <see cref="Leave"/> with (0 for a bad slot).</summary>
    public uint Enter(int slot, in ThumbKey key) => Enter(slot, new InFlightEntry(key.Hash64, key.Check32));

    /// <summary>Names the key a slot's thread starts making (by its hashes); returns the epoch to leave with.</summary>
    public uint Enter(int slot, InFlightEntry entry)
    {
        if ((uint)slot >= Slots) return 0;
        lock (_gate)
        {
            uint epoch = ++_epoch == 0 ? ++_epoch : _epoch;
            _epochs[slot] = epoch;
            if (_view != null && !_quiet)
            {
                long at = HeaderBytes + slot * SlotBytes;
                try
                {
                    // The flag goes last, so a slot half written when the process dies reads as empty.
                    _view.Write(at + 8, (byte)0);
                    _view.Write(at, entry.Hash64);
                    _view.Write(at + 12, entry.Check32);
                    _view.Write(at + 16, epoch);
                    _view.Write(at + 8, (byte)1);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
            }
            return epoch;
        }
    }

    /// <summary>The work is done: clears the slot, unless it now carries another epoch (a later key's).</summary>
    public void Leave(int slot, uint epoch)
    {
        if ((uint)slot >= Slots) return;
        lock (_gate)
        {
            if (_epochs[slot] != epoch) return;
            _epochs[slot] = 0;
            ClearSlot(slot);
        }
    }

    /// <summary>The keys the slots name now (tests and diagnostics).</summary>
    public IReadOnlyList<InFlightEntry> Live()
    {
        var live = new List<InFlightEntry>();
        lock (_gate)
        {
            if (_view == null || _disposed) return live;
            var slot = new byte[SlotBytes];
            for (int i = 0; i < Slots; i++)
            {
                _view.ReadArray(HeaderBytes + i * SlotBytes, slot, 0, SlotBytes);
                if (ReadSlot(slot) is { } e) live.Add(e);
            }
        }
        return live;
    }

    // Remember which strike this was before the first append. If only part of a batch lands, its first strikes
    // must still be first strikes when the next Open retries the batch. Slot byte 9 was previously unused.
    internal StoredKind KindForLeftover(InFlightEntry key, StoredKind proposed)
    {
        int slot = _leftovers.First(p => p.Key == key).Slot;
        long at = HeaderBytes + slot * SlotBytes + 9;
        var kind = (StoredKind)_view!.ReadByte(at);
        if (kind is not (StoredKind.Suspect or StoredKind.Crashed)) kind = proposed;
        // Two workers can leave the same key. Either copy might survive an interrupted ClearLeftovers.
        foreach (var copy in _leftovers)
            if (copy.Key == key) _view.Write(HeaderBytes + copy.Slot * SlotBytes + 9, (byte)kind);
        return kind;
    }

    /// <summary>Gives the last session's slots back once every key they named has been recorded.</summary>
    public void ClearLeftovers()
    {
        lock (_gate)
        {
            foreach (var (slot, _) in _leftovers)
            {
                ClearSlot(slot);
                _taken[slot] = false;
            }
            _leftovers.Clear();
        }
    }

    void WriteHeader(bool clearSlots)
    {
        if (_view == null) return;
        var buffer = new byte[clearSlots ? FileBytes : HeaderBytes];
        Magic.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), Slots);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), ContentNative.BootId());
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(16), ContentNative.BootTimeUtc());
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), Environment.ProcessId);
        _view.WriteArray(0, buffer, 0, buffer.Length);
    }

    void ClearSlot(int slot)
    {
        if (_view == null || _disposed) return;
        try
        {
            long at = HeaderBytes + slot * SlotBytes;
            _view.Write(at + 8, (ushort)0); // clear the live flag and the saved strike together
            _view.Write(at, 0UL);
            _view.Write(at + 12, 0U);
            _view.Write(at + 16, 0U);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    static InFlightEntry? ReadSlot(ReadOnlySpan<byte> slot)
    {
        if (slot[8] != 1) return null;
        ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(slot);
        if (hash == 0) return null;
        return new InFlightEntry(hash, BinaryPrimitives.ReadUInt32LittleEndian(slot[12..]));
    }

    /// <summary>Closes the file without clearing the slots, as a process crash would (tests).</summary>
    internal void Abandon()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _quiet = true;
            _disposed = true;
            _view?.Flush();
            _view?.Dispose();
            _map?.Dispose();
            _stream?.Dispose();
        }
    }

    /// <summary>Clears this session's work and ignores later Enter writes, keeping the folder held.</summary>
    public void Quiet()
    {
        lock (_gate)
        {
            if (_quiet) return;
            _quiet = true;
            // An unrecorded leftover has no epoch in this session and still belongs to the earlier crash.
            for (int i = 0; i < Slots; i++)
                if (_epochs[i] != 0) ClearSlot(i);
        }
    }

    /// <summary>Clears this session's work and releases the folder.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Quiet();
            _disposed = true;
            _view?.Dispose();
            _map?.Dispose();
            _stream?.Dispose();
        }
    }
}

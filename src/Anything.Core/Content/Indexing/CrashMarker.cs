using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Anything.Core.Content.Indexing;

/// <summary>Which reader a marker slot belongs to.</summary>
internal enum MarkerLane : byte { None, Local, Network, OnDemand }

/// <summary>A file named by a marker slot: the one a reader was in when the app last stopped unexpectedly.</summary>
/// <param name="Key">The document key (or, for calibration, the key of "path:" + the full path).</param>
internal readonly record struct MarkerEntry(ulong Key, ushort Volume, MarkerLane Lane, long Size, long Modified);

/// <summary>
/// Content\reading: fixed 32-byte slots naming the file each reader thread is in, written in place before each file and
/// zeroed after it, so a crash (which .NET can't catch for a stack overflow) marks that file Crashed at the next start.
/// The file stays open all session (FileShare.Read|Delete), so the journal sees one change at open and one at close; writes
/// go to the OS cache, which survives a process crash. The header holds the boot id and boot time: slots left by an earlier
/// boot (power cut, blue screen) blame nothing. A slot belongs to one reader-thread instance; a thread zeroes it only while
/// the slot still carries its own epoch, so an abandoned thread that wakes up late never clears its successor's slot.
/// Culprits found at open are carried in a second region until they are recorded, so a crash before that loses nothing.
/// </summary>
internal sealed class CrashMarker : IDisposable
{
    /// <summary>Live slots (reader-thread instances, on-demand readers included).</summary>
    public const int LiveSlots = 16;
    /// <summary>Carried culprits, oldest dropped first.</summary>
    public const int CarriedSlots = 16;
    const int HeaderBytes = 64, SlotBytes = 32, FileBytes = 4096;
    const int CarriedOffset = HeaderBytes + LiveSlots * SlotBytes;
    static ReadOnlySpan<byte> Magic => "RPCMRK01"u8;
    const ushort Version = 1;

    readonly SafeFileHandle? _file;
    readonly object _gate = new();
    readonly bool[] _taken = new bool[LiveSlots];
    readonly uint[] _epochs = new uint[LiveSlots];
    readonly List<MarkerEntry> _carried = [];
    uint _epoch;
    bool _disposed;

    CrashMarker(SafeFileHandle? file) => _file = file;

    /// <summary>A marker with no file (in-memory indexes, tests): slots work, nothing survives.</summary>
    public static CrashMarker InMemory() => new(null);

    /// <summary>Whether slots are written to a file.</summary>
    public bool HasFile => _file != null;

    /// <summary>Closed (its owner stopped): slots taken now protect nothing.</summary>
    public bool IsDisposed { get { lock (_gate) return _disposed; } }

    /// <summary>Files named by live slots at open: the last session of this boot stopped while reading them.</summary>
    public int FreshCulprits { get; private set; }

    /// <summary>
    /// Opens or creates the marker at <paramref name="path"/> and returns what the last session left: slots of a session of
    /// this boot that ended while reading (its culprits) and culprits carried from before. Null when the file can't be
    /// opened (another writer holds it, or no folder): then reading must go on without one, or not at all.
    /// </summary>
    public static CrashMarker? Open(string path, out IReadOnlyList<MarkerEntry> leftovers)
    {
        leftovers = [];
        SafeFileHandle file;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            file = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        var marker = new CrashMarker(file);
        try
        {
            var old = new byte[FileBytes];
            int n = RandomAccess.Read(file, old, 0);
            var found = new List<MarkerEntry>();
            int fresh = 0;
            if (n >= CarriedOffset + CarriedSlots * SlotBytes && old.AsSpan(0, 8).SequenceEqual(Magic)
                && BinaryPrimitives.ReadUInt16LittleEndian(old.AsSpan(8)) == Version)
            {
                uint bootId = BinaryPrimitives.ReadUInt32LittleEndian(old.AsSpan(12));
                long bootTime = BinaryPrimitives.ReadInt64LittleEndian(old.AsSpan(16));
                if (SameBoot(bootId, bootTime))
                    for (int i = 0; i < LiveSlots; i++)
                        if (ReadSlot(old.AsSpan(HeaderBytes + i * SlotBytes)) is { } e) { found.Add(e); fresh++; }
                for (int i = 0; i < CarriedSlots; i++)
                    if (ReadSlot(old.AsSpan(CarriedOffset + i * SlotBytes)) is { } e && !found.Exists(f => f.Key == e.Key)) found.Add(e);
            }
            foreach (var e in found)
                if (marker._carried.Count < CarriedSlots) marker._carried.Add(e);
            marker.FreshCulprits = fresh;
            marker.WriteAll();
            leftovers = found;
            return marker;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            marker.Dispose();
            return null;
        }
    }

    /// <summary>Whether a header's boot id and boot time are this boot's (the tick-count boot time drifts with clock changes).</summary>
    internal static bool SameBoot(uint bootId, long bootTimeUtc)
    {
        uint now = ContentNative.BootId();
        if (now != 0 && bootId != 0) return now == bootId;
        return Math.Abs(ContentNative.BootTimeUtc() - bootTimeUtc) < TimeSpan.FromMinutes(2).Ticks;
    }

    /// <summary>The culprits carried until they are recorded.</summary>
    public IReadOnlyList<MarkerEntry> Carried { get { lock (_gate) return [.. _carried]; } }

    /// <summary>A slot for one reader-thread instance; -1 when all are taken (then that reader must not start a file).</summary>
    public int Acquire()
    {
        lock (_gate)
        {
            for (int i = 0; i < LiveSlots; i++)
                if (!_taken[i]) { _taken[i] = true; return i; }
            return -1;
        }
    }

    /// <summary>Gives a slot back (its thread ended); clears it.</summary>
    public void Release(int slot)
    {
        if ((uint)slot >= LiveSlots) return;
        lock (_gate)
        {
            _taken[slot] = false;
            _epochs[slot] = 0;
            WriteSlot(HeaderBytes + slot * SlotBytes, null);
        }
    }

    /// <summary>Names the file a slot's thread starts reading; returns the epoch to leave with.</summary>
    public uint Enter(int slot, in MarkerEntry entry)
    {
        if ((uint)slot >= LiveSlots) return 0;
        lock (_gate)
        {
            uint epoch = ++_epoch == 0 ? ++_epoch : _epoch;
            _epochs[slot] = epoch;
            WriteSlot(HeaderBytes + slot * SlotBytes, entry, epoch);
            return epoch;
        }
    }

    /// <summary>The file is done: zeroes the slot, unless it now carries another epoch (a later file's).</summary>
    public void Leave(int slot, uint epoch)
    {
        if ((uint)slot >= LiveSlots) return;
        lock (_gate)
        {
            if (_epochs[slot] != epoch) return;
            _epochs[slot] = 0;
            WriteSlot(HeaderBytes + slot * SlotBytes, null);
        }
    }

    /// <summary>Keeps a culprit until it is recorded (oldest dropped when full).</summary>
    public void Carry(in MarkerEntry entry)
    {
        lock (_gate)
        {
            var key = entry.Key;
            _carried.RemoveAll(e => e.Key == key);
            if (_carried.Count >= CarriedSlots) _carried.RemoveAt(0);
            _carried.Add(entry);
            WriteCarried();
        }
    }

    /// <summary>A culprit was recorded (or its file changed): forgets it.</summary>
    public void Uncarry(ulong key)
    {
        lock (_gate)
        {
            if (_carried.RemoveAll(e => e.Key == key) > 0) WriteCarried();
        }
    }

    /// <summary>Forgets every carried culprit ("Read problem files again").</summary>
    public void ClearCarried()
    {
        lock (_gate)
        {
            if (_carried.Count == 0) return;
            _carried.Clear();
            WriteCarried();
        }
    }

    /// <summary>Zeroes every live slot (a checkpoint before the session may end, or a clean stop); carried culprits stay.</summary>
    public void ClearLive()
    {
        lock (_gate)
        {
            Array.Clear(_epochs);
            if (_file == null || _disposed) return;
            try { RandomAccess.Write(_file, new byte[LiveSlots * SlotBytes], HeaderBytes); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
        }
    }

    void WriteAll()
    {
        if (_file == null) return;
        var buffer = new byte[FileBytes];
        Magic.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), LiveSlots);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), ContentNative.BootId());
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(16), ContentNative.BootTimeUtc());
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24), ContentNative.ProcessStartUtc());
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(32), Environment.ProcessId);
        for (int i = 0; i < _carried.Count; i++) Encode(buffer.AsSpan(CarriedOffset + i * SlotBytes), _carried[i], 0);
        RandomAccess.Write(_file, buffer, 0);
    }

    void WriteCarried()
    {
        if (_file == null || _disposed) return;
        var buffer = new byte[CarriedSlots * SlotBytes];
        for (int i = 0; i < _carried.Count; i++) Encode(buffer.AsSpan(i * SlotBytes), _carried[i], 0);
        try { RandomAccess.Write(_file, buffer, CarriedOffset); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    void WriteSlot(int offset, MarkerEntry? entry, uint epoch = 0)
    {
        if (_file == null || _disposed) return;
        Span<byte> slot = stackalloc byte[SlotBytes];
        slot.Clear();
        if (entry is { } e) Encode(slot, e, epoch);
        try { RandomAccess.Write(_file, slot, offset); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    static void Encode(Span<byte> slot, in MarkerEntry e, uint epoch)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(slot, e.Key);
        BinaryPrimitives.WriteUInt16LittleEndian(slot[8..], e.Volume);
        slot[10] = (byte)(e.Lane == MarkerLane.None ? MarkerLane.Local : e.Lane);
        slot[11] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(slot[12..], epoch);
        BinaryPrimitives.WriteInt64LittleEndian(slot[16..], e.Size);
        BinaryPrimitives.WriteInt64LittleEndian(slot[24..], e.Modified);
    }

    static MarkerEntry? ReadSlot(ReadOnlySpan<byte> slot)
    {
        var lane = (MarkerLane)slot[10];
        if (lane is MarkerLane.None or > MarkerLane.OnDemand) return null;
        ulong key = BinaryPrimitives.ReadUInt64LittleEndian(slot);
        if (key == 0) return null;
        return new MarkerEntry(key, BinaryPrimitives.ReadUInt16LittleEndian(slot[8..]), lane,
            BinaryPrimitives.ReadInt64LittleEndian(slot[16..]), BinaryPrimitives.ReadInt64LittleEndian(slot[24..]));
    }

    /// <summary>Zeroes the live slots and closes the file (carried culprits stay for the next session).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_file != null)
            {
                try { RandomAccess.Write(_file, new byte[LiveSlots * SlotBytes], HeaderBytes); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
                _file.Dispose();
            }
            _disposed = true;
        }
    }
}

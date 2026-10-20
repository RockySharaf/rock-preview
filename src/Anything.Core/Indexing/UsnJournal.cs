using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Anything.Core.Indexing;

[Flags]
public enum UsnReason : uint
{
    DataOverwrite = 0x00000001,
    DataExtend = 0x00000002,
    DataTruncation = 0x00000004,
    NamedDataOverwrite = 0x00000010,
    NamedDataExtend = 0x00000020,
    NamedDataTruncation = 0x00000040,
    FileCreate = 0x00000100,
    FileDelete = 0x00000200,
    EaChange = 0x00000400,
    SecurityChange = 0x00000800,
    RenameOldName = 0x00001000,
    RenameNewName = 0x00002000,
    IndexableChange = 0x00004000,
    BasicInfoChange = 0x00008000,
    HardLinkChange = 0x00010000,
    CompressionChange = 0x00020000,
    EncryptionChange = 0x00040000,
    ObjectIdChange = 0x00080000,
    ReparsePointChange = 0x00100000,
    StreamChange = 0x00200000,
    Close = 0x80000000,

    /// <summary>Reasons after which size, dates or attributes should be re-read.</summary>
    MetadataChanged = DataOverwrite | DataExtend | DataTruncation | BasicInfoChange | CompressionChange | EncryptionChange
        | ReparsePointChange | StreamChange | Close,
}

/// <summary>One USN_RECORD_V2/V3 (file references reduced to the 48-bit NTFS record number).</summary>
public readonly record struct UsnRecord(long Record, long ParentRecord, long Usn, long TimeStamp, UsnReason Reason, FileAttributes Attributes, string Name);

public readonly record struct UsnJournalInfo(ulong JournalId, long FirstUsn, long NextUsn, long LowestValidUsn, long MaxUsn, ulong MaximumSize, ulong AllocationDelta);

public static class UsnRecordParser
{
    const long RefMask = 0x0000FFFFFFFFFFFF;

    /// <summary>Parses one record at the start of <paramref name="buffer"/>. Returns false on malformed data; <paramref name="length"/> is the record length.</summary>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out UsnRecord record, out int length)
    {
        record = default;
        length = 0;
        if (buffer.Length < 8) return false;
        length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (length < 0x3C || length > buffer.Length) return false;
        int major = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..]);
        var r = buffer[..length];
        long frn, parent, usn, ts; uint reason, attrs; int nameLen, nameOfs;
        if (major == 2)
        {
            frn = (long)(BinaryPrimitives.ReadUInt64LittleEndian(r[8..]) & RefMask);
            parent = (long)(BinaryPrimitives.ReadUInt64LittleEndian(r[16..]) & RefMask);
            usn = BinaryPrimitives.ReadInt64LittleEndian(r[24..]);
            ts = BinaryPrimitives.ReadInt64LittleEndian(r[32..]);
            reason = BinaryPrimitives.ReadUInt32LittleEndian(r[40..]);
            attrs = BinaryPrimitives.ReadUInt32LittleEndian(r[52..]);
            nameLen = BinaryPrimitives.ReadUInt16LittleEndian(r[56..]);
            nameOfs = BinaryPrimitives.ReadUInt16LittleEndian(r[58..]);
        }
        else if (major == 3)
        {
            if (length < 0x4C) return false;
            frn = (long)(BinaryPrimitives.ReadUInt64LittleEndian(r[8..]) & RefMask);
            parent = (long)(BinaryPrimitives.ReadUInt64LittleEndian(r[24..]) & RefMask);
            usn = BinaryPrimitives.ReadInt64LittleEndian(r[40..]);
            ts = BinaryPrimitives.ReadInt64LittleEndian(r[48..]);
            reason = BinaryPrimitives.ReadUInt32LittleEndian(r[56..]);
            attrs = BinaryPrimitives.ReadUInt32LittleEndian(r[68..]);
            nameLen = BinaryPrimitives.ReadUInt16LittleEndian(r[72..]);
            nameOfs = BinaryPrimitives.ReadUInt16LittleEndian(r[74..]);
        }
        else return true; // unknown version (e.g. V4 range records): skip but keep walking

        if (nameOfs + nameLen > length || (nameLen & 1) != 0) return false;
        string name = new(MemoryMarshal.Cast<byte, char>(r.Slice(nameOfs, nameLen)));
        record = new UsnRecord(frn, parent, usn, ts, (UsnReason)reason, (FileAttributes)attrs, name);
        return true;
    }
}

/// <summary>FSCTL wrappers for the NTFS change journal.</summary>
internal static unsafe class UsnJournal
{
    [StructLayout(LayoutKind.Sequential)]
    struct ReadUsnJournalDataV0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MftEnumDataV0
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CreateUsnJournalData
    {
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    public static bool TryQuery(SafeFileHandle volume, out UsnJournalInfo info, out int error)
    {
        byte* buf = stackalloc byte[128];
        if (!Native.DeviceIoControl(volume, Native.FSCTL_QUERY_USN_JOURNAL, null, 0, buf, 128, out int ret, 0) || ret < 56)
        {
            error = Marshal.GetLastPInvokeError();
            info = default;
            return false;
        }
        error = 0;
        info = new UsnJournalInfo(*(ulong*)buf, *(long*)(buf + 8), *(long*)(buf + 16), *(long*)(buf + 24), *(long*)(buf + 32), *(ulong*)(buf + 40), *(ulong*)(buf + 48));
        return true;
    }

    public static bool TryCreate(SafeFileHandle volume, ulong maximumSize = 32UL << 20, ulong allocationDelta = 8UL << 20)
    {
        var data = new CreateUsnJournalData { MaximumSize = maximumSize, AllocationDelta = allocationDelta };
        return Native.DeviceIoControl(volume, Native.FSCTL_CREATE_USN_JOURNAL, &data, sizeof(CreateUsnJournalData), null, 0, out _, 0);
    }

    /// <summary>Queries the journal, creating it when it does not exist yet.</summary>
    public static bool TryQueryOrCreate(SafeFileHandle volume, out UsnJournalInfo info)
    {
        if (TryQuery(volume, out info, out int err)) return true;
        if (err is Native.ERROR_JOURNAL_NOT_ACTIVE or Native.ERROR_JOURNAL_DELETE_IN_PROGRESS)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (TryCreate(volume) && TryQuery(volume, out info, out _)) return true;
                Thread.Sleep(250);
            }
        }
        return false;
    }

    /// <summary>
    /// Reads journal records from <paramref name="startUsn"/> without waiting: when there is nothing new it returns
    /// just the next USN (8 bytes, equal to <paramref name="startUsn"/>), and the caller polls again a little later.
    /// Returns bytes in <paramref name="buffer"/> (first 8 bytes = next USN), or -Win32Error on failure.
    /// </summary>
    public static int Read(SafeFileHandle volume, ulong journalId, long startUsn, byte* buffer, int size)
    {
        // BytesToWaitFor = 0 returns at once. Any wait (even with a timeout) keeps the call outstanding until a record
        // arrives, which on a quiet drive can be forever: the monitor could then not stop, and through the service the
        // volume handle could not be closed for an eject.
        var rd = new ReadUsnJournalDataV0
        {
            StartUsn = startUsn,
            ReasonMask = 0xFFFFFFFF,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalId = journalId,
        };
        if (!Native.DeviceIoControl(volume, Native.FSCTL_READ_USN_JOURNAL, &rd, sizeof(ReadUsnJournalDataV0), buffer, size, out int ret, 0))
            return -Math.Max(1, Marshal.GetLastPInvokeError());
        return ret;
    }

    /// <summary>
    /// Fallback scan: enumerates every MFT record through FSCTL_ENUM_USN_DATA (names, parents and attributes only).
    /// </summary>
    public static VolumeRecords Enumerate(SafeFileHandle volume, long highUsn, CancellationToken ct, Action<int>? progress)
    {
        const int Size = 1 << 20;
        var records = new VolumeRecords(1 << 16);
        byte* buf = (byte*)NativeMemory.Alloc(Size);
        try
        {
            var med = new MftEnumDataV0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = highUsn <= 0 ? long.MaxValue : highUsn };
            int total = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!Native.DeviceIoControl(volume, Native.FSCTL_ENUM_USN_DATA, &med, sizeof(MftEnumDataV0), buf, Size, out int ret, 0))
                {
                    int err = Marshal.GetLastPInvokeError();
                    if (err == Native.ERROR_HANDLE_EOF) break;
                    throw new Win32Exception(err, "FSCTL_ENUM_USN_DATA failed");
                }
                if (ret <= 8) break;
                med.StartFileReferenceNumber = *(ulong*)buf;
                var span = new ReadOnlySpan<byte>(buf + 8, ret - 8);
                while (span.Length > 0)
                {
                    if (!UsnRecordParser.TryParse(span, out var r, out int len) || len <= 0) break;
                    if (r.Name != null) records.SetFromUsn(r.Record, r.ParentRecord, r.Name, r.Attributes);
                    span = span[len..];
                    total++;
                }
                progress?.Invoke(total);
            }
            records.EnsureRoot();
            return records;
        }
        finally { NativeMemory.Free(buf); }
    }
}

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Anything.Core.Indexing;

/// <summary>Geometry read from the NTFS boot sector (sector 0 of the volume).</summary>
public readonly record struct NtfsBootSector(
    int BytesPerSector, int SectorsPerCluster, long TotalSectors, long MftLcn, long MftMirrorLcn, int BytesPerFileRecord)
{
    const long MaxClusterSize = 2 << 20;

    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;

    public static NtfsBootSector Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512) throw new InvalidDataException("boot sector too short");
        if (!sector.Slice(3, 8).SequenceEqual("NTFS    "u8)) throw new InvalidDataException("bad NTFS signature");
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(sector[0x0B..]);
        if (bps < 256 || bps > 4096 || (bps & (bps - 1)) != 0) throw new InvalidDataException("invalid bytes_per_sector");
        byte rawSpc = sector[0x0D];
        // Values above 0x80 encode 2^(256-x) (clusters larger than 64 KB). NTFS clusters are at most 2 MB.
        long spc = rawSpc <= 0x80 ? rawSpc : 256 - rawSpc < 32 ? 1L << (256 - rawSpc) : 0;
        if (spc == 0 || (spc & (spc - 1)) != 0 || bps * spc > MaxClusterSize) throw new InvalidDataException("invalid sectors_per_cluster");
        long cluster = bps * spc;
        long total = BinaryPrimitives.ReadInt64LittleEndian(sector[0x28..]);
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(sector[0x30..]);
        long mirr = BinaryPrimitives.ReadInt64LittleEndian(sector[0x38..]);
        sbyte cpfr = (sbyte)sector[0x40];
        long recordSize = cpfr > 0 ? cpfr * cluster : cpfr > -32 ? 1L << -cpfr : 0;
        // Windows formats 1 KB records, 4 KB on 4K-sector disks. Bigger ones would let a crafted disk pack thousands of
        // attribute-list entries and tens of thousands of $MFT runs into every record.
        if (recordSize is not (1024 or 4096)) throw new InvalidDataException("invalid bytes_per_file_record");
        if (total <= 0) throw new InvalidDataException("invalid total_sectors");
        if (mftLcn <= 0 || mftLcn > total / spc) throw new InvalidDataException("invalid mft_lcn");
        return new NtfsBootSector(bps, (int)spc, total, mftLcn, mirr, (int)recordSize);
    }
}

/// <summary>One extent of a non-resident attribute. <see cref="Lcn"/> is -1 for sparse runs.</summary>
public readonly record struct DataRun(long Vcn, long Lcn, long Length)
{
    public bool IsSparse => Lcn < 0;
}

/// <summary>Entry of an $ATTRIBUTE_LIST: which record holds (part of) an attribute.</summary>
public readonly record struct AttributeListEntry(uint Type, long StartingVcn, long RecordNumber, bool Named);

public enum FileNameNamespace : byte { Posix = 0, Win32 = 1, Dos = 2, Win32AndDos = 3 }

public readonly record struct MftFileName(long ParentRecord, ushort ParentSequence, string Name, FileNameNamespace Namespace);

/// <summary>Parsed view of one FILE record. Reused across records to avoid per-record allocations.</summary>
public sealed class MftRecord
{
    public long RecordNumber;
    /// <summary>Base record number for extension records; 0 for base records.</summary>
    public long BaseRecord;
    public ushort SequenceNumber;
    public ushort Flags;
    public bool InUse => (Flags & 0x01) != 0;
    public bool IsDirectory => (Flags & 0x02) != 0;

    public bool HasStandardInformation;
    public long CreationTime;
    public long ModificationTime;
    public long AccessTime;
    public FileAttributes Attributes;

    /// <summary>True when the unnamed $DATA attribute (its first extent) was seen.</summary>
    public bool HasData;
    public bool DataNonResident;
    public long DataSize;
    public long DataAllocatedSize;
    public long DataInitializedSize;

    public bool HasAttributeList;
    public readonly List<MftFileName> FileNames = new(2);
    /// <summary>Only filled when parsing with captureDataRuns (used for $MFT itself).</summary>
    public readonly List<DataRun> DataRuns = [];
    public readonly List<AttributeListEntry> AttributeList = [];

    public void Reset()
    {
        RecordNumber = 0; BaseRecord = 0; SequenceNumber = 0; Flags = 0;
        HasStandardInformation = false; CreationTime = 0; ModificationTime = 0; AccessTime = 0; Attributes = 0;
        HasData = false; DataNonResident = false; DataSize = 0; DataAllocatedSize = 0; DataInitializedSize = 0;
        HasAttributeList = false;
        FileNames.Clear(); DataRuns.Clear(); AttributeList.Clear();
    }
}

/// <summary>Parses raw NTFS FILE records (works on byte spans so it is testable without a volume).</summary>
public static class MftRecordParser
{
    public const uint FileSignature = 0x454C4946; // "FILE"
    public const uint AttrStandardInformation = 0x10;
    public const uint AttrAttributeList = 0x20;
    public const uint AttrFileName = 0x30;
    public const uint AttrData = 0x80;
    public const uint AttrEnd = 0xFFFFFFFF;
    const int SequenceStride = 512;
    const long RefMask = 0x0000FFFFFFFFFFFF;

    public static bool HasFileSignature(ReadOnlySpan<byte> record)
        => record.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(record) == FileSignature;

    /// <summary>
    /// Applies the update sequence array: verifies the last two bytes of every 512-byte stride carry the
    /// update sequence number and restores the original bytes. Returns false for torn/corrupt records.
    /// </summary>
    public static bool ApplyFixups(Span<byte> record)
    {
        if (record.Length < 0x30) return false;
        int usaOfs = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount == 0) return true;
        if (usaOfs + usaCount * 2 > record.Length || (usaCount - 1) * SequenceStride > record.Length) return false;
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOfs..]);
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * SequenceStride - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[pos..]) != usn) return false;
            record[pos] = record[usaOfs + 2 * i];
            record[pos + 1] = record[usaOfs + 2 * i + 1];
        }
        return true;
    }

    /// <summary>
    /// Parses a FILE record whose fixups were already applied. DOS-only (8.3) names are skipped unless the record
    /// has no other name. Returns false if the record is not a valid FILE record.
    /// </summary>
    public static bool Parse(ReadOnlySpan<byte> rec, long recordNumber, MftRecord result, bool captureDataRuns = false)
    {
        result.Reset();
        result.RecordNumber = recordNumber;
        if (rec.Length < 0x30 || BinaryPrimitives.ReadUInt32LittleEndian(rec) != FileSignature) return false;
        result.SequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x10..]);
        int firstAttr = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x14..]);
        result.Flags = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x16..]);
        int bytesInUse = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(rec[0x18..]), (uint)rec.Length);
        result.BaseRecord = (long)(BinaryPrimitives.ReadUInt64LittleEndian(rec[0x20..]) & RefMask);

        int dosValueOffset = -1;
        int off = firstAttr;
        while (off + 16 <= bytesInUse)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(rec[off..]);
            if (type == AttrEnd) break;
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[(off + 4)..]);
            if (len < 16 || off + len > bytesInUse) break; // corrupt attribute chain
            bool nonResident = rec[off + 8] != 0;
            int nameLength = rec[off + 9];
            var attr = rec.Slice(off, len);

            switch (type)
            {
                case AttrStandardInformation when !nonResident:
                    if (TryResidentValue(attr, out var si) && si.Length >= 0x24)
                    {
                        result.HasStandardInformation = true;
                        result.CreationTime = BinaryPrimitives.ReadInt64LittleEndian(si);
                        result.ModificationTime = BinaryPrimitives.ReadInt64LittleEndian(si[0x08..]);
                        result.AccessTime = BinaryPrimitives.ReadInt64LittleEndian(si[0x18..]);
                        result.Attributes = (FileAttributes)(BinaryPrimitives.ReadUInt32LittleEndian(si[0x20..]) & 0x0007FFFF);
                    }
                    break;

                case AttrAttributeList:
                    result.HasAttributeList = true;
                    if (captureDataRuns && !nonResident && TryResidentValue(attr, out var al)) ParseAttributeList(al, result.AttributeList);
                    break;

                case AttrFileName when !nonResident:
                    if (TryResidentValue(attr, out var fn) && fn.Length >= 0x42)
                    {
                        int chars = fn[0x40];
                        var ns = (FileNameNamespace)fn[0x41];
                        if (0x42 + chars * 2 > fn.Length || chars == 0) break;
                        if (ns == FileNameNamespace.Dos)
                        {
                            if (dosValueOffset < 0) dosValueOffset = off + BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..]);
                            break;
                        }
                        result.FileNames.Add(ReadFileName(fn, chars, ns));
                    }
                    break;

                case AttrData when nameLength == 0:
                    if (!nonResident)
                    {
                        result.HasData = true;
                        result.DataSize = BinaryPrimitives.ReadUInt32LittleEndian(attr[0x10..]);
                        result.DataAllocatedSize = result.DataInitializedSize = result.DataSize;
                    }
                    else if (len >= 0x40)
                    {
                        long lowestVcn = BinaryPrimitives.ReadInt64LittleEndian(attr[0x10..]);
                        if (lowestVcn == 0)
                        {
                            result.HasData = true;
                            result.DataNonResident = true;
                            result.DataAllocatedSize = BinaryPrimitives.ReadInt64LittleEndian(attr[0x28..]);
                            result.DataSize = BinaryPrimitives.ReadInt64LittleEndian(attr[0x30..]);
                            result.DataInitializedSize = BinaryPrimitives.ReadInt64LittleEndian(attr[0x38..]);
                        }
                        if (captureDataRuns)
                        {
                            int mp = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x20..]);
                            if (mp < len) result.DataRuns.AddRange(DecodeDataRuns(attr[mp..], lowestVcn));
                        }
                    }
                    break;
            }
            off += len;
        }

        if (result.FileNames.Count == 0 && dosValueOffset >= 0)
        {
            var fn = rec[dosValueOffset..];
            result.FileNames.Add(ReadFileName(fn, fn[0x40], FileNameNamespace.Dos));
        }
        return true;
    }

    static MftFileName ReadFileName(ReadOnlySpan<byte> fn, int chars, FileNameNamespace ns)
    {
        ulong parentRef = BinaryPrimitives.ReadUInt64LittleEndian(fn);
        string name = new(MemoryMarshal.Cast<byte, char>(fn.Slice(0x42, chars * 2)));
        return new MftFileName((long)(parentRef & RefMask), (ushort)(parentRef >> 48), name, ns);
    }

    static bool TryResidentValue(ReadOnlySpan<byte> attr, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (attr.Length < 0x18) return false;
        int vlen = (int)BinaryPrimitives.ReadUInt32LittleEndian(attr[0x10..]);
        int vofs = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..]);
        if (vofs + (long)vlen > attr.Length) return false;
        value = attr.Slice(vofs, vlen);
        return true;
    }

    static void ParseAttributeList(ReadOnlySpan<byte> list, List<AttributeListEntry> into)
    {
        int p = 0;
        while (p + 0x1A <= list.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(list[p..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(list[(p + 4)..]);
            if (len < 0x1A || p + len > list.Length) break;
            int nameLen = list[p + 6];
            long vcn = BinaryPrimitives.ReadInt64LittleEndian(list[(p + 8)..]);
            long rec = (long)(BinaryPrimitives.ReadUInt64LittleEndian(list[(p + 0x10)..]) & RefMask);
            into.Add(new AttributeListEntry(type, vcn, rec, nameLen != 0));
            p += len;
        }
    }

    /// <summary>Decodes an NTFS mapping-pairs array into runs (absolute LCNs; sparse runs get Lcn = -1).</summary>
    public static List<DataRun> DecodeDataRuns(ReadOnlySpan<byte> mapping, long startVcn = 0)
    {
        var runs = new List<DataRun>();
        long vcn = startVcn, lcn = 0;
        int p = 0;
        while (p < mapping.Length)
        {
            byte header = mapping[p++];
            if (header == 0) break;
            int lenSize = header & 0x0F, ofsSize = header >> 4;
            if (lenSize == 0 || lenSize > 8 || ofsSize > 8 || p + lenSize + ofsSize > mapping.Length)
                throw new InvalidDataException("bad mapping pairs");
            long length = 0;
            for (int i = 0; i < lenSize; i++) length |= (long)mapping[p + i] << (8 * i);
            p += lenSize;
            if (length <= 0) throw new InvalidDataException("bad run length");
            if (ofsSize == 0)
            {
                runs.Add(new DataRun(vcn, -1, length));
            }
            else
            {
                long delta = 0;
                for (int i = 0; i < ofsSize; i++) delta |= (long)mapping[p + i] << (8 * i);
                if ((mapping[p + ofsSize - 1] & 0x80) != 0 && ofsSize < 8) delta |= -1L << (8 * ofsSize); // sign extend
                p += ofsSize;
                lcn += delta;
                runs.Add(new DataRun(vcn, lcn, length));
            }
            vcn += length;
        }
        return runs;
    }
}

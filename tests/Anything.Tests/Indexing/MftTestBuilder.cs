using System.Buffers.Binary;
using System.Text;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

/// <summary>Builds synthetic NTFS FILE records and attributes for parser tests.</summary>
internal static class MftTestBuilder
{
    public const int UsaOffset = 0x30;

    static int Align8(int v) => (v + 7) & ~7;

    public static byte[] Record(int size, ushort flags, long baseRef, params byte[][] attributes)
    {
        var b = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, MftRecordParser.FileSignature);
        int usaCount = size / 512 + 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), UsaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)usaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x10), 1); // sequence
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x12), 1); // link count
        int first = Align8(UsaOffset + usaCount * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x14), (ushort)first);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x16), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x1C), (uint)size);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x20), (ulong)baseRef | (baseRef != 0 ? 1UL << 48 : 0));
        int off = first;
        foreach (var a in attributes)
        {
            a.CopyTo(b, off);
            off += a.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off), MftRecordParser.AttrEnd);
        off += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x18), (uint)off);
        return b;
    }

    /// <summary>Simulates the on-disk form: moves the last two bytes of every 512-byte stride into the USA.</summary>
    public static void Protect(byte[] b, ushort usn = 0x4242)
    {
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(UsaOffset), usn);
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * 512 - 2;
            b[UsaOffset + 2 * i] = b[pos];
            b[UsaOffset + 2 * i + 1] = b[pos + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(pos), usn);
        }
    }

    public static byte[] Resident(uint type, byte[] value, string? name = null)
    {
        int nameLen = name?.Length ?? 0;
        int valueOfs = Align8(0x18 + nameLen * 2);
        int len = Align8(valueOfs + value.Length);
        var a = new byte[len];
        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)len);
        a[8] = 0;
        a[9] = (byte)nameLen;
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(0x10), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x14), (ushort)valueOfs);
        if (name != null) Encoding.Unicode.GetBytes(name).CopyTo(a, 0x18);
        value.CopyTo(a, valueOfs);
        return a;
    }

    public static byte[] NonResident(uint type, long lowestVcn, long highestVcn, long allocated, long size, long initialized, byte[] runs, string? name = null)
    {
        int nameLen = name?.Length ?? 0;
        int nameOfs = 0x40;
        int runsOfs = Align8(nameOfs + nameLen * 2);
        int len = Align8(runsOfs + runs.Length + 1);
        var a = new byte[len];
        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)len);
        a[8] = 1;
        a[9] = (byte)nameLen;
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), (ushort)nameOfs);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x10), lowestVcn);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x18), highestVcn);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x20), (ushort)runsOfs);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x28), allocated);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x30), size);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x38), initialized);
        if (name != null) Encoding.Unicode.GetBytes(name).CopyTo(a, nameOfs);
        runs.CopyTo(a, runsOfs);
        return a;
    }

    public static byte[] StandardInformation(long created, long modified, FileAttributes attributes)
    {
        var v = new byte[0x48];
        BinaryPrimitives.WriteInt64LittleEndian(v, created);
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(8), modified);
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0x10), modified);
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(0x18), modified);
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(0x20), (uint)attributes);
        return Resident(MftRecordParser.AttrStandardInformation, v);
    }

    public static byte[] FileName(long parentRecord, string name, FileNameNamespace ns, ushort parentSeq = 1)
    {
        var v = new byte[0x42 + name.Length * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(v, (ulong)parentRecord | ((ulong)parentSeq << 48));
        v[0x40] = (byte)name.Length;
        v[0x41] = (byte)ns;
        Encoding.Unicode.GetBytes(name).CopyTo(v, 0x42);
        return Resident(MftRecordParser.AttrFileName, v);
    }

    public static byte[] ResidentData(int length) => Resident(MftRecordParser.AttrData, new byte[length]);
}

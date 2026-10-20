using System.Buffers.Binary;
using Anything.Core;
using Anything.Core.Indexing;
using Microsoft.Win32.SafeHandles;
using static Anything.Tests.Indexing.MftTestBuilder;

namespace Anything.Tests.Indexing;

/// <summary>
/// The $MFT's own data runs, as a crafted USB stick could set them for the LocalSystem service: they must map the MFT
/// exactly once (no gaps, overlaps, repeats, or more than its allocated size), while the layouts real volumes have
/// still read: a fragmented $MFT with extents in an extension record, one grown after format, 4K sectors, 64 KB
/// clusters, and positions past 4 GB.
/// </summary>
public sealed class MftRunValidationTests : IDisposable
{
    const ushort InUse = 1, Dir = 2;
    const int ExtensionRecord = 15;
    readonly string _image = Path.Combine(Path.GetTempPath(), $"anything-ntfs-runs-{Guid.NewGuid():N}.img");

    public void Dispose()
    {
        try { File.Delete(_image); } catch { }
    }

    /// <summary>Volume geometry, and where the $MFT really lies: (LCN, clusters) extents in VCN order.</summary>
    sealed record Geometry(int SectorSize, int ClusterSize, int RecordSize, long Clusters, (long Lcn, long Count)[] Extents)
    {
        public long MftRecords => Extents.Sum(e => e.Count) * ClusterSize / RecordSize;

        public long Offset(long rec)
        {
            long vcn = rec * RecordSize / ClusterSize, within = rec * RecordSize % ClusterSize;
            foreach (var (lcn, count) in Extents)
            {
                if (vcn < count) return (lcn + vcn) * ClusterSize + within;
                vcn -= count;
            }
            throw new ArgumentOutOfRangeException(nameof(rec));
        }
    }

    /// <summary>Two extents with a cluster between them; records 0-15 (record 0 and the extension record) in the first.</summary>
    static Geometry TwoExtents(int sectorSize = 512, int clusterSize = 4096, int recordSize = 1024)
    {
        long count = Math.Max(1, 16L * recordSize / clusterSize);
        long second = 2 + count + 1;
        return new Geometry(sectorSize, clusterSize, recordSize, second + count + 1, [(2, count), (second, count)]);
    }

    /// <summary>
    /// Writes the image: boot sector, record 0 ($MFT, with <paramref name="mftAttributes"/> after its name), the
    /// root, Users, Users\readme.txt, Users\far.txt in the MFT's last record, and <paramref name="records"/>.
    /// </summary>
    void Write(Geometry g, byte[][] mftAttributes, (long Rec, byte[] Bytes)[]? records = null, bool sparse = false)
    {
        using var file = new FileStream(_image, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        if (sparse && !TrySetSparse(file.SafeFileHandle)) throw new SkipException();
        file.SetLength(g.Clusters * g.ClusterSize);

        var boot = new byte[Math.Max(512, g.SectorSize)];
        "NTFS    "u8.CopyTo(boot.AsSpan(3));
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(0x0B), (ushort)g.SectorSize);
        boot[0x0D] = (byte)(g.ClusterSize / g.SectorSize);
        BinaryPrimitives.WriteInt64LittleEndian(boot.AsSpan(0x28), g.Clusters * g.ClusterSize / g.SectorSize);
        BinaryPrimitives.WriteInt64LittleEndian(boot.AsSpan(0x30), g.Extents[0].Lcn);
        BinaryPrimitives.WriteInt64LittleEndian(boot.AsSpan(0x38), 1);
        boot[0x40] = (byte)(sbyte)-int.Log2(g.RecordSize);
        file.Position = 0;
        file.Write(boot);

        void Put(long rec, byte[] bytes)
        {
            Protect(bytes, (ushort)(0x100 + rec));
            file.Position = g.Offset(rec);
            file.Write(bytes);
        }

        int rs = g.RecordSize;
        long far = g.MftRecords - 1;
        Put(0, Record(rs, InUse, 0, [StandardInformation(1, 1, FileAttributes.Hidden | FileAttributes.System),
            FileName(5, "$MFT", FileNameNamespace.Win32AndDos), .. mftAttributes]));
        Put(5, Record(rs, InUse | Dir, 0, StandardInformation(1, 1, FileAttributes.Hidden | FileAttributes.System), FileName(5, ".", FileNameNamespace.Win32AndDos)));
        Put(16, Record(rs, InUse | Dir, 0, StandardInformation(2, 3, 0), FileName(5, "Users", FileNameNamespace.Win32AndDos)));
        Put(17, Record(rs, InUse, 0, StandardInformation(4, 5, FileAttributes.Archive), FileName(16, "readme.txt", FileNameNamespace.Win32AndDos), ResidentData(10)));
        Put(far, Record(rs, InUse, 0, StandardInformation(8, 9, FileAttributes.Archive), FileName(16, "far.txt", FileNameNamespace.Win32AndDos), ResidentData(3)));
        foreach (var (rec, bytes) in records ?? []) Put(rec, bytes);
    }

    VolumeRecords Read()
    {
        using var handle = File.OpenHandle(_image, FileMode.Open, FileAccess.Read);
        return MftReader.Read(handle, CancellationToken.None, null);
    }

    void AssertReadsTree()
    {
        var index = new FileIndex();
        Read().BuildInto(index, "T:", ExclusionFilter.None);
        Assert.Equal([@"T:\", @"T:\Users", @"T:\Users\far.txt", @"T:\Users\readme.txt"], DriveIndexingTestSupport.Paths(index));
    }

    void AssertRejected()
    {
        using var handle = File.OpenHandle(_image, FileMode.Open, FileAccess.Read);
        Assert.Throws<InvalidDataException>(() => MftReader.Read(handle, CancellationToken.None, null));
    }

    /// <summary>Record 0's unnamed $DATA: the first extent, with the sizes (in bytes).</summary>
    static byte[] MftData(long allocated, long size, byte[] runs, long highestVcn = 0) =>
        NonResident(MftRecordParser.AttrData, 0, highestVcn, allocated, size, size, runs);

    /// <summary>An extension record holding a later extent of $MFT's $DATA, from <paramref name="lowestVcn"/>.</summary>
    static byte[] Extension(int recordSize, long lowestVcn, byte[] runs) =>
        Record(recordSize, InUse, 0, NonResident(MftRecordParser.AttrData, lowestVcn, lowestVcn, 0, 0, 0, runs));

    /// <summary>An $ATTRIBUTE_LIST naming where $DATA's extents are: (starting VCN, record).</summary>
    static byte[] AttributeList(params (long StartVcn, long Record)[] entries)
    {
        var v = new byte[entries.Length * 0x20];
        for (int i = 0; i < entries.Length; i++)
        {
            var e = v.AsSpan(i * 0x20);
            BinaryPrimitives.WriteUInt32LittleEndian(e, MftRecordParser.AttrData);
            BinaryPrimitives.WriteUInt16LittleEndian(e[4..], 0x20);
            e[7] = 0x1A; // name offset (no name)
            BinaryPrimitives.WriteInt64LittleEndian(e[8..], entries[i].StartVcn);
            BinaryPrimitives.WriteUInt64LittleEndian(e[0x10..], (ulong)entries[i].Record | 1UL << 48);
        }
        return Resident(MftRecordParser.AttrAttributeList, v);
    }

    /// <summary>Mapping pairs for (LCN, clusters) runs; a run with Lcn -1 is sparse.</summary>
    static byte[] Runs(params (long Lcn, long Count)[] runs)
    {
        var b = new List<byte>();
        long prev = 0;
        foreach (var (lcn, count) in runs)
        {
            byte[] len = LittleEndian(count, signed: false), ofs = lcn < 0 ? [] : LittleEndian(lcn - prev, signed: true);
            b.Add((byte)(ofs.Length << 4 | len.Length));
            b.AddRange(len);
            b.AddRange(ofs);
            if (lcn >= 0) prev = lcn;
        }
        return [.. b];
    }

    static byte[] LittleEndian(long v, bool signed)
    {
        var b = new List<byte>();
        do { b.Add((byte)v); v >>= 8; }
        while (signed ? !(v == 0 && (b[^1] & 0x80) == 0 || v == -1 && (b[^1] & 0x80) != 0) : v != 0);
        return [.. b];
    }

    static long Bytes(Geometry g, long clusters) => clusters * g.ClusterSize;

    [Theory]
    [InlineData(512, 4096, 1024)]
    [InlineData(4096, 4096, 4096)] // 4K-sector disk
    [InlineData(512, 65536, 1024)] // 64 KB clusters
    public void Fragmented_mft_with_its_later_extent_in_an_extension_record_reads(int sectorSize, int clusterSize, int recordSize)
    {
        var g = TwoExtents(sectorSize, clusterSize, recordSize);
        var (first, second) = (g.Extents[0], g.Extents[1]);
        long size = Bytes(g, first.Count + second.Count);
        // The attribute list names the extension record many times over (once per extent it holds, or crafted): it
        // still counts once, so its runs aren't taken for overlapping copies.
        var list = AttributeList([(0, 0), .. Enumerable.Repeat((first.Count, (long)ExtensionRecord), 16)]);
        Write(g, [list, MftData(size, size, Runs(first), first.Count - 1)],
            [(ExtensionRecord, Extension(recordSize, first.Count, Runs(second)))]);
        AssertReadsTree();
    }

    [Fact]
    public void Mft_grown_after_format_with_more_allocated_than_in_use_reads()
    {
        var g = TwoExtents();
        // 8 clusters allocated, 6 of them in use (24 of 32 records), all mapped.
        long inUse = Bytes(g, 6);
        Write(g, [NonResident(MftRecordParser.AttrData, 0, 7, Bytes(g, 8), inUse, inUse, Runs(g.Extents))]);
        var records = Read();
        Assert.Equal("readme.txt", records.NameOf(17));
        Assert.Null(records.NameOf(31)); // past the records in use: never read
    }

    [Fact]
    public void Heavily_fragmented_mft_reads()
    {
        // 24 one-cluster extents, every other cluster.
        var extents = Enumerable.Range(0, 24).Select(i => (Lcn: 4L + 2 * i, Count: 1L)).ToArray();
        var g = new Geometry(512, 4096, 1024, 4 + 2 * 24 + 1, extents);
        long size = Bytes(g, 24);
        Write(g, [MftData(size, size, Runs(extents), 23)]);
        AssertReadsTree();
    }

    [Fact]
    public void Mft_past_four_gigabytes_on_a_large_volume_reads()
    {
        // A sparse 12 GB image (nothing on disk but these few records): record 0 near the end, the second extent at
        // 3 GB, so 64-bit offsets and a multi-byte negative run offset.
        long clusters = (12L << 30) / 4096;
        var g = new Geometry(512, 4096, 1024, clusters, [(clusters - 100, 4), ((3L << 30) / 4096, 4)]);
        long size = Bytes(g, 8);
        try { Write(g, [MftData(size, size, Runs(g.Extents), 7)], sparse: true); }
        catch (SkipException) { return; } // the temp folder's file system has no sparse files
        AssertReadsTree();
    }

    [Fact]
    public void Extension_records_mapping_the_same_vcns_are_rejected()
    {
        var g = TwoExtents();
        var (first, second) = (g.Extents[0], g.Extents[1]);
        long size = Bytes(g, first.Count + second.Count);
        // Two different records both claim VCN 4-7 (each with its own clusters).
        var list = AttributeList((0, 0), (first.Count, ExtensionRecord - 1), (first.Count, ExtensionRecord));
        Write(g, [list, MftData(size, size, Runs(first), first.Count - 1)],
        [
            (ExtensionRecord - 1, Extension(g.RecordSize, first.Count, Runs(second))),
            (ExtensionRecord, Extension(g.RecordSize, first.Count, Runs((1, 1), (second.Lcn + second.Count, 1)))),
        ]);
        AssertRejected();
    }

    [Fact]
    public void Extent_starting_past_where_the_last_one_ended_is_rejected()
    {
        var g = TwoExtents();
        var (first, second) = (g.Extents[0], g.Extents[1]);
        long size = Bytes(g, first.Count + second.Count + 1);
        // Record 0 maps VCN 0-3, the extension VCN 5-8: VCN 4 is nowhere.
        var list = AttributeList((0, 0), (first.Count + 1, ExtensionRecord));
        Write(g, [list, MftData(size, size, Runs(first), first.Count - 1)],
            [(ExtensionRecord, Extension(g.RecordSize, first.Count + 1, Runs(second)))]);
        AssertRejected();
    }

    [Fact]
    public void Runs_mapping_the_same_clusters_over_and_over_are_rejected()
    {
        // VCN 0-11 contiguous and within the volume, but every run is the same 4 clusters: 48 "records" out of 16.
        var g = TwoExtents();
        var runs = Enumerable.Repeat(g.Extents[0], 3).ToArray();
        long size = Bytes(g, 12);
        Write(g, [MftData(size, size, Runs(runs), 11)]);
        AssertRejected();
    }

    [Fact]
    public void Runs_mapping_more_than_the_allocated_size_are_rejected()
    {
        var g = TwoExtents();
        long size = Bytes(g, 4); // both extents mapped (8 clusters), 4 allocated
        Write(g, [MftData(size, size, Runs(g.Extents), 7)]);
        AssertRejected();
    }

    [Fact]
    public void Data_size_past_the_allocated_size_is_rejected()
    {
        var g = TwoExtents();
        Write(g, [MftData(Bytes(g, 8), Bytes(g, 8), Runs(g.Extents), 7)]);
        AssertReadsTree(); // as it should be

        Write(g, [NonResident(MftRecordParser.AttrData, 0, 7, Bytes(g, 8), Bytes(g, 9), Bytes(g, 9), Runs(g.Extents))]);
        AssertRejected();
    }

    [Fact]
    public void Runs_ending_before_the_last_record_are_rejected()
    {
        var g = TwoExtents();
        long size = Bytes(g, 8);
        // 8 clusters of records, but only the first extent mapped (and no attribute list for the rest).
        Write(g, [MftData(size, size, Runs(g.Extents[0]), 3)]);
        AssertRejected();
    }

    [Fact]
    public void Mft_extents_with_a_sparse_run_are_rejected()
    {
        var g = TwoExtents();
        long size = Bytes(g, 8);
        Write(g, [MftData(size, size, Runs(g.Extents[0], (-1, 4)), 7)]);
        AssertRejected();
    }

    sealed class SkipException : Exception;

    static unsafe bool TrySetSparse(SafeFileHandle file)
    {
        const uint FSCTL_SET_SPARSE = 0x000900C4;
        return Native.DeviceIoControl(file, FSCTL_SET_SPARSE, null, 0, null, 0, out _, 0);
    }
}

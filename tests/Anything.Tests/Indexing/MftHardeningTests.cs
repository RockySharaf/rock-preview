using System.Buffers.Binary;
using System.Diagnostics;
using Anything.Core;
using Anything.Core.Indexing;
using static Anything.Tests.Indexing.MftTestBuilder;

namespace Anything.Tests.Indexing;

/// <summary>
/// Crafted NTFS images and records, as a malicious USB stick would carry them: the LocalSystem service reads those on
/// plug-in, so nothing on the disk may make it allocate without bound or loop forever.
/// </summary>
public sealed class MftHardeningTests : IDisposable
{
    const int Cluster = 4096, RecordSize = 1024, MftLcn = 4, ImageClusters = 14;
    const ushort InUse = 1, Dir = 2;
    const long MaxAllocation = 16 << 20;
    readonly string _image = Path.Combine(Path.GetTempPath(), $"anything-ntfs-crafted-{Guid.NewGuid():N}.img");

    public void Dispose()
    {
        try { File.Delete(_image); } catch { }
    }

    /// <summary>A 14-cluster image whose $MFT (record 0 at LCN 4) has <paramref name="mftRuns"/> and claims <paramref name="mftBytes"/>.</summary>
    void WriteImage(byte[] mftRuns, long mftBytes, long totalSectors = ImageClusters * 8, params (int Rec, byte[] Bytes)[] records)
    {
        var img = new byte[ImageClusters * Cluster];
        "NTFS    "u8.CopyTo(img.AsSpan(3));
        BitConverter.GetBytes((ushort)512).CopyTo(img, 0x0B);
        img[0x0D] = 8;
        BitConverter.GetBytes(totalSectors).CopyTo(img, 0x28);
        BitConverter.GetBytes((long)MftLcn).CopyTo(img, 0x30);
        BitConverter.GetBytes(2L).CopyTo(img, 0x38);
        img[0x40] = 0xF6; // 1024-byte records

        void Put(int rec, byte[] bytes)
        {
            Protect(bytes, (ushort)(0x100 + rec));
            bytes.CopyTo(img, (long)MftLcn * Cluster + rec * RecordSize);
        }

        Put(0, Record(RecordSize, InUse, 0, StandardInformation(1, 1, FileAttributes.Hidden | FileAttributes.System),
            FileName(5, "$MFT", FileNameNamespace.Win32AndDos),
            NonResident(MftRecordParser.AttrData, 0, 0, mftBytes, mftBytes, mftBytes, mftRuns)));
        foreach (var (rec, bytes) in records) Put(rec, bytes);
        File.WriteAllBytes(_image, img);
    }

    /// <summary>Reads the image, returning the records and the managed bytes this thread allocated meanwhile.</summary>
    (VolumeRecords Records, long Allocated) Read()
    {
        using var handle = File.OpenHandle(_image, FileMode.Open, FileAccess.Read);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var records = MftReader.Read(handle, CancellationToken.None, null);
        return (records, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    long AllocatedByFailedRead()
    {
        using var handle = File.OpenHandle(_image, FileMode.Open, FileAccess.Read);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => MftReader.Read(handle, CancellationToken.None, null));
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Base_references_past_the_end_of_the_mft_are_skipped_without_allocating()
    {
        // 24 records (6 clusters); extension records claiming bases far beyond them.
        WriteImage([0x11, 0x06, 0x04], 24 * RecordSize, records:
        [
            (5, Record(RecordSize, InUse | Dir, 0, StandardInformation(1, 1, FileAttributes.Hidden), FileName(5, ".", FileNameNamespace.Win32AndDos))),
            (16, Record(RecordSize, InUse | Dir, 0, StandardInformation(2, 3, 0), FileName(5, "Users", FileNameNamespace.Win32AndDos))),
            (17, Record(RecordSize, InUse, 0x7FFF_FFFE, FileName(5, "evil.txt", FileNameNamespace.Win32))),
            (18, Record(RecordSize, InUse, 0xFFFF_FFFF_FFFF, FileName(5, "evil2.txt", FileNameNamespace.Win32))),
            (19, Record(RecordSize, InUse, 24, FileName(5, "just-past.txt", FileNameNamespace.Win32))),
            (20, Record(RecordSize, InUse, 0, StandardInformation(4, 5, FileAttributes.Archive), FileName(16, "real.txt", FileNameNamespace.Win32AndDos), ResidentData(7))),
        ]);

        var (records, allocated) = Read();

        Assert.True(allocated < MaxAllocation, $"allocated {allocated:N0} bytes");
        Assert.True(records.Length <= 24);
        var index = new FileIndex();
        records.BuildInto(index, "T:", ExclusionFilter.None);
        var paths = Enumerable.Range(0, index.Count).Where(index.IsAlive).Select(index.GetFullPath).OrderBy(p => p, StringComparer.Ordinal);
        Assert.Equal([@"T:\", @"T:\Users", @"T:\Users\real.txt"], paths);
    }

    [Fact]
    public void Mft_claiming_more_records_than_the_volume_holds_is_rejected_before_allocating()
    {
        // 16 M records (16 GB of $MFT) on a 56 KB volume.
        WriteImage([0x11, 0x06, 0x04], (16L << 20) * RecordSize);
        Assert.True(AllocatedByFailedRead() < MaxAllocation);
    }

    [Fact]
    public void Boot_sector_claiming_a_bigger_volume_than_the_device_does_not_widen_the_bounds()
    {
        // The boot sector says 512 TB and the $MFT run 64 GB; the image is 56 KB.
        WriteImage([0x14, 0x00, 0x00, 0x00, 0x01, 0x04], (16L << 20) * RecordSize, totalSectors: 1L << 40);
        Assert.True(AllocatedByFailedRead() < MaxAllocation);
    }

    [Fact]
    public void Sparse_mft_run_is_rejected()
    {
        // 2 real clusters, then 4 sparse ones.
        WriteImage([0x11, 0x02, 0x04, 0x01, 0x04], 24 * RecordSize);
        AllocatedByFailedRead();
    }

    [Fact]
    public void Mft_run_past_the_end_of_the_volume_is_rejected()
    {
        // 32 clusters from LCN 4 on a 14-cluster volume (only the first 6 hold records).
        WriteImage([0x11, 0x20, 0x04], 24 * RecordSize);
        AllocatedByFailedRead();
    }

    [Fact]
    public void Boot_sector_rejects_cluster_sizes_and_positions_that_would_overflow()
    {
        static byte[] Sector(byte spc, long total, long mftLcn)
        {
            var s = new byte[512];
            "NTFS    "u8.CopyTo(s.AsSpan(3));
            BitConverter.GetBytes((ushort)512).CopyTo(s, 0x0B);
            s[0x0D] = spc;
            BitConverter.GetBytes(total).CopyTo(s, 0x28);
            BitConverter.GetBytes(mftLcn).CopyTo(s, 0x30);
            s[0x40] = 0xF6;
            return s;
        }

        Assert.Equal(2 << 20, NtfsBootSector.Parse(Sector(0xF4, 1 << 30, 4)).BytesPerCluster); // 2 MB: the largest NTFS allows
        Assert.Throws<InvalidDataException>(() => NtfsBootSector.Parse(Sector(0xF3, 1 << 30, 4))); // 4 MB
        Assert.Throws<InvalidDataException>(() => NtfsBootSector.Parse(Sector(0x81, 1 << 30, 4))); // 2^127 sectors
        Assert.Throws<InvalidDataException>(() => NtfsBootSector.Parse(Sector(8, 0, 4)));
        Assert.Throws<InvalidDataException>(() => NtfsBootSector.Parse(Sector(8, -1, 4)));
        Assert.Throws<InvalidDataException>(() => NtfsBootSector.Parse(Sector(8, 1 << 30, long.MaxValue / 2)));
    }

    [Fact]
    public void Boot_sector_accepts_only_the_record_sizes_windows_formats()
    {
        static NtfsBootSector Parse(ushort bytesPerSector, byte sectorsPerCluster, byte clustersPerRecord)
        {
            var s = new byte[4096];
            "NTFS    "u8.CopyTo(s.AsSpan(3));
            BitConverter.GetBytes(bytesPerSector).CopyTo(s, 0x0B);
            s[0x0D] = sectorsPerCluster;
            BitConverter.GetBytes(1L << 30).CopyTo(s, 0x28);
            BitConverter.GetBytes(4L).CopyTo(s, 0x30);
            s[0x40] = clustersPerRecord;
            return NtfsBootSector.Parse(s);
        }

        Assert.Equal(1024, Parse(512, 8, 0xF6).BytesPerFileRecord);
        Assert.Equal(1024, Parse(512, 128, 0xF6).BytesPerFileRecord); // 64 KB clusters
        Assert.Equal(4096, Parse(4096, 1, 0xF4).BytesPerFileRecord); // 4K sectors
        Assert.Equal(4096, Parse(4096, 1, 0x01).BytesPerFileRecord); // given as one cluster
        // 64 KB records hold thousands of attribute-list entries, tens of thousands of runs; 1 << 74 would wrap to 1024.
        foreach (byte cpfr in new byte[] { 0xF8, 0xF7, 0xF5, 0xF3, 0xF0, 0xB6, 0x80 })
            Assert.Throws<InvalidDataException>(() => Parse(512, 8, cpfr));
        Assert.Throws<InvalidDataException>(() => Parse(512, 8, 0x02)); // two 4 KB clusters
        Assert.Throws<InvalidDataException>(() => Parse(512, 1, 0x01)); // one 512-byte cluster
    }

    [Fact]
    public void Extension_whose_base_comes_later_is_folded_in_and_one_whose_base_never_comes_is_dropped()
    {
        var vr = new VolumeRecords(64);
        var r = new MftRecord();
        void Add(long recNo, byte[] bytes)
        {
            Assert.True(MftRecordParser.Parse(bytes, recNo, r));
            vr.Merge(r);
        }

        Add(5, Record(1024, InUse | Dir, 0, StandardInformation(1, 1, 0), FileName(5, ".", FileNameNamespace.Win32AndDos)));
        // Bases beyond what the arrays hold yet: held back, not grown to.
        Add(30, Record(1024, InUse, 100, FileName(5, "later.bin", FileNameNamespace.Win32),
            NonResident(MftRecordParser.AttrData, 0, 3, 16384, 12_345, 12_345, [0x11, 0x04, 0x20])));
        Add(31, Record(1024, InUse, 0x7FFF_FFF0, FileName(5, "never.bin", FileNameNamespace.Win32)));
        Assert.True(vr.Length <= 64);
        Add(100, Record(1024, InUse, 0, StandardInformation(30, 40, FileAttributes.ReadOnly)));

        var index = new FileIndex();
        var (_, map, count) = vr.BuildInto(index, "T:", ExclusionFilter.None);
        Assert.Equal(1, count);
        int id = map.Get(100);
        Assert.Equal("later.bin", index.Name(id));
        Assert.Equal(12_345, index.Size(id));
        Assert.Equal(FileAttributes.ReadOnly, index.Attributes(id));
        Assert.Equal(101, vr.Length);
    }

    [Fact]
    public void Record_limit_bounds_usn_and_mft_records()
    {
        var vr = new VolumeRecords(64, recordLimit: 1000);
        vr.SetFromUsn(999, VolumeRecords.RootRecord, "last.txt", FileAttributes.Archive);
        vr.SetFromUsn(1000, VolumeRecords.RootRecord, "past.txt", FileAttributes.Archive);
        vr.SetFromUsn(int.MaxValue - 1, VolumeRecords.RootRecord, "far.txt", FileAttributes.Archive);
        var r = new MftRecord();
        Assert.True(MftRecordParser.Parse(Record(1024, InUse, 0, FileName(5, "beyond.txt", FileNameNamespace.Win32)), 5000, r));
        vr.Merge(r);
        Assert.Equal(1000, vr.Length);
        Assert.Equal("last.txt", vr.NameOf(999));
    }

    [Fact]
    public void Millions_of_hard_links_are_merged_in_linear_time()
    {
        var vr = new VolumeRecords();
        vr.EnsureRoot();
        var sw = Stopwatch.StartNew();
        // 200,000 names over 1,000 records: a scan of the link list per name would take minutes.
        for (int i = 0; i < 200_000; i++) vr.SetFromUsn(VolumeRecords.FirstUserRecord + i % 1000, VolumeRecords.RootRecord, "n" + i, FileAttributes.Archive);
        for (int i = 0; i < 1000; i++) vr.SetFromUsn(VolumeRecords.FirstUserRecord + i, VolumeRecords.RootRecord, "n" + i, FileAttributes.Archive); // repeats
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");

        var index = new FileIndex();
        var (_, _, count) = vr.BuildInto(index, "T:", ExclusionFilter.None);
        Assert.Equal(200_000, count);
    }

    [Fact]
    public void Crafted_lengths_and_counts_in_a_record_neither_allocate_nor_loop()
    {
        var r = new MftRecord();

        var fixups = Record(1024, InUse, 0, FileName(5, "a.txt", FileNameNamespace.Win32));
        BinaryPrimitives.WriteUInt16LittleEndian(fixups.AsSpan(6), 0xFFFF); // update sequence count
        Assert.False(MftRecordParser.ApplyFixups(fixups));

        var attrLength = Record(1024, InUse, 0, FileName(5, "a.txt", FileNameNamespace.Win32));
        int first = BinaryPrimitives.ReadUInt16LittleEndian(attrLength.AsSpan(0x14));
        BinaryPrimitives.WriteUInt32LittleEndian(attrLength.AsSpan(first + 4), 0xFFFF_FFF0);
        Assert.True(MftRecordParser.Parse(attrLength, 20, r));
        Assert.Empty(r.FileNames);

        var valueLength = Record(1024, InUse, 0, FileName(5, "a.txt", FileNameNamespace.Win32));
        BinaryPrimitives.WriteUInt32LittleEndian(valueLength.AsSpan(first + 0x10), 0x7FFF_FFFF);
        Assert.True(MftRecordParser.Parse(valueLength, 20, r));
        Assert.Empty(r.FileNames);

        var nameLength = Record(1024, InUse, 0, FileName(5, "a.txt", FileNameNamespace.Win32));
        int valueOfs = BinaryPrimitives.ReadUInt16LittleEndian(nameLength.AsSpan(first + 0x14));
        nameLength[first + valueOfs + 0x40] = 0xFF; // 255 characters claimed in a 5-character value
        Assert.True(MftRecordParser.Parse(nameLength, 20, r));
        Assert.Empty(r.FileNames);

        // A run claiming 2^63 clusters is one run, not 2^63 of anything.
        var run = Assert.Single(MftRecordParser.DecodeDataRuns([0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F]));
        Assert.True(run.IsSparse);
        Assert.Equal(long.MaxValue, run.Length);
    }
}

using Anything.Core;
using Anything.Core.Indexing;
using static Anything.Tests.Indexing.MftTestBuilder;

namespace Anything.Tests.Indexing;

/// <summary>
/// Runs the raw MFT reader end to end against a tiny synthetic NTFS image file (no administrator rights needed):
/// boot sector → $MFT record 0 → fragmented $DATA runs → record parsing → tree build.
/// </summary>
public sealed class MftReaderImageTests : IDisposable
{
    const int Cluster = 4096, RecordSize = 1024;
    const ushort InUse = 1, Dir = 2;
    readonly string _image = Path.Combine(Path.GetTempPath(), $"anything-ntfs-{Guid.NewGuid():N}.img");

    public void Dispose()
    {
        try { File.Delete(_image); } catch { }
    }

    // $MFT is fragmented: records 0-7 at LCN 4 (2 clusters), records 8-23 at LCN 9 (4 clusters).
    static long RecordOffset(int rec) => rec < 8 ? 4L * Cluster + rec * RecordSize : 9L * Cluster + (rec - 8) * RecordSize;

    void BuildImage()
    {
        var img = new byte[14 * Cluster];
        "NTFS    "u8.CopyTo(img.AsSpan(3));
        BitConverter.GetBytes((ushort)512).CopyTo(img, 0x0B);
        img[0x0D] = 8;
        BitConverter.GetBytes(14L * 8).CopyTo(img, 0x28);
        BitConverter.GetBytes(4L).CopyTo(img, 0x30);
        BitConverter.GetBytes(2L).CopyTo(img, 0x38);
        img[0x40] = 0xF6; // 1024-byte records

        void Put(int rec, byte[] bytes)
        {
            Protect(bytes, (ushort)(0x100 + rec));
            bytes.CopyTo(img, RecordOffset(rec));
        }

        byte[] mftRuns = [0x11, 0x02, 0x04, 0x11, 0x04, 0x05];
        Put(0, Record(RecordSize, InUse, 0, StandardInformation(1, 1, FileAttributes.Hidden | FileAttributes.System),
            FileName(5, "$MFT", FileNameNamespace.Win32AndDos),
            NonResident(MftRecordParser.AttrData, 0, 5, 6 * Cluster, 24 * RecordSize, 24 * RecordSize, mftRuns)));
        Put(5, Record(RecordSize, InUse | Dir, 0, StandardInformation(1, 1, FileAttributes.Hidden | FileAttributes.System), FileName(5, ".", FileNameNamespace.Win32AndDos)));
        Put(16, Record(RecordSize, InUse | Dir, 0, StandardInformation(2, 3, 0), FileName(5, "Users", FileNameNamespace.Win32AndDos)));
        Put(17, Record(RecordSize, InUse, 0, StandardInformation(4, 5, FileAttributes.Archive), FileName(16, "readme.txt", FileNameNamespace.Win32AndDos), ResidentData(10)));
        Put(18, Record(RecordSize, InUse, 0, StandardInformation(6, 7, FileAttributes.Archive), FileName(5, "big.iso", FileNameNamespace.Win32AndDos),
            NonResident(MftRecordParser.AttrData, 0, 1220, 1221L * Cluster, 5_000_000, 5_000_000, [0x21, 0xC5, 0x04, 0x64])));
        Put(19, Record(RecordSize, 0, 0, FileName(5, "ghost.txt", FileNameNamespace.Win32AndDos)));
        Put(22, Record(RecordSize, InUse, 0, StandardInformation(8, 9, FileAttributes.Archive), FileName(16, "far.txt", FileNameNamespace.Win32AndDos), ResidentData(3)));
        // A torn record (bad update sequence) must be skipped, not crash the scan.
        var torn = Record(RecordSize, InUse, 0, FileName(5, "torn.txt", FileNameNamespace.Win32AndDos));
        Protect(torn);
        torn[1022] ^= 0xFF;
        torn.CopyTo(img, RecordOffset(23));
        File.WriteAllBytes(_image, img);
    }

    [Fact]
    public void Reads_fragmented_mft_from_image_and_builds_tree()
    {
        BuildImage();
        VolumeRecords records;
        using (var handle = File.OpenHandle(_image, FileMode.Open, FileAccess.Read))
            records = MftReader.Read(handle, CancellationToken.None, null);

        Assert.True(records.IsInUse(5));
        Assert.True(records.IsDirectory(16));
        Assert.Equal("far.txt", records.NameOf(22));
        Assert.Equal(5_000_000, records.SizeOf(18));
        Assert.False(records.IsInUse(19));
        Assert.Null(records.NameOf(23));

        var index = new FileIndex();
        var (rootId, map, count) = records.BuildInto(index, "T:", ExclusionFilter.None);
        var paths = Enumerable.Range(0, index.Count).Where(index.IsAlive).Select(index.GetFullPath).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal([@"T:\", @"T:\Users", @"T:\Users\far.txt", @"T:\Users\readme.txt", @"T:\big.iso"], paths);
        Assert.Equal(4, count);
        Assert.Equal(10, index.Size(map.Get(17)));
        Assert.Equal(5, index.Modified(map.Get(17)));
        Assert.Equal(4, index.Created(map.Get(17)));
        Assert.Equal(FileAttributes.Archive, index.Attributes(map.Get(17)));
        Assert.Equal(rootId, index.Parent(map.Get(18)));
    }
}

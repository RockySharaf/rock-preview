using Anything.Core;
using Anything.Core.Indexing;
using static Anything.Tests.Indexing.MftTestBuilder;

namespace Anything.Tests.Indexing;

public class MftParserTests
{
    const ushort InUse = 1, Dir = 2;

    [Fact]
    public void Fixups_restore_sector_tails_and_detect_torn_records()
    {
        var rec = Record(1024, InUse, 0, StandardInformation(1, 2, FileAttributes.Archive));
        rec[510] = 0x12; rec[511] = 0x34; rec[1022] = 0x56; rec[1023] = 0x78;
        var original = (byte[])rec.Clone();
        Protect(rec);
        Assert.NotEqual(original[510], rec[510]);

        Assert.True(MftRecordParser.ApplyFixups(rec));
        // Everything except the update sequence array itself is restored.
        Array.Copy(rec, UsaOffset, original, UsaOffset, 6);
        Assert.Equal(original, rec);
        Assert.Equal(0x34, rec[511]);
        Assert.Equal(0x78, rec[1023]);

        var torn = (byte[])original.Clone();
        Protect(torn);
        torn[1022] ^= 0xFF; // second sector written by a different update
        Assert.False(MftRecordParser.ApplyFixups(torn));
    }

    [Fact]
    public void Parses_standard_information_win32_name_and_resident_data_skipping_dos_name()
    {
        var rec = Record(1024, InUse, 0,
            StandardInformation(created: 111, modified: 222, FileAttributes.Hidden | FileAttributes.Archive),
            FileName(5, "LONGFI~1.TXT", FileNameNamespace.Dos),
            FileName(5, "Long file name.txt", FileNameNamespace.Win32),
            ResidentData(37));
        Protect(rec);
        Assert.True(MftRecordParser.ApplyFixups(rec));

        var r = new MftRecord();
        Assert.True(MftRecordParser.Parse(rec, 42, r));
        Assert.Equal(42, r.RecordNumber);
        Assert.True(r.InUse);
        Assert.False(r.IsDirectory);
        Assert.Equal(0, r.BaseRecord);
        Assert.True(r.HasStandardInformation);
        Assert.Equal(111, r.CreationTime);
        Assert.Equal(222, r.ModificationTime);
        Assert.Equal(FileAttributes.Hidden | FileAttributes.Archive, r.Attributes);
        var fn = Assert.Single(r.FileNames);
        Assert.Equal("Long file name.txt", fn.Name);
        Assert.Equal(5, fn.ParentRecord);
        Assert.Equal(FileNameNamespace.Win32, fn.Namespace);
        Assert.True(r.HasData);
        Assert.False(r.DataNonResident);
        Assert.Equal(37, r.DataSize);
    }

    [Fact]
    public void Parses_non_resident_data_size_and_runs_and_ignores_named_streams()
    {
        byte[] runs = [0x31, 0x10, 0x00, 0x10, 0x00, 0x21, 0x08, 0x00, 0xF0, 0x00];
        var rec = Record(1024, InUse, 0,
            StandardInformation(1, 2, FileAttributes.Normal),
            FileName(5, "big.bin", FileNameNamespace.Win32AndDos),
            NonResident(MftRecordParser.AttrData, 0, 23, 24 * 4096, 90_000, 90_000, runs),
            NonResident(MftRecordParser.AttrData, 0, 0, 4096, 7, 7, [0x11, 0x01, 0x05], name: "Zone.Identifier"));
        var r = new MftRecord();
        Assert.True(MftRecordParser.Parse(rec, 30, r, captureDataRuns: true));
        Assert.True(r.HasData);
        Assert.True(r.DataNonResident);
        Assert.Equal(90_000, r.DataSize);
        Assert.Equal(24 * 4096, r.DataAllocatedSize);
        Assert.Equal([new DataRun(0, 4096, 16), new DataRun(16, 0, 8)], r.DataRuns);
        Assert.Equal("big.bin", Assert.Single(r.FileNames).Name);
    }

    [Fact]
    public void Dos_only_record_falls_back_to_the_short_name()
    {
        var rec = Record(1024, InUse, 0, FileName(5, "DOSONLY.TXT", FileNameNamespace.Dos));
        var r = new MftRecord();
        Assert.True(MftRecordParser.Parse(rec, 20, r));
        var fn = Assert.Single(r.FileNames);
        Assert.Equal("DOSONLY.TXT", fn.Name);
        Assert.Equal(FileNameNamespace.Dos, fn.Namespace);
    }

    [Fact]
    public void Rejects_non_file_records()
    {
        var rec = new byte[1024];
        "BAAD"u8.CopyTo(rec);
        Assert.False(MftRecordParser.HasFileSignature(rec));
        Assert.False(MftRecordParser.Parse(rec, 1, new MftRecord()));
    }

    [Fact]
    public void Data_runs_decode_positive_negative_and_sparse_runs()
    {
        // len 0x10 @ +0x1000, len 8 @ -0x1000 (back to LCN 0), sparse len 5, len 0x100 @ +0x123456
        byte[] mapping = [0x31, 0x10, 0x00, 0x10, 0x00, 0x21, 0x08, 0x00, 0xF0, 0x01, 0x05, 0x32, 0x00, 0x01, 0x56, 0x34, 0x12, 0x00];
        var runs = MftRecordParser.DecodeDataRuns(mapping, startVcn: 100);
        Assert.Equal(
            [new DataRun(100, 0x1000, 0x10), new DataRun(116, 0, 8), new DataRun(124, -1, 5), new DataRun(129, 0x123456, 0x100)],
            runs);
        Assert.True(runs[2].IsSparse);
        Assert.Throws<InvalidDataException>(() => MftRecordParser.DecodeDataRuns([0x31, 0x10]));
    }

    [Fact]
    public void Boot_sector_geometry()
    {
        var s = new byte[512];
        "NTFS    "u8.CopyTo(s.AsSpan(3));
        BitConverter.GetBytes((ushort)512).CopyTo(s, 0x0B);
        s[0x0D] = 8;
        BitConverter.GetBytes(500_000_000L).CopyTo(s, 0x28);
        BitConverter.GetBytes(786_432L).CopyTo(s, 0x30);
        BitConverter.GetBytes(2L).CopyTo(s, 0x38);
        s[0x40] = 0xF6; // -10 → 1024-byte records
        var bs = NtfsBootSector.Parse(s);
        Assert.Equal(512, bs.BytesPerSector);
        Assert.Equal(4096, bs.BytesPerCluster);
        Assert.Equal(786_432L, bs.MftLcn);
        Assert.Equal(1024, bs.BytesPerFileRecord);

        s[3] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => NtfsBootSector.Parse(s));
    }

    [Fact]
    public void Volume_records_build_tree_with_hard_links_extensions_and_exclusions()
    {
        var vr = new VolumeRecords();
        var r = new MftRecord();
        void Add(long recNo, byte[] bytes)
        {
            Protect(bytes);
            Assert.True(MftRecordParser.ApplyFixups(bytes));
            Assert.True(MftRecordParser.Parse(bytes, recNo, r));
            vr.Merge(r);
        }

        Add(5, Record(1024, InUse | Dir, 0, StandardInformation(1, 1, FileAttributes.Hidden | FileAttributes.System), FileName(5, ".", FileNameNamespace.Win32AndDos)));
        Add(3, Record(1024, InUse, 0, StandardInformation(1, 1, 0), FileName(5, "$Volume", FileNameNamespace.Win32AndDos)));
        Add(16, Record(1024, InUse | Dir, 0, StandardInformation(1, 1, 0), FileName(5, "A", FileNameNamespace.Win32AndDos)));
        Add(17, Record(1024, InUse | Dir, 0, StandardInformation(1, 1, 0), FileName(5, "B", FileNameNamespace.Win32AndDos)));
        // Hard link: one record, two Win32 names in two folders (+ a DOS alias that must be ignored).
        Add(18, Record(1024, InUse, 0, StandardInformation(10, 20, FileAttributes.Archive),
            FileName(16, "f.txt", FileNameNamespace.Win32AndDos),
            FileName(17, "g long.txt", FileNameNamespace.Win32),
            FileName(17, "GLONG~1.TXT", FileNameNamespace.Dos),
            ResidentData(5)));
        // Extension record (19) seen before its base (20): its name and size merge into record 20.
        Add(19, Record(1024, InUse, 20, FileName(5, "ext.bin", FileNameNamespace.Win32),
            NonResident(MftRecordParser.AttrData, 0, 3, 16384, 12_345, 12_345, [0x11, 0x04, 0x20])));
        Add(20, Record(1024, InUse, 0, StandardInformation(30, 40, FileAttributes.ReadOnly),
            Resident(MftRecordParser.AttrAttributeList, new byte[0x20])));
        Add(21, Record(1024, InUse, 0, FileName(5, "DOSONLY.TXT", FileNameNamespace.Dos)));
        Add(22, Record(1024, InUse, 0, FileName(999, "orphan.txt", FileNameNamespace.Win32)));
        Add(23, Record(1024, 0, 0, FileName(5, "deleted.txt", FileNameNamespace.Win32)));
        Add(24, Record(1024, InUse, 0, FileName(5, "skip.tmp", FileNameNamespace.Win32)));
        Add(25, Record(1024, InUse | Dir, 0, FileName(5, "Excluded", FileNameNamespace.Win32)));
        Add(26, Record(1024, InUse, 0, FileName(25, "inside.txt", FileNameNamespace.Win32)));

        var index = new FileIndex();
        var filter = new ExclusionFilter(new IndexSettings { ExcludePatterns = ["*.tmp"], ExcludeFolders = [@"X:\Excluded"] });
        var (rootId, map, count) = vr.BuildInto(index, "X:", filter);

        var paths = Enumerable.Range(0, index.Count).Where(index.IsAlive).Select(index.GetFullPath).OrderBy(p => p).ToArray();
        Assert.Equal(
            [@"X:\", @"X:\A", @"X:\A\f.txt", @"X:\B", @"X:\B\g long.txt", @"X:\DOSONLY.TXT", @"X:\ext.bin"],
            paths);
        Assert.Equal(6, count);
        Assert.Equal(rootId, map.Get(5));
        Assert.Equal(FileIndex.NoParent, index.Parent(rootId));

        var tmp = new List<int>();
        map.GetAll(18, tmp);
        Assert.Equal(2, tmp.Count);
        int f = tmp.Single(id => index.Name(id) == "f.txt");
        Assert.Equal(5, index.Size(f));
        Assert.Equal(20, index.Modified(f));
        Assert.Equal(10, index.Created(f));

        int ext = map.Get(20);
        Assert.Equal("ext.bin", index.Name(ext));
        Assert.Equal(12_345, index.Size(ext));
        Assert.Equal(FileAttributes.ReadOnly, index.Attributes(ext));
        Assert.True(index.IsDirectory(map.Get(16)));
        Assert.Equal(-1, index.Size(map.Get(16)));
        Assert.Equal(-1, map.Get(22));
        Assert.Equal(-1, map.Get(3));

        var inverse = map.BuildInverse(index.Count);
        Assert.Equal(20, inverse[ext]);
    }

    [Fact]
    public void Usn_v2_record_parses()
    {
        string name = "new file.txt";
        int len = (0x3C + name.Length * 2 + 7) & ~7;
        var b = new byte[len];
        BitConverter.GetBytes(len).CopyTo(b, 0);
        BitConverter.GetBytes((ushort)2).CopyTo(b, 4);
        BitConverter.GetBytes(0x0003_0000_0000_1234UL).CopyTo(b, 8);
        BitConverter.GetBytes(0x0001_0000_0000_0005UL).CopyTo(b, 16);
        BitConverter.GetBytes(999L).CopyTo(b, 24);
        BitConverter.GetBytes(123456L).CopyTo(b, 32);
        BitConverter.GetBytes((uint)(UsnReason.FileCreate | UsnReason.Close)).CopyTo(b, 40);
        BitConverter.GetBytes((uint)FileAttributes.Archive).CopyTo(b, 52);
        BitConverter.GetBytes((ushort)(name.Length * 2)).CopyTo(b, 56);
        BitConverter.GetBytes((ushort)0x3C).CopyTo(b, 58);
        System.Text.Encoding.Unicode.GetBytes(name).CopyTo(b, 0x3C);

        Assert.True(UsnRecordParser.TryParse(b, out var r, out int read));
        Assert.Equal(len, read);
        Assert.Equal(0x1234, r.Record);
        Assert.Equal(5, r.ParentRecord);
        Assert.Equal(999, r.Usn);
        Assert.Equal(UsnReason.FileCreate | UsnReason.Close, r.Reason);
        Assert.Equal(FileAttributes.Archive, r.Attributes);
        Assert.Equal(name, r.Name);
    }
}

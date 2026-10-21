using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

/// <summary>
/// Dates accessed from the file table to the index (directly, through the service, in the database), the database
/// staying readable across versions, and recent changes from the change journal.
/// </summary>
public sealed class FileInformationIndexingTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("anything-info-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Mft_record_gives_its_date_accessed()
    {
        var si = MftTestBuilder.StandardInformation(created: 111, modified: 222, FileAttributes.Archive);
        BinaryPrimitives.WriteInt64LittleEndian(si.AsSpan(0x18 + 0x18), 333); // the value starts at 0x18 in the attribute
        var rec = MftTestBuilder.Record(1024, 0x01, 0, si, MftTestBuilder.FileName(5, "a.txt", FileNameNamespace.Win32));
        var parsed = new MftRecord();
        Assert.True(MftRecordParser.Parse(rec, 40, parsed));
        Assert.Equal(111, parsed.CreationTime);
        Assert.Equal(222, parsed.ModificationTime);
        Assert.Equal(333, parsed.AccessTime);

        var records = new VolumeRecords();
        records.EnsureRoot();
        records.Merge(parsed);
        Assert.True(records.HasAccessTimes);
        var index = new FileIndex();
        index.Configure(dateAccessed: true, folderSizes: false, recentChanges: false);
        records.BuildInto(index, "C:", ExclusionFilter.None);
        int a = Enumerable.Range(0, index.Count).Single(i => index.Name(i) == "a.txt");
        Assert.Equal(333, index.Accessed(a));
    }

    static VolumeRecords WithAccessTime()
    {
        var si = MftTestBuilder.StandardInformation(1, 2, FileAttributes.Archive);
        BinaryPrimitives.WriteInt64LittleEndian(si.AsSpan(0x18 + 0x18), 777);
        var parsed = new MftRecord();
        MftRecordParser.Parse(MftTestBuilder.Record(1024, 0x01, 0, si, MftTestBuilder.FileName(5, "f.txt", FileNameNamespace.Win32)), 16, parsed);
        var records = new VolumeRecords();
        records.EnsureRoot();
        records.Merge(parsed);
        return records;
    }

    static VolumeRecords Transfer(VolumeRecords records, bool accessTimes)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) records.WriteTo(w, accessTimes);
        ms.Position = 0;
        using var r = new BinaryReader(ms);
        var copy = VolumeRecords.ReadFrom(r, accessTimes);
        Assert.Equal(ms.Length, ms.Position);
        return copy;
    }

    [Fact]
    public void Service_sends_dates_accessed_with_protocol_2_and_none_with_1()
    {
        var records = WithAccessTime();
        var index = new FileIndex();
        index.Configure(dateAccessed: true, folderSizes: false, recentChanges: false);

        var current = Transfer(records, accessTimes: true);
        Assert.True(current.HasAccessTimes);
        current.BuildInto(index, "C:", ExclusionFilter.None);
        Assert.Equal(777, index.Accessed(Enumerable.Range(0, index.Count).Single(i => index.Name(i) == "f.txt")));

        // An app and a service from before dates accessed still understand each other.
        var old = Transfer(records, accessTimes: false);
        Assert.False(old.HasAccessTimes);
        Assert.Equal(records.NameOf(16), old.NameOf(16));
    }

    [Fact]
    public void Database_keeps_dates_accessed_and_works_out_folder_sizes_again()
    {
        var index = new FileIndex();
        index.Configure(dateAccessed: true, folderSizes: true, recentChanges: true);
        int c = index.AddRoot("C:");
        int dir = index.Add("Dir", c, FileAttributes.Directory, -1, 10, 20, 30);
        int file = index.Add("f.bin", dir, FileAttributes.Archive, 500, 11, 21, 31);
        index.SetRecentChange(file, 99);
        string db = Path.Combine(_dir, "v3.db");
        IndexDatabase.Save(db, index, [new DbRoot { Path = "C:", Kind = RootKind.Folder, RootId = c, HasDateAccessed = true }]);

        // Loaded with date accessed and folder sizes on.
        var loaded = new FileIndex();
        loaded.Configure(dateAccessed: true, folderSizes: true, recentChanges: true);
        var roots = IndexDatabase.Load(db, loaded)!;
        Assert.True(roots[0].HasDateAccessed);
        int lf = roots[0].Ids.Single(i => loaded.Name(i) == "f.bin");
        int ld = roots[0].Ids.Single(i => loaded.Name(i) == "Dir");
        Assert.Equal(31, loaded.Accessed(lf));
        Assert.Equal(30, loaded.Accessed(ld));
        Assert.Equal(500, loaded.Size(ld));
        Assert.Equal(500, loaded.Size(roots[0].RootId));
        Assert.Equal(0, loaded.RecentChange(lf)); // recent changes are since the start, as in Everything

        // Loaded with both off: no stale folder totals, no dates kept, and the root says so (a rescan reads them later).
        var plain = new FileIndex();
        roots = IndexDatabase.Load(db, plain)!;
        Assert.False(roots[0].HasDateAccessed);
        Assert.Equal(-1, plain.Size(roots[0].Ids.Single(i => plain.Name(i) == "Dir")));
        Assert.Equal(500, plain.Size(roots[0].Ids.Single(i => plain.Name(i) == "f.bin")));
    }

    /// <summary>A version 2 database, byte for byte as 1.4.x wrote it (no flags, no dates accessed).</summary>
    static void WriteVersion2(string path)
    {
        using var fs = File.Create(path);
        fs.Write("ANYTHGDB"u8);
        fs.Write(BitConverter.GetBytes(2));
        using var brotli = new BrotliStream(fs, CompressionLevel.Fastest);
        using var w = new BinaryWriter(brotli, Encoding.UTF8);
        w.Write(1);                 // roots
        w.Write("C:");
        w.Write((byte)RootKind.Ntfs);
        w.Write(42UL);              // journal id
        w.Write(1000L);             // next USN
        w.Write(7UL);               // rules stamp
        w.Write(2);                 // entries
        w.Write(-1); w.Write("C:"); w.Write((uint)FileAttributes.Directory); w.Write(-1L); w.Write(5L); w.Write(6L); w.Write(5L);
        w.Write(0); w.Write("old.txt"); w.Write((uint)FileAttributes.Archive); w.Write(12L); w.Write(7L); w.Write(8L); w.Write(64L);
        w.Write(0x21444E45);        // "END!"
    }

    [Fact]
    public void Version_2_database_still_loads()
    {
        string db = Path.Combine(_dir, "v2.db");
        WriteVersion2(db);
        var index = new FileIndex();
        index.Configure(dateAccessed: true, folderSizes: false, recentChanges: true);
        var roots = IndexDatabase.Load(db, index);
        Assert.NotNull(roots);
        var root = Assert.Single(roots);
        Assert.Equal(42UL, root.JournalId);
        Assert.Equal(7UL, root.RulesStamp);
        Assert.False(root.HasDateAccessed); // so turning date accessed on reads the drive again
        int old = root.Ids[1];
        Assert.Equal(@"C:\old.txt", index.GetFullPath(old));
        Assert.Equal(12, index.Size(old));
        Assert.Equal(0, index.Accessed(old));
        Assert.Equal([5L, 64L], root.Frns!);
    }

    sealed class Host(bool fromJournal) : IIndexerHost
    {
        public FileIndex Index { get; } = new();
        public ExclusionFilter Filter => ExclusionFilter.None;
        public bool RecentChangesFromJournal => fromJournal;
        public void MarkChanged() { }
        public void RequestRescan(RootIndexer root, string reason) { }
        public void ReportProgress(RootIndexer root, long objects) { }
    }

    /// <summary>A volume with records 16 (old.txt) and 17 (kept.txt), and a journal handed out in reads.</summary>
    sealed unsafe class JournalVolume : IVolumeSource
    {
        public readonly Queue<(byte[] Records, long Next)> Reads = new();
        public long FirstUsn;
        public string Kind => "local";

        public bool TryQueryJournal(bool create, out UsnJournalInfo info)
        {
            info = new UsnJournalInfo(7, FirstUsn, 1000, 0, long.MaxValue, 1 << 20, 1 << 16);
            return true;
        }

        public VolumeRecords ReadRecords(long highUsn, CancellationToken ct, Action<int> progress, out string method)
        {
            method = "MFT";
            var records = new VolumeRecords();
            records.EnsureRoot();
            records.SetFromUsn(16, VolumeRecords.RootRecord, "old.txt", FileAttributes.Archive);
            records.SetFromUsn(17, VolumeRecords.RootRecord, "kept.txt", FileAttributes.Archive);
            records.SetFromUsn(16, VolumeRecords.RootRecord, "old link.txt", FileAttributes.Archive); // a second hard link of 16
            return records;
        }

        public int ReadJournal(ulong journalId, long start, byte* buffer, int size)
        {
            *(long*)buffer = start;
            lock (Reads)
            {
                if (Reads.Count == 0) return 8;
                var (records, next) = Reads.Dequeue();
                *(long*)buffer = next;
                records.CopyTo(new Span<byte>(buffer + 8, size - 8));
                return 8 + records.Length;
            }
        }

        public void Dispose() { }
    }

    static byte[] Usn(long record, long usn, long time, string name, UsnReason reason)
    {
        int len = (0x3C + name.Length * 2 + 7) & ~7;
        var b = new byte[len];
        BitConverter.GetBytes(len).CopyTo(b, 0);
        BitConverter.GetBytes((ushort)2).CopyTo(b, 4);
        BitConverter.GetBytes((ulong)record | 1UL << 48).CopyTo(b, 8);
        BitConverter.GetBytes((ulong)VolumeRecords.RootRecord | 1UL << 48).CopyTo(b, 16);
        BitConverter.GetBytes(usn).CopyTo(b, 24);
        BitConverter.GetBytes(time).CopyTo(b, 32);
        BitConverter.GetBytes((uint)reason).CopyTo(b, 40);
        BitConverter.GetBytes((uint)FileAttributes.Archive).CopyTo(b, 52);
        BitConverter.GetBytes((ushort)(name.Length * 2)).CopyTo(b, 56);
        BitConverter.GetBytes((ushort)0x3C).CopyTo(b, 58);
        Encoding.Unicode.GetBytes(name).CopyTo(b, 0x3C);
        return b;
    }

    static bool WaitFor(Func<bool> condition, int ms = 5000)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            try { if (condition()) return true; }
            catch (InvalidOperationException) { } // not there yet
            Thread.Sleep(20);
        }
        return false;
    }

    static int IdOf(FileIndex index, string name) => Enumerable.Range(0, index.Count).Single(i => index.IsAlive(i) && index.Name(i) == name);

    [Fact]
    public void Journal_records_mark_recent_changes_at_their_own_time()
    {
        var host = new Host(fromJournal: false);
        host.Index.Configure(dateAccessed: false, folderSizes: false, recentChanges: true);
        var volume = new JournalVolume();
        using var root = new NtfsVolumeIndexer(host, "T:", volume);
        root.Scan(CancellationToken.None);
        lock (volume.Reads)
            volume.Reads.Enqueue(([.. Usn(16, 1000, 5_000, "old.txt", UsnReason.DataExtend | UsnReason.Close),
                                   .. Usn(18, 1100, 6_000, "new.txt", UsnReason.FileCreate),
                                   .. Usn(17, 1200, 7_000, "kept.txt", UsnReason.SecurityChange),
                                   .. Usn(18, 1250, 7_500, "new.txt", UsnReason.FileDelete)], 1300));
        root.StartMonitoring();
        Assert.True(WaitFor(() => host.Index.RecentChange(IdOf(host.Index, "old.txt")) == 5_000));
        Assert.True(WaitFor(() => host.Index.RecentChange(IdOf(host.Index, "kept.txt")) == 7_000)); // permissions count, as in Everything
        Assert.Equal(0, host.Index.RecentChange(IdOf(host.Index, "old link.txt"))); // the other hard link wasn't named
        Assert.DoesNotContain(Enumerable.Range(0, host.Index.Count), i => host.Index.IsAlive(i) && host.Index.Name(i) == "new.txt"); // made, then deleted
        root.StopMonitoring();
    }

    [Fact]
    public void Recent_changes_survive_a_rescan()
    {
        var host = new Host(fromJournal: false);
        host.Index.Configure(dateAccessed: false, folderSizes: false, recentChanges: true);
        var volume = new JournalVolume();
        using var root = new NtfsVolumeIndexer(host, "T:", volume);
        root.Scan(CancellationToken.None);
        host.Index.SetRecentChange(IdOf(host.Index, "kept.txt"), 4_000);
        host.Index.SetRecentChange(IdOf(host.Index, "old link.txt"), 5_000);
        int before = IdOf(host.Index, "kept.txt");

        root.Scan(CancellationToken.None); // the journal wrapped, say: every entry is made again
        Assert.NotEqual(before, IdOf(host.Index, "kept.txt"));
        Assert.Equal(4_000, host.Index.RecentChange(IdOf(host.Index, "kept.txt")));
        Assert.Equal(5_000, host.Index.RecentChange(IdOf(host.Index, "old link.txt"))); // the same link of the file
        Assert.Equal(0, host.Index.RecentChange(IdOf(host.Index, "old.txt")));
    }

    [Fact]
    public void Recent_changes_survive_a_folder_rescan()
    {
        string dir = Path.Combine(_dir, "scanned");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "a.txt"), "a");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "b");
        var host = new Host(fromJournal: false);
        host.Index.Configure(dateAccessed: true, folderSizes: false, recentChanges: true);
        using var root = new FolderIndexer(host, dir);
        root.Scan(CancellationToken.None);
        Assert.True(root.HasDateAccessed);
        int a = IdOf(host.Index, "a.txt");
        Assert.NotEqual(0, host.Index.Accessed(a)); // from the folder listing
        host.Index.SetRecentChange(a, 4_000);
        host.Index.SetRecentChange(IdOf(host.Index, "sub"), 3_000);

        root.Scan(CancellationToken.None); // a network drive's hourly check, say: a new copy swapped in
        Assert.NotEqual(a, IdOf(host.Index, "a.txt"));
        Assert.Equal(4_000, host.Index.RecentChange(IdOf(host.Index, "a.txt")));
        Assert.Equal(3_000, host.Index.RecentChange(IdOf(host.Index, "sub")));
        Assert.Equal(0, host.Index.RecentChange(IdOf(host.Index, "b.txt")));
        root.StopMonitoring();
    }

    [Fact]
    public void Recent_changes_can_reach_back_through_the_whole_journal_at_startup()
    {
        var host = new Host(fromJournal: true);
        host.Index.Configure(dateAccessed: false, folderSizes: false, recentChanges: true);
        var volume = new JournalVolume { FirstUsn = 10 };
        using var root = new NtfsVolumeIndexer(host, "T:", volume);
        root.Scan(CancellationToken.None); // monitoring starts at USN 1000 (the journal's next USN)
        // Before that point: only read for their times, not applied again.
        lock (volume.Reads)
            volume.Reads.Enqueue(([.. Usn(16, 10, 1_000, "old.txt", UsnReason.DataOverwrite),
                                   .. Usn(16, 20, 3_000, "old.txt", UsnReason.BasicInfoChange),
                                   .. Usn(17, 30, 2_000, "kept.txt", UsnReason.RenameNewName),
                                   .. Usn(99, 40, 4_000, "deleted long ago.txt", UsnReason.FileCreate)], 1000));
        root.StartMonitoring();
        Assert.True(WaitFor(() => host.Index.RecentChange(IdOf(host.Index, "old.txt")) == 3_000));
        Assert.Equal(2_000, host.Index.RecentChange(IdOf(host.Index, "kept.txt")));
        Assert.Equal(0, host.Index.RecentChange(IdOf(host.Index, "old link.txt")));
        Assert.DoesNotContain(Enumerable.Range(0, host.Index.Count), i => host.Index.IsAlive(i) && host.Index.Name(i) == "deleted long ago.txt");
        root.StopMonitoring();
    }
}

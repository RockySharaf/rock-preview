using Anything.Core;
using Anything.Core.Indexing;
using static Anything.Tests.Indexing.DriveIndexingTestSupport;

namespace Anything.Tests.Indexing;

/// <summary>
/// <see cref="RootIndexer.Complete"/>: a root's entries are a whole copy. A volume's come with its finished scan; a
/// folder's first scan adds them as it goes.
/// </summary>
public sealed class RootCompleteTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("anything-complete-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    sealed class Host : IIndexerHost
    {
        public FileIndex Index { get; } = new();
        public ExclusionFilter Filter => ExclusionFilter.None;
        public void MarkChanged() { }
        public void RequestRescan(RootIndexer root, string reason) { }
        public void ReportProgress(RootIndexer root, long objects) { }
    }

    /// <summary>A volume of one file, a.txt, with a journal that never has anything new.</summary>
    sealed unsafe class OneFileVolume : IVolumeSource
    {
        public string Kind => "local";

        public bool TryQueryJournal(bool create, out UsnJournalInfo info)
        {
            info = new UsnJournalInfo(7, 0, 100, 0, long.MaxValue, 1 << 20, 1 << 16);
            return true;
        }

        public VolumeRecords ReadRecords(long highUsn, CancellationToken ct, Action<int> progress, out string method)
        {
            method = "MFT";
            var r = new VolumeRecords();
            r.EnsureRoot();
            r.SetFromUsn(16, VolumeRecords.RootRecord, "a.txt", FileAttributes.Archive);
            return r;
        }

        public int ReadJournal(ulong journalId, long start, byte* buffer, int size)
        {
            *(long*)buffer = start;
            return 8;
        }

        public void Dispose() { }
    }

    [Fact]
    public void Folder_is_complete_once_its_first_scan_finishes()
    {
        for (int i = 0; i < 20; i++) Write(Path.Combine(_root, $"sub{i}", "a.txt"));
        var host = new Host();
        using var root = new FolderIndexer(host, _root);
        Assert.False(root.Complete);

        // As each folder's entries are added: there, but partial.
        int during = 0, partial = 0;
        host.Index.Changed += () =>
        {
            if (root.RootId < 0) return;
            Interlocked.Increment(ref during);
            if (!root.Complete) Interlocked.Increment(ref partial);
        };
        root.Scan(CancellationToken.None);
        Assert.True(during > 0, "no entries added while it scanned");
        Assert.Equal(during, partial);
        Assert.True(root.Complete);
        Assert.Equal(41, host.Index.LiveCount);

        // A rescan swaps in a new copy when done: complete throughout.
        partial = 0;
        root.Scan(CancellationToken.None);
        Assert.Equal(0, partial);
        Assert.True(root.Complete);
    }

    [Theory]
    [InlineData(0L, false)] // saved partway through its first scan
    [InlineData(132_000_000_000_000_000L, true)]
    public void Folder_loaded_from_the_database_is_complete_when_a_scan_of_it_had_finished(long lastScan, bool complete)
    {
        var host = new Host();
        int id = host.Index.Add(@"Q:\data", FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
        using var root = new FolderIndexer(host, @"Q:\data");
        root.AttachLoaded(new LoadedRoot { Path = @"Q:\data", Kind = RootKind.Folder, RootId = id, NextUsn = lastScan, Ids = [id] });
        Assert.Equal(id, root.RootId);
        Assert.Equal(complete, root.Complete);
    }

    [Fact]
    public void Volume_is_complete_once_it_has_entries()
    {
        var host = new Host();
        using var volume = new NtfsVolumeIndexer(host, "T:", new OneFileVolume());
        Assert.False(volume.Complete);
        volume.Scan(CancellationToken.None);
        Assert.True(volume.Complete);
        Assert.Equal([@"T:\", @"T:\a.txt"], Paths(host.Index));

        // Loaded from the database: only a finished scan is ever saved with its root entry.
        var loaded = new Host();
        int id = loaded.Index.Add("U:", FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
        using var again = new NtfsVolumeIndexer(loaded, "U:", new OneFileVolume());
        again.AttachLoaded(new LoadedRoot { Path = "U:", Kind = RootKind.Ntfs, RootId = id, Ids = [id], Frns = [5] });
        Assert.True(again.Complete);
    }
}

using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

public sealed class IndexDatabaseTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("anything-db-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static string[] Paths(FileIndex index) =>
        Enumerable.Range(0, index.Count).Where(index.IsAlive).Select(index.GetFullPath).OrderBy(p => p, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Save_then_load_round_trips_entries_metadata_and_journal_info()
    {
        var index = new FileIndex();
        int c = index.AddRoot("C:");
        int win = index.Add("Windows", c, FileAttributes.Directory, -1, 100, 50);
        int notepad = index.Add("notepad.exe", win, FileAttributes.Archive, 123_456, 200, 60);
        int dead = index.Add("gone.txt", win, FileAttributes.Archive, 1, 1, 1);
        index.Remove(dead);
        // A child created before its (re-parented) folder: forward parent reference in id order.
        int late = index.Add("late.txt", c, FileAttributes.Normal, 7, 3, 2);
        int folder = index.Add("Moved", c, FileAttributes.Directory, -1, 4, 4);
        index.Rename(late, "late.txt", folder);

        int d = index.AddRoot(@"D:\Data");
        index.Add("ünïcödé ✓.txt", d, FileAttributes.Hidden, 42, 5, 6);

        var idToFrn = new long[index.Count];
        Array.Fill(idToFrn, -1);
        idToFrn[c] = 5; idToFrn[win] = 100; idToFrn[notepad] = 101; idToFrn[late] = 102; idToFrn[folder] = 103;

        string db = Path.Combine(_dir, "sub", "Anything.db");
        IndexDatabase.Save(db, index,
        [
            new DbRoot { Path = "C:", Kind = RootKind.Ntfs, JournalId = 0xABCDEF, NextUsn = 987654321, RootId = c, IdToFrn = idToFrn },
            new DbRoot { Path = @"D:\Data", Kind = RootKind.Folder, RootId = d },
        ]);
        Assert.True(File.Exists(db));
        Assert.False(File.Exists(db + ".tmp"));

        var loadedIndex = new FileIndex();
        var roots = IndexDatabase.Load(db, loadedIndex);
        Assert.NotNull(roots);
        Assert.Equal(Paths(index), Paths(loadedIndex));
        Assert.Equal(index.LiveCount, loadedIndex.LiveCount);

        var ntfs = roots!.Single(r => r.Kind == RootKind.Ntfs);
        Assert.Equal("C:", ntfs.Path);
        Assert.Equal(0xABCDEFUL, ntfs.JournalId);
        Assert.Equal(987654321, ntfs.NextUsn);
        Assert.NotNull(ntfs.Frns);
        int loadedNotepad = ntfs.Ids[Array.IndexOf(ntfs.Frns!, 101L)];
        Assert.Equal("notepad.exe", loadedIndex.Name(loadedNotepad));
        Assert.Equal(123_456, loadedIndex.Size(loadedNotepad));
        Assert.Equal(200, loadedIndex.Modified(loadedNotepad));
        Assert.Equal(60, loadedIndex.Created(loadedNotepad));
        Assert.Equal(FileAttributes.Archive, loadedIndex.Attributes(loadedNotepad));
        Assert.Equal(@"C:\Moved\late.txt", loadedIndex.GetFullPath(ntfs.Ids[Array.IndexOf(ntfs.Frns!, 102L)]));

        var folderRoot = roots!.Single(r => r.Kind == RootKind.Folder);
        Assert.Null(folderRoot.Frns);
        Assert.Equal(@"D:\Data\", loadedIndex.GetFullPath(folderRoot.RootId));
    }

    [Fact]
    public void Corrupt_or_unknown_database_is_ignored()
    {
        string db = Path.Combine(_dir, "bad.db");
        var index = new FileIndex();
        Assert.Null(IndexDatabase.Load(db, index)); // missing

        File.WriteAllBytes(db, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]);
        Assert.Null(IndexDatabase.Load(db, index));

        var good = new FileIndex();
        int r = good.AddRoot(@"E:\x");
        good.Add("a", r, FileAttributes.Normal, 1, 1, 1);
        IndexDatabase.Save(db, good, [new DbRoot { Path = @"E:\x", Kind = RootKind.Folder, RootId = r }]);
        var bytes = File.ReadAllBytes(db);
        File.WriteAllBytes(db, bytes[..(bytes.Length / 2)]); // truncated
        Assert.Null(IndexDatabase.Load(db, index));
        Assert.Equal(0, index.Count);
    }
}

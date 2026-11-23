using System.Buffers.Binary;
using Anything.Core.Content.Indexing;
using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

/// <summary>
/// The store's behaviour around files that aren't its own, failed writes, packs another program holds, torn packs and a
/// Clear that races the store's close.
/// </summary>
public sealed class ThumbStoreSafetyTests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rp-thumbs-");
    string Folder => Path.Combine(_dir.FullName, ThumbStore.FolderName);
    string IndexPath => Path.Combine(Folder, "index.bin");
    string TempPath => Path.Combine(Folder, "index.tmp");
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    // Plenty of free space, so a test never depends on the disk it runs on.
    static readonly FixedPower Roomy = new(new PowerState(false, false, TimeSpan.Zero, false, FreeBytes: 500L << 30, TotalBytes: 1000L << 30));

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static ThumbKey Key(int n) => ThumbKey.Of($@"D:\Pictures\photo {n}.jpg", new ThumbStamp(1000, 5000), 256);

    static byte[] Payload(int n, int length = 100)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(n * 31 + i);
        return bytes;
    }

    ThumbStore Open(ThumbStoreOptions? options = null) => ThumbStore.Open(Folder, options);

    string[] Packs() => Directory.Exists(Folder) ? [.. Directory.GetFiles(Folder, "pack-*.bin").OrderBy(p => p, StringComparer.Ordinal)] : [];

    static void AssertPicture(ThumbStore store, ThumbKey key, byte[] payload)
    {
        Assert.True(store.TryGet(key, out var t));
        Assert.Equal(StoredKind.Jpeg, t.Kind);
        Assert.Equal(payload, t.Payload.ToArray());
    }

    static void PutMany(ThumbStore store, int count, int from = 0)
    {
        for (int i = from; i < from + count; i++) Assert.True(store.Put(Key(i), StoredKind.Jpeg, 256, 192, Payload(i)));
        Assert.True(store.WaitUntilWritten(Wait));
    }

    // ── Files that aren't the store's ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_foreign_index_tmp_that_appears_after_open_is_not_written_over()
    {
        using (var store = Open())
        {
            PutMany(store, 1);
            File.WriteAllText(TempPath, "someone's file");
            store.SaveIndex();
            Assert.Equal("someone's file", File.ReadAllText(TempPath));
            Assert.False(File.Exists(IndexPath));
        }
        // Not at close either.
        Assert.Equal("someone's file", File.ReadAllText(TempPath));
        // Once the file is gone, the store finds its records again by scanning the packs.
        File.Delete(TempPath);
        using var again = Open();
        AssertPicture(again, Key(0), Payload(0));
    }

    [Fact]
    public void A_foreign_index_bin_that_appears_after_open_is_not_replaced()
    {
        using (var store = Open())
        {
            PutMany(store, 1);
            File.WriteAllText(IndexPath, "another program's index");
            store.SaveIndex();
            Assert.Equal("another program's index", File.ReadAllText(IndexPath));
            // The store's own copy doesn't stay behind.
            Assert.False(File.Exists(TempPath));
        }
        Assert.Equal("another program's index", File.ReadAllText(IndexPath));
        Assert.False(File.Exists(TempPath));
    }

    [Fact]
    public void An_index_tmp_that_cannot_be_read_is_not_deleted_at_open()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(TempPath, "another program's file");
        // Held by another program that lets it be deleted but not read: the store can't tell whose it is.
        using (new FileStream(TempPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
        using (var store = Open())
        {
            Assert.True(store.IsEnabled);
            PutMany(store, 1);
            store.SaveIndex();
        }
        Assert.Equal("another program's file", File.ReadAllText(TempPath));
    }

    [Theory]
    [InlineData("index.bin", false)]
    [InlineData("index.tmp", false)]
    [InlineData("index.bin", true)]
    [InlineData("index.tmp", true)]
    public void Clear_leaves_a_foreign_or_unreadable_index_alone(string name, bool blockReads)
    {
        string path = Path.Combine(Folder, name);
        using (var store = Open())
        {
            File.WriteAllText(path, "someone's file");
            using (blockReads ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete) : null)
                store.Clear();
        }
        Assert.Equal("someone's file", File.ReadAllText(path));
    }

    [Fact]
    public void A_crash_left_empty_pack_and_short_index_tmp_are_still_removed()
    {
        using (var store = Open()) PutMany(store, 2);
        // What a crash leaves: a pack made and never written, an index copy cut short.
        File.WriteAllBytes(Path.Combine(Folder, "pack-00000009.bin"), []);
        File.WriteAllText(TempPath, "RPT");
        Assert.Equal(StoreRemoval.Removed, ThumbStore.RemoveFolder(Folder));
        Assert.False(Directory.Exists(Folder));
    }

    // ── Failed writes ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_failed_write_leaves_no_empty_pack()
    {
        using var store = Open(new ThumbStoreOptions { Power = Roomy, FailWrite = () => true });
        for (int i = 0; i < 3; i++)
        {
            Assert.True(store.Put(Key(i), StoredKind.Jpeg, 256, 192, Payload(i)));
            Assert.True(store.PutNone(Key(100 + i)));
            Assert.True(store.WaitUntilWritten(Wait));
        }
        Assert.Equal(0, store.PackCount);
        Assert.Empty(Packs());
        Assert.Equal(0, store.SizeBytes);
        Assert.False(store.TryGet(Key(0), out _));
    }

    [Fact]
    public void A_pack_with_records_is_not_written_to_after_a_failed_write_and_keeps_its_records()
    {
        bool fail = false;
        var options = new ThumbStoreOptions { Power = Roomy, DiskCheckInterval = TimeSpan.FromMilliseconds(50), FailWrite = () => Volatile.Read(ref fail) };
        using (var store = Open(options))
        {
            PutMany(store, 2);
            Volatile.Write(ref fail, true);
            PutMany(store, 1, from: 2);
            Volatile.Write(ref fail, false);
            Thread.Sleep(80);
            PutMany(store, 1, from: 3);
            // The new record went to a new pack, not after the failed write.
            Assert.Equal(2, store.PackCount);
            AssertPicture(store, Key(0), Payload(0));
            AssertPicture(store, Key(1), Payload(1));
            Assert.False(store.TryGet(Key(2), out _));
            AssertPicture(store, Key(3), Payload(3));
        }
        File.Delete(IndexPath);
        using var again = Open();
        Assert.Equal(3, again.Count);
        AssertPicture(again, Key(1), Payload(1));
        AssertPicture(again, Key(3), Payload(3));
    }

    [Fact]
    public void After_a_failed_write_writing_pauses_for_one_disk_check_interval()
    {
        bool fail = false;
        var options = new ThumbStoreOptions { Power = Roomy, DiskCheckInterval = TimeSpan.FromMilliseconds(300), FailWrite = () => Volatile.Read(ref fail) };
        using var store = Open(options);
        PutMany(store, 1);
        Volatile.Write(ref fail, true);
        PutMany(store, 1, from: 1);
        Volatile.Write(ref fail, false);
        // Within the pause: dropped, and no new pack is made.
        PutMany(store, 1, from: 2);
        Assert.False(store.TryGet(Key(2), out _));
        Assert.Equal(1, store.PackCount);
        Thread.Sleep(350);
        // After it: stored again.
        PutMany(store, 1, from: 3);
        AssertPicture(store, Key(3), Payload(3));
        Assert.Equal(2, store.PackCount);
    }

    // ── Clear and a pack another program holds ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_with_a_pack_held_without_delete_sharing_keeps_counting_it_and_deletes_it_later()
    {
        using var store = Open(new ThumbStoreOptions { Power = Roomy });
        PutMany(store, 3);
        string first = Packs().Single();
        long firstBytes = new FileInfo(first).Length;
        var holder = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            store.Clear();
            Assert.True(File.Exists(first));
            // The bytes are still on disk, so the size Settings shows says so (counted once).
            Assert.Equal(firstBytes, store.SizeBytes);
            Assert.False(store.TryGet(Key(0), out _));
            Assert.Equal(0, store.Count);
        }
        finally { holder.Dispose(); }
        // Let go: the writer's next round deletes it.
        PutMany(store, 1, from: 10);
        Assert.False(File.Exists(first));
        Assert.Equal(132, store.SizeBytes);
        AssertPicture(store, Key(10), Payload(10));
    }

    [Fact]
    public void A_pack_still_held_when_cleared_is_gone_after_dispose_once_free()
    {
        string first;
        using (var store = Open(new ThumbStoreOptions { Power = Roomy, FlushInterval = TimeSpan.FromMinutes(5) }))
        {
            PutMany(store, 2);
            first = Packs().Single();
            using (new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                store.Clear();
                Assert.True(File.Exists(first));
                Assert.True(store.SizeBytes > 0);
            }
        }
        Assert.False(File.Exists(first));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_cleared_pack_stays_cleared_across_restarts(bool heldAtReopen)
    {
        var options = new ThumbStoreOptions { Power = Roomy };
        using var store = Open(options);
        PutMany(store, 2);
        string first = Assert.Single(Packs());
        using var holder = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        store.Clear();
        // No final save. The index written by Clear must be enough.
        store.Abandon();
        if (!heldAtReopen) holder.Dispose();
        using (var again = Open(options))
        {
            Assert.True(again.IsEnabled);
            Assert.False(again.TryGet(Key(0), out _));
            Assert.Equal(0, again.PackCount);
            Assert.Equal(heldAtReopen ? 264L : 0L, again.SizeBytes);
            Assert.Equal(heldAtReopen, File.Exists(first));
            again.SaveIndex();
        }
        using (var later = Open(options))
        {
            Assert.False(later.TryGet(Key(0), out _));
            holder.Dispose();
            PutMany(later, 1, from: 10);
            // Even after the old pack is gone, the next pack must have a number above the cleared ones.
            Assert.NotEqual(first, Assert.Single(Packs()));
        }
        using var final = Open(options);
        AssertPicture(final, Key(10), Payload(10));
    }

    [Fact]
    public void Held_packs_count_against_older_packs_but_do_not_evict_the_current_pack()
    {
        using var store = Open(new ThumbStoreOptions { Power = Roomy, PackBytes = 264, CapBytes = 528 });
        // One large record fills the cap. The three small records that follow Clear would fit together without it.
        Assert.True(store.Put(Key(0), StoredKind.Jpeg, 256, 192, Payload(0, 496)));
        Assert.True(store.WaitUntilWritten(Wait));
        string first = Assert.Single(Packs());
        using var holder = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        store.Clear();
        PutMany(store, 1, from: 10);
        AssertPicture(store, Key(10), Payload(10));
        Assert.Equal(528 + 132, store.SizeBytes);
        PutMany(store, 2, from: 11);
        Assert.False(store.TryGet(Key(10), out _));
        AssertPicture(store, Key(12), Payload(12));
        Assert.Equal(1, store.PackCount);
        Assert.Equal(528 + 132, store.SizeBytes);
    }

    // ── A torn pack ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_torn_pack_stays_sealed_after_a_reopen_that_writes_nothing()
    {
        using (var store = Open()) PutMany(store, 3);
        File.Delete(IndexPath);
        // A power cut in the middle of the third record.
        string pack = Packs().Single();
        using (var f = new FileStream(pack, FileMode.Open)) f.SetLength(3 * 132 - 50);
        // A session that scans the pack, saves the index and writes nothing.
        using (var store = Open()) Assert.Equal(2, store.Count);
        Assert.True(File.Exists(IndexPath));
        using (var store = Open())
        {
            Assert.Equal(2, store.Count);
            PutMany(store, 1, from: 3);
            // Never appended after the tear: a new pack.
            Assert.Equal(2, store.PackCount);
        }
        // Found again by a scan without the index.
        File.Delete(IndexPath);
        using var again = Open();
        Assert.Equal(3, again.Count);
        AssertPicture(again, Key(0), Payload(0));
        AssertPicture(again, Key(1), Payload(1));
        AssertPicture(again, Key(3), Payload(3));
    }

    [Fact]
    public void The_index_saved_after_a_torn_pack_covers_only_its_good_records()
    {
        using (var store = Open()) PutMany(store, 3);
        File.Delete(IndexPath);
        string pack = Packs().Single();
        using (var f = new FileStream(pack, FileMode.Open)) f.SetLength(3 * 132 - 50);
        using (var store = Open())
        {
            // The physical bytes still count toward the size and the cap.
            Assert.Equal(3 * 132 - 50, store.SizeBytes);
        }
        // The index's one pack entry: number 1, covered up to the end of the second record, not the file's length.
        var index = File.ReadAllBytes(IndexPath);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(12)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(24)));
        Assert.Equal(2 * 132L, BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(28)));
        using var again = Open();
        Assert.Equal(2, again.Count);
        AssertPicture(again, Key(0), Payload(0));
        AssertPicture(again, Key(1), Payload(1));
        Assert.Equal(3 * 132 - 50, again.SizeBytes);
    }

    // ── Clear against closing ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_after_dispose_touches_nothing_of_the_store_that_took_the_folder()
    {
        var first = Open();
        PutMany(first, 2);
        first.Dispose();
        using (var second = Open())
        {
            Assert.True(second.IsEnabled);
            PutMany(second, 1, from: 5);
            var packs = Packs();
            int count = second.Count;
            // A Clear that arrives late, on the closed store.
            first.Clear();
            Assert.Equal(count, second.Count);
            Assert.Equal(packs, Packs());
            Assert.All(packs, p => Assert.True(File.Exists(p)));
            // The index saved at the first store's close is still there.
            Assert.True(File.Exists(IndexPath));
        }
        using var third = Open();
        Assert.Equal(3, third.Count);
        AssertPicture(third, Key(0), Payload(0));
        AssertPicture(third, Key(5), Payload(5));
    }

    [Fact]
    public void Clear_holds_the_folder_until_its_deletes_are_done()
    {
        ThumbStore? store = null;
        int during = 0;
        var options = new ThumbStoreOptions { BeforeClearDeletes = () => during = store!.MarkerHolders };
        store = Open(options);
        try
        {
            Assert.Equal(2, store.MarkerHolders);
            PutMany(store, 2);
            store.Clear();
            // The writer, Dispose to come, and the Clear itself.
            Assert.Equal(3, during);
            Assert.Equal(2, store.MarkerHolders);
        }
        finally { store.Dispose(); }
        // Closed: the marker went, and another store can have the folder.
        using var again = Open();
        Assert.True(again.IsEnabled);
    }

    sealed class FixedPower(PowerState state) : IPowerPolicy
    {
        public PowerState Now(string contentFolder) => state;
    }
}

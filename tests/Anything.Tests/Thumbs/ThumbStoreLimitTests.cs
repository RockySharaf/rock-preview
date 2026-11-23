using Anything.Core.Content.Indexing;
using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

/// <summary>The limit without a fixed top, the record ceiling, a large index, and the signals for the window.</summary>
public sealed class ThumbStoreLimitTests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rp-thumbs-limit-");
    string Folder => Path.Combine(_dir.FullName, ThumbStore.FolderName);
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

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

    /// <summary>Puts records from .. from+count-1 (100-byte payloads, 132 bytes on disk) and waits until they are written.</summary>
    static void PutMany(ThumbStore store, int count, int from = 0)
    {
        for (int i = from; i < from + count; i++) Assert.True(store.Put(Key(i), StoredKind.Jpeg, 256, 192, Payload(i)));
        Assert.True(store.WaitUntilWritten(Wait));
    }

    static bool SpinUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    [Fact]
    public void A_limit_above_8_GB_is_kept()
    {
        using var store = Open(new ThumbStoreOptions { CapBytes = ThumbStore.CapBytesFor(100_000) });
        Assert.Equal(100_000_000_000L, store.CapBytes);
        store.CapBytes = ThumbStore.CapBytesFor(2_000_000);
        Assert.Equal(2_000_000_000_000L, store.CapBytes);
        PutMany(store, 3);
        Assert.Equal(3, store.Count);
        Assert.False(store.IsFull);
    }

    [Fact]
    public void Past_the_record_ceiling_the_oldest_packs_go()
    {
        // Seven 132-byte records to a 1 KB pack; at most 10 records, whatever the limit in bytes.
        var options = new ThumbStoreOptions { PackBytes = 1024, MaxRecords = 10, CapBytes = 1 << 30 };
        using (var store = Open(options))
        {
            PutMany(store, 30);
            Assert.True(SpinUntil(() => store.Count <= 10));
            Assert.True(store.IsFull);
            // The newest are kept, the oldest went with their packs.
            Assert.True(store.TryGet(Key(29), out _));
            Assert.False(store.TryGet(Key(0), out _));
            Assert.False(store.TryGet(Key(13), out _));
            Assert.Equal(["pack-00000004.bin", "pack-00000005.bin"], Packs().Select(Path.GetFileName));
        }
        // Opened again with a lower ceiling: the oldest pack goes at once, and the store says it is full.
        using var again = Open(new ThumbStoreOptions { PackBytes = 1024, MaxRecords = 5, CapBytes = 1 << 30 });
        Assert.True(again.Count <= 5);
        Assert.True(again.IsFull);
        Assert.True(again.TryGet(Key(29), out _));
    }

    [Fact]
    public void Many_packs_evicted_at_once_cost_one_pass_over_the_records()
    {
        var options = new ThumbStoreOptions { PackBytes = 1024, CapBytes = 1 << 30 };
        using var store = Open(options);
        PutMany(store, 140); // twenty packs of seven
        Assert.Equal(20, store.PackCount);
        int before = store.EvictionPasses;
        // Seventeen packs must go.
        store.CapBytes = 3 * 1024;
        // The packs leave the list first, then their records leave the map in the pass.
        Assert.True(SpinUntil(() => store.PackCount == 3 && store.EvictionPasses > before));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.Equal(before + 1, store.EvictionPasses);
        Assert.Equal(21, store.Count);
        Assert.False(store.TryGet(Key(118), out _));
        Assert.True(store.TryGet(Key(119), out _));
    }

    [Fact]
    public void The_index_is_saved_and_read_back_in_many_parts()
    {
        var options = new ThumbStoreOptions { IndexPartRecords = 7 };
        using (var store = Open(options))
        {
            PutMany(store, 500);
            Assert.True(store.PutNone(Key(1000)));
            Assert.True(store.WaitUntilWritten(Wait));
        }
        using (var again = Open(options))
        {
            Assert.Equal(501, again.IndexEntriesLoaded);
            Assert.Equal(501, again.Count);
            for (int i = 0; i < 500; i++) Assert.True(again.TryGet(Key(i), out var t) && t.Payload.Span.SequenceEqual(Payload(i)));
            Assert.True(again.TryGet(Key(1000), out var none));
            Assert.Equal(StoredKind.None, none.Kind);
        }
    }

    [Fact]
    public void The_index_size_bound_follows_the_record_ceiling_and_is_checked_when_read()
    {
        // The product's bound holds an index of the ceiling and more, over the packs of a 4 TB store.
        long packsOf4TB = ThumbStore.CapBytesFor(ThumbStore.MaxCapMegabytes) / (16 << 20) + 1;
        Assert.True(ThumbStore.MaxIndexBytesFor(ThumbStore.MaxRecords) > ThumbStore.IndexBytes(ThumbStore.MaxRecords + 1_000_000L, packsOf4TB));
        // Above the 256 MB the store refused before, for a higher ceiling.
        Assert.True(ThumbStore.MaxIndexBytesFor(8_000_000) > 256L << 20);

        using (var store = Open()) PutMany(store, 50);
        long size = new FileInfo(Path.Combine(Folder, "index.bin")).Length;
        Assert.Equal(ThumbStore.IndexBytes(50, 1), size);
        // A bound exactly the index's size reads it.
        using (var exact = Open(new ThumbStoreOptions { MaxIndexBytes = size }))
        {
            Assert.Equal(50, exact.IndexEntriesLoaded);
            exact.Abandon();
        }
        // One byte less: the index isn't trusted and the packs are scanned, so nothing is lost.
        using var lower = Open(new ThumbStoreOptions { MaxIndexBytes = size - 1 });
        Assert.Equal(0, lower.IndexEntriesLoaded);
        Assert.Equal(50, lower.Count);
        Assert.True(lower.TryGet(Key(49), out _));
    }

    [Fact]
    public void Records_written_after_the_last_save_are_found_again_after_a_crash()
    {
        var options = new ThumbStoreOptions { IndexSaveInterval = TimeSpan.FromHours(1) };
        using (var store = Open(options))
        {
            PutMany(store, 20);
            store.SaveIndex();
            PutMany(store, 20, from: 20);
            store.Abandon();
        }
        using var again = Open(options);
        Assert.Equal(20, again.IndexEntriesLoaded);
        Assert.Equal(40, again.Count);
        Assert.True(again.TryGet(Key(39), out _));
    }

    [Fact]
    public void A_large_index_is_saved_less_often()
    {
        var minute = TimeSpan.FromMinutes(1);
        Assert.Equal(minute, ThumbStore.IndexSaveIntervalFor(0, minute));
        Assert.Equal(minute, ThumbStore.IndexSaveIntervalFor(ThumbStore.IndexBytesPerMinute, minute));
        Assert.Equal(TimeSpan.FromMinutes(5), ThumbStore.IndexSaveIntervalFor(5 * ThumbStore.IndexBytesPerMinute, minute));
        // The ceiling's index (about 108 MB at 3 million records) is saved every five minutes or so, never more often.
        Assert.True(ThumbStore.IndexSaveIntervalFor(ThumbStore.IndexBytes(ThumbStore.MaxRecords, 2000), minute) > TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Full_is_said_once_when_packs_go_and_ends_when_the_limit_is_raised_or_the_store_cleared()
    {
        int raised = 0;
        var options = new ThumbStoreOptions { PackBytes = 1024, CapBytes = 2048 };
        using (var store = Open(options))
        {
            store.BecameFull += () => Interlocked.Increment(ref raised);
            Assert.False(store.IsFull);
            PutMany(store, 14); // two packs: just at the limit, nothing dropped
            Assert.False(store.IsFull);
            PutMany(store, 14, from: 14);
            Assert.True(SpinUntil(() => Volatile.Read(ref raised) == 1));
            Assert.True(store.IsFull);
            // Still full while more packs go: said once.
            PutMany(store, 21, from: 28);
            Assert.True(store.IsFull);
            Thread.Sleep(50);
            Assert.Equal(1, Volatile.Read(ref raised));

            // Raised: not full, until the new limit is reached as well.
            store.CapBytes = 4096;
            Assert.False(store.IsFull);
            PutMany(store, 14, from: 49);
            Assert.False(store.IsFull);
            PutMany(store, 21, from: 63);
            Assert.True(SpinUntil(() => Volatile.Read(ref raised) == 2));
            Assert.True(store.IsFull);
            // The same limit set again is no more room.
            store.CapBytes = 4096;
            Assert.True(store.IsFull);

            store.Clear();
            Assert.False(store.IsFull);
        }
        // Opened again with room: not full.
        using var again = Open(options);
        Assert.False(again.IsFull);
    }

    [Fact]
    public void A_handler_that_fails_does_not_stop_the_store()
    {
        using var store = Open(new ThumbStoreOptions { PackBytes = 1024, CapBytes = 1024 });
        store.BecameFull += () => throw new InvalidOperationException("a handler's mistake");
        PutMany(store, 20);
        Assert.True(store.IsFull);
        PutMany(store, 5, from: 20);
        Assert.True(store.TryGet(Key(24), out _));
    }

    [Fact]
    public void Disk_low_is_said_while_pictures_are_refused_and_ends_with_the_next_picture_written()
    {
        var power = new SwitchedPower { FreeBytes = 1L << 30 };
        using var store = Open(new ThumbStoreOptions { Power = power, DiskCheckInterval = TimeSpan.Zero });
        Assert.False(store.DiskLow);
        // Records without a picture don't say it: nothing was refused.
        Assert.True(store.PutNone(Key(0)));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.False(store.DiskLow);
        Assert.True(store.Put(Key(1), StoredKind.Jpeg, 256, 192, Payload(1)));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.True(store.DiskLow);
        Assert.True(store.PutNone(Key(2)));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.True(store.DiskLow);

        power.FreeBytes = 400L << 30;
        Assert.True(store.Put(Key(3), StoredKind.Jpeg, 256, 192, Payload(3)));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.False(store.DiskLow);
        Assert.True(store.TryGet(Key(3), out _));
    }

    [Fact]
    public void A_store_that_stores_nothing_is_never_full_or_low()
    {
        using var store = ThumbStore.Disabled();
        int raised = 0;
        store.BecameFull += () => raised++;
        store.CapBytes = 1;
        Assert.False(store.Put(Key(1), StoredKind.Jpeg, 256, 192, Payload(1)));
        store.Clear();
        Assert.False(store.IsFull);
        Assert.False(store.DiskLow);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Measuring_a_folder_notices_a_file_replaced_under_the_same_name()
    {
        using (var store = Open()) PutMany(store, 2);
        string pack = Assert.Single(Packs());
        long all = ThumbStore.MeasureFolder(Folder);
        Assert.True(all >= 2 * 132);
        // Measured again unchanged: the same.
        Assert.Equal(all, ThumbStore.MeasureFolder(Folder));
        // The user's own file now has the pack's name: it is no longer counted.
        File.Delete(pack);
        var theirs = new byte[500];
        theirs[0] = (byte)'X';
        File.WriteAllBytes(pack, theirs);
        Assert.Equal(all - 264, ThumbStore.MeasureFolder(Folder));
    }

    sealed class SwitchedPower : IPowerPolicy
    {
        public long FreeBytes;
        public PowerState Now(string contentFolder)
            => new(false, false, TimeSpan.Zero, false, FreeBytes: Interlocked.Read(ref FreeBytes), TotalBytes: 500L << 30);
    }
}

using Anything.Core.Content.Indexing;
using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

public sealed class ThumbStoreTests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rp-thumbs-");
    string Folder => Path.Combine(_dir.FullName, ThumbStore.FolderName);
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static ThumbKey Key(int n, int sizeClass = 256, long length = 1000, long modified = 5000)
        => ThumbKey.Of($@"D:\Pictures\photo {n}.jpg", new ThumbStamp(length, modified), sizeClass);

    static byte[] Payload(int n, int length = 100)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(n * 31 + i);
        return bytes;
    }

    ThumbStore Open(ThumbStoreOptions? options = null) => ThumbStore.Open(Folder, options);

    string[] Packs() => Directory.Exists(Folder) ? [.. Directory.GetFiles(Folder, "pack-*.bin").OrderBy(p => p, StringComparer.Ordinal)] : [];

    static void AssertPicture(ThumbStore store, ThumbKey key, byte[] payload, int width = 256, int height = 192)
    {
        Assert.True(store.TryGet(key, out var t));
        Assert.Equal(StoredKind.Jpeg, t.Kind);
        Assert.Equal((width, height), (t.Width, t.Height));
        Assert.Equal(payload, t.Payload.ToArray());
    }

    /// <summary>Puts records 0 .. count-1 (100-byte payloads) and waits until they are written.</summary>
    static void PutMany(ThumbStore store, int count, int from = 0)
    {
        for (int i = from; i < from + count; i++) Assert.True(store.Put(Key(i), StoredKind.Jpeg, 256, 192, Payload(i)));
        Assert.True(store.WaitUntilWritten(Wait));
    }

    [Fact]
    public void A_picture_put_is_read_back_at_once_after_writing_and_after_reopening()
    {
        var key = Key(1);
        var payload = Payload(1);
        using (var store = Open())
        {
            Assert.True(store.IsEnabled);
            Assert.True(store.Put(key, StoredKind.Jpeg, 256, 192, payload));
            AssertPicture(store, key, payload);
            Assert.True(store.WaitUntilWritten(Wait));
            AssertPicture(store, key, payload);
            Assert.Equal(1, store.Count);
            Assert.Equal(132, store.SizeBytes);
        }
        Assert.True(File.Exists(Path.Combine(Folder, "index.bin")));
        using var again = Open();
        AssertPicture(again, key, payload);
        Assert.Equal(132, again.SizeBytes);
    }

    [Fact]
    public void A_png_keeps_its_kind()
    {
        var key = Key(1);
        using (var store = Open())
        {
            Assert.True(store.Put(key, StoredKind.Png, 10, 20, Payload(1)));
        }
        using var again = Open();
        Assert.True(again.TryGet(key, out var t));
        Assert.Equal((StoredKind.Png, 10, 20), (t.Kind, t.Width, t.Height));
        Assert.True(t.HasPicture);
    }

    [Fact]
    public void Reopening_without_the_index_scans_the_packs()
    {
        using (var store = Open()) PutMany(store, 10);
        File.Delete(Path.Combine(Folder, "index.bin"));
        using var again = Open();
        Assert.Equal(10, again.Count);
        for (int i = 0; i < 10; i++) AssertPicture(again, Key(i), Payload(i));
    }

    [Fact]
    public void A_stale_index_is_completed_by_scanning_what_it_does_not_cover()
    {
        using (var store = Open())
        {
            PutMany(store, 5);
            store.SaveIndex();
            PutMany(store, 5, from: 5);
            // The program dies: the index on disk knows only the first five.
            store.Abandon();
        }
        using var again = Open();
        Assert.Equal(10, again.Count);
        for (int i = 0; i < 10; i++) AssertPicture(again, Key(i), Payload(i));
    }

    [Fact]
    public void A_damaged_index_is_ignored_and_the_packs_are_scanned()
    {
        using (var store = Open()) PutMany(store, 6);
        string index = Path.Combine(Folder, "index.bin");
        var bytes = File.ReadAllBytes(index);
        bytes[bytes.Length / 2] ^= 0x5A;
        File.WriteAllBytes(index, bytes);
        using var again = Open();
        Assert.Equal(6, again.Count);
        for (int i = 0; i < 6; i++) AssertPicture(again, Key(i), Payload(i));
    }

    [Theory]
    [InlineData("index.bin")]
    [InlineData("index.tmp")]
    public void A_zero_filled_index_does_not_prevent_recovery(string name)
    {
        using (var store = Open(new ThumbStoreOptions { Power = NoPowerPolicy.Instance })) PutMany(store, 1);
        File.WriteAllBytes(Path.Combine(Folder, name), new byte[128]);
        using (var again = Open())
        {
            Assert.True(again.IsEnabled);
            AssertPicture(again, Key(0), Payload(0));
            Assert.False(File.Exists(Path.Combine(Folder, "index.tmp")));
        }
        Assert.True(File.ReadAllBytes(Path.Combine(Folder, "index.bin")).AsSpan().StartsWith("RPTHIDX1"u8));
    }

    [Fact]
    public void A_zero_filled_marker_is_reinitialized_and_tracks_new_work()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, "inflight.bin"), new byte[4096]);
        using (var again = Open())
        {
            Assert.True(again.IsEnabled);
            Assert.Empty(again.Marker.Live());
            again.Marker.Enter(again.Marker.Acquire(), Key(1));
            again.Abandon();
        }
        using var next = Open();
        Assert.True(next.IsSuspect(Key(1)));
    }

    [Fact]
    public void A_zero_filled_pack_is_counted_and_sealed_even_with_a_matching_index()
    {
        using (var store = Open()) Assert.True(store.PutNone(Key(0)));
        string pack = Assert.Single(Packs());
        var zeros = new byte[new FileInfo(pack).Length];
        File.WriteAllBytes(pack, zeros);

        using var again = Open();
        Assert.Equal(zeros.LongLength, again.SizeBytes);
        Assert.False(again.TryGet(Key(0), out _));
        Assert.True(again.PutNone(Key(1)));
        Assert.True(again.WaitUntilWritten(Wait));
        Assert.Equal(2, again.PackCount);
        Assert.Equal(zeros, File.ReadAllBytes(pack));
        again.Clear();
        Assert.Empty(Packs());
    }

    [Fact]
    public void An_index_that_names_a_shorter_pack_is_not_trusted_for_it()
    {
        using (var store = Open()) PutMany(store, 4);
        // The pack is cut after the second record, behind the index's back.
        using (var f = new FileStream(Packs().Single(), FileMode.Open)) f.SetLength(2 * 132);
        using var again = Open();
        Assert.Equal(2, again.Count);
        AssertPicture(again, Key(0), Payload(0));
        AssertPicture(again, Key(1), Payload(1));
        Assert.False(again.TryGet(Key(2), out _));
    }

    [Fact]
    public void A_damaged_record_is_a_miss_and_leaves_the_index()
    {
        using (var store = Open()) PutMany(store, 3);
        FlipByte(Packs().Single(), 132 + 32 + 10);
        using var again = Open();
        Assert.Equal(3, again.Count);
        AssertPicture(again, Key(0), Payload(0));
        Assert.False(again.TryGet(Key(1), out _));
        Assert.Equal(2, again.Count);
        AssertPicture(again, Key(2), Payload(2));
    }

    [Fact]
    public void A_scan_stops_at_a_damaged_record()
    {
        using (var store = Open()) PutMany(store, 3);
        File.Delete(Path.Combine(Folder, "index.bin"));
        FlipByte(Packs().Single(), 132 + 32 + 10);
        using var again = Open();
        AssertPicture(again, Key(0), Payload(0));
        Assert.False(again.TryGet(Key(1), out _));
        Assert.False(again.TryGet(Key(2), out _));
        Assert.Equal(1, again.Count);
    }

    [Fact]
    public void A_cut_off_pack_keeps_its_whole_records_and_new_ones_go_to_a_new_pack()
    {
        using (var store = Open()) PutMany(store, 3);
        File.Delete(Path.Combine(Folder, "index.bin"));
        using (var f = new FileStream(Packs().Single(), FileMode.Open)) f.SetLength(3 * 132 - 50);
        using (var again = Open())
        {
            AssertPicture(again, Key(0), Payload(0));
            AssertPicture(again, Key(1), Payload(1));
            Assert.False(again.TryGet(Key(2), out _));
            PutMany(again, 1, from: 3);
            Assert.Equal(2, again.PackCount);
        }
        File.Delete(Path.Combine(Folder, "index.bin"));
        using var third = Open();
        Assert.Equal(3, third.Count);
        AssertPicture(third, Key(3), Payload(3));
    }

    [Fact]
    public void Over_the_cap_the_oldest_packs_are_deleted()
    {
        // 232-byte records, four to a 1 KB pack; a 2 KB cap keeps two packs.
        var options = new ThumbStoreOptions { PackBytes = 1024, CapBytes = 2048 };
        using var store = Open(options);
        for (int i = 0; i < 20; i++) Assert.True(store.Put(Key(i), StoredKind.Jpeg, 256, 192, Payload(i, 200)));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.True(store.SizeBytes <= 2048);
        Assert.Equal(2, store.PackCount);
        for (int i = 0; i < 12; i++) Assert.False(store.TryGet(Key(i), out _));
        for (int i = 12; i < 20; i++) Assert.True(store.TryGet(Key(i), out _));
        Assert.Equal(["pack-00000004.bin", "pack-00000005.bin"], Packs().Select(Path.GetFileName));
    }

    [Fact]
    public void Lowering_the_cap_deletes_the_oldest_packs()
    {
        var options = new ThumbStoreOptions { PackBytes = 1024, CapBytes = 1 << 20 };
        using var store = Open(options);
        for (int i = 0; i < 20; i++) Assert.True(store.Put(Key(i), StoredKind.Jpeg, 256, 192, Payload(i, 200)));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.Equal(5, store.PackCount);
        store.CapBytes = 1024;
        Assert.True(SpinUntil(() => store.PackCount == 1));
        Assert.True(store.TryGet(Key(19), out _));
        Assert.False(store.TryGet(Key(15), out _));
    }

    [Fact]
    public void Another_stamp_or_size_class_is_another_picture()
    {
        using var store = Open();
        var key = Key(1);
        Assert.True(store.Put(key, StoredKind.Jpeg, 256, 192, Payload(1)));
        Assert.True(store.Put(Key(1, sizeClass: 512), StoredKind.Jpeg, 512, 384, Payload(2)));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.False(store.TryGet(Key(1, length: 1001), out _));
        Assert.False(store.TryGet(Key(1, modified: 5001), out _));
        AssertPicture(store, key, Payload(1));
        AssertPicture(store, Key(1, sizeClass: 512), Payload(2), 512, 384);
        // The same path in another case is the same picture.
        Assert.True(store.TryGet(ThumbKey.Of(@"d:\pictures\PHOTO 1.JPG", new ThumbStamp(1000, 5000), 256), out _));
    }

    [Fact]
    public void A_second_hash_that_does_not_match_is_a_miss()
    {
        using var store = Open();
        var key = Key(1);
        Assert.True(store.Put(key, StoredKind.Jpeg, 256, 192, Payload(1)));
        // Queued.
        Assert.False(store.TryGet(key.Hash64, key.Check32 ^ 1, out _));
        Assert.True(store.WaitUntilWritten(Wait));
        // Written.
        Assert.False(store.TryGet(key.Hash64, key.Check32 ^ 1, out _));
        // The record belongs to another key, not damaged: it stays for its own.
        AssertPicture(store, key, Payload(1));
    }

    [Fact]
    public void None_and_Crashed_records_say_so_and_survive_reopening()
    {
        using (var store = Open())
        {
            Assert.True(store.PutNone(Key(1)));
            Assert.True(store.PutCrashed(Key(2)));
            Assert.True(store.TryGet(Key(1), out var none));
            Assert.Equal(StoredKind.None, none.Kind);
            Assert.False(none.HasPicture);
            Assert.True(store.WaitUntilWritten(Wait));
        }
        File.Delete(Path.Combine(Folder, "index.bin"));
        using var again = Open();
        Assert.True(again.TryGet(Key(1), out var n));
        Assert.Equal((StoredKind.None, 0), (n.Kind, n.Payload.Length));
        Assert.True(again.TryGet(Key(2), out var c));
        Assert.Equal(StoredKind.Crashed, c.Kind);
    }

    [Fact]
    public void The_last_record_for_a_key_wins()
    {
        using (var store = Open())
        {
            Assert.True(store.PutNone(Key(1)));
            Assert.True(store.WaitUntilWritten(Wait));
            Assert.True(store.Put(Key(1), StoredKind.Jpeg, 256, 192, Payload(1)));
            Assert.True(store.Put(Key(1), StoredKind.Jpeg, 256, 192, Payload(9)));
            AssertPicture(store, Key(1), Payload(9));
        }
        File.Delete(Path.Combine(Folder, "index.bin"));
        using var again = Open();
        AssertPicture(again, Key(1), Payload(9));
    }

    [Fact]
    public void Puts_and_reads_from_several_threads_at_once()
    {
        using var store = Open();
        const int threads = 8, each = 150;
        var failures = 0;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            var random = new Random(t);
            for (int i = 0; i < each; i++)
            {
                int n = t * each + i;
                if (!store.Put(Key(n), StoredKind.Jpeg, 256, 192, Payload(n))) Interlocked.Increment(ref failures);
                // Read back something this thread put already: it must be there, queued or written.
                int back = t * each + random.Next(i + 1);
                if (!store.TryGet(Key(back), out var got) || !got.Payload.Span.SequenceEqual(Payload(back))) Interlocked.Increment(ref failures);
            }
        });
        Assert.Equal(0, failures);
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.Equal(threads * each, store.Count);
        for (int n = 0; n < threads * each; n++) AssertPicture(store, Key(n), Payload(n));
    }

    [Fact]
    public void Nothing_is_written_while_the_disk_is_low()
    {
        var low = new FixedPower(new PowerState(false, false, TimeSpan.Zero, false, FreeBytes: 1L << 30, TotalBytes: 500L << 30));
        using var store = Open(new ThumbStoreOptions { Power = low });
        Assert.True(store.Put(Key(1), StoredKind.Jpeg, 256, 192, Payload(1)));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.False(store.TryGet(Key(1), out _));
        Assert.Equal(0, store.SizeBytes);
        Assert.Empty(Packs());
    }

    [Fact]
    public void Records_without_a_picture_are_still_written_while_the_disk_is_low()
    {
        // A file that killed the program must be remembered even on a full disk, or it is tried again at every start.
        var low = new FixedPower(new PowerState(false, false, TimeSpan.Zero, false, FreeBytes: 1L << 30, TotalBytes: 500L << 30));
        using var store = Open(new ThumbStoreOptions { Power = low });
        Assert.True(store.PutCrashed(Key(1)));
        Assert.True(store.PutNone(Key(2)));
        Assert.True(store.Put(Key(3), StoredKind.Jpeg, 256, 192, Payload(3)));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.True(store.TryGet(Key(1), out var crashed));
        Assert.Equal(StoredKind.Crashed, crashed.Kind);
        Assert.True(store.TryGet(Key(2), out var none));
        Assert.Equal(StoredKind.None, none.Kind);
        Assert.False(store.TryGet(Key(3), out _));
        Assert.Equal(64, store.SizeBytes);
    }

    [Fact]
    public void A_pack_the_cap_could_not_delete_still_counts_until_it_goes()
    {
        var options = new ThumbStoreOptions { PackBytes = 1024, CapBytes = 2048 };
        using var store = Open(options);
        PutMany(store, 7); // seven 132-byte records fill one pack of 1 KB and start a second
        string first = Packs()[0];
        long firstBytes = new FileInfo(first).Length;
        // Something else (a backup) reads the oldest pack without letting it be deleted.
        using (var holder = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            PutMany(store, 12, from: 7);
            store.CapBytes = 1100;
            Assert.True(store.WaitUntilWritten(Wait));

            Assert.True(store.WaitUntilWritten(Wait));
            Assert.True(File.Exists(first));
            // Its space is still taken, so the size says so.
            Assert.True(store.SizeBytes >= firstBytes, $"size {store.SizeBytes}, undeleted pack {firstBytes}");
        }
        // Let go: the next round of the writer deletes it.
        Assert.True(store.Put(Key(100), StoredKind.Jpeg, 256, 192, Payload(100)));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.False(File.Exists(first));
        Assert.True(store.SizeBytes <= 1100 + 1024);
    }

    [Fact]
    public void Under_5_percent_free_counts_as_low_only_below_10_GB()
    {
        // 8 GB of 200 GB: 4% and under 10 GB, so low.
        var low = new FixedPower(new PowerState(false, false, TimeSpan.Zero, false, FreeBytes: 8L << 30, TotalBytes: 200L << 30));
        using (var store = Open(new ThumbStoreOptions { Power = low }))
        {
            Assert.True(store.Put(Key(1), StoredKind.Jpeg, 256, 192, Payload(1)));
            Assert.True(store.WaitUntilWritten(Wait));
            Assert.Empty(Packs());
        }
        // 40 GB of 1000 GB: 4% too, but a large drive with room to spare.
        var roomy = new FixedPower(new PowerState(false, false, TimeSpan.Zero, false, FreeBytes: 40L << 30, TotalBytes: 1000L << 30));
        using var again = Open(new ThumbStoreOptions { Power = roomy });
        Assert.True(again.Put(Key(2), StoredKind.Jpeg, 256, 192, Payload(2)));
        Assert.True(again.WaitUntilWritten(Wait));
        Assert.NotEmpty(Packs());
    }

    [Fact]
    public void Clear_empties_the_store_and_it_stores_again_after()
    {
        using (var store = Open())
        {
            PutMany(store, 5);
            store.SaveIndex();
            store.Clear();
            Assert.Equal((0, 0L), (store.Count, store.SizeBytes));
            Assert.False(store.TryGet(Key(0), out _));
            Assert.Empty(Packs());
            var clearedIndex = File.ReadAllBytes(Path.Combine(Folder, "index.bin"));
            Assert.Equal(2, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(clearedIndex.AsSpan(20)));
            PutMany(store, 1, from: 7);
        }
        using var again = Open();
        Assert.Equal(1, again.Count);
        AssertPicture(again, Key(7), Payload(7));
        Assert.False(again.TryGet(Key(0), out _));
    }

    [Fact]
    public void A_key_left_in_flight_by_two_crashes_becomes_a_Crashed_record()
    {
        var key = Key(1);
        for (int strike = 0; strike < 2; strike++)
        {
            using var store = Open();
            if (strike == 1)
            {
                // After the first strike the file is only a suspect: it is tried again.
                Assert.Equal(0, store.CrashedAtOpen);
                Assert.False(store.TryGet(key, out _));
            }
            int slot = store.Marker.Acquire();
            store.Marker.Enter(slot, key);
            // The decoder takes the process down here.
            store.Abandon();
        }
        using (var again = Open())
        {
            Assert.Equal(1, again.CrashedAtOpen);
            Assert.True(again.TryGet(key, out var t));
            Assert.Equal(StoredKind.Crashed, t.Kind);
            Assert.Empty(again.Marker.Live());
            // The same file once changed is another key, and is tried again.
            Assert.False(again.TryGet(key.WithStamp(new ThumbStamp(2000, 6000)), out _));
        }
        using var third = Open();
        Assert.Equal(0, third.CrashedAtOpen);
        Assert.True(third.TryGet(key, out var still));
        Assert.Equal(StoredKind.Crashed, still.Kind);
    }

    [Fact]
    public void One_end_in_flight_is_forgiven_once_the_picture_is_made()
    {
        var key = Key(1);
        using (var store = Open())
        {
            int slot = store.Marker.Acquire();
            store.Marker.Enter(slot, key);
            // Ended from outside (a logoff, Task Manager) while this file was being made.
            store.Abandon();
        }
        using (var again = Open())
        {
            Assert.Equal(0, again.CrashedAtOpen);
            Assert.False(again.TryGet(key, out _));
            // Tried again, and made this time: the picture replaces the suspicion.
            Assert.True(again.Put(key, StoredKind.Jpeg, 256, 192, Payload(1)));
            Assert.True(again.WaitUntilWritten(TimeSpan.FromSeconds(10)));
        }
        using var third = Open();
        AssertPicture(third, key, Payload(1));
    }

    [Fact]
    public void ClearSuspect_wakes_the_writer_and_saves_before_its_usual_interval()
    {
        using (var store = Open())
        {
            store.Marker.Enter(store.Marker.Acquire(), Key(1));
            store.Abandon();
        }
        var interval = TimeSpan.FromMinutes(10);
        using (var store = Open(new ThumbStoreOptions { FlushInterval = interval, IndexSaveInterval = interval }))
        {
            store.SaveIndex();
            string index = Path.Combine(Folder, "index.bin");
            long before = new FileInfo(index).Length;
            Assert.True(store.ClearSuspect(Key(1)));
            // Only ClearSuspect can wake the idle writer. A smaller index means the removal reached disk.
            Assert.True(SpinWait.SpinUntil(() => new FileInfo(index).Length < before, TimeSpan.FromSeconds(1)));
            store.Abandon();
        }
        using var next = Open();
        Assert.False(next.IsSuspect(Key(1)));
    }

    [Fact]
    public void A_record_forgotten_is_a_miss_and_stays_one()
    {
        using (var store = Open())
        {
            PutMany(store, 2);
            Assert.True(store.WaitUntilWritten(TimeSpan.FromSeconds(10)));
            store.Forget(Key(0));
            Assert.False(store.TryGet(Key(0), out _));
            AssertPicture(store, Key(1), Payload(1));
        }
        using var again = Open();
        Assert.False(again.TryGet(Key(0), out _));
        AssertPicture(again, Key(1), Payload(1));
    }

    [Fact]
    public void A_folder_without_the_stores_files_is_not_removed()
    {
        // An empty folder of the user's that happens to be where a store would be.
        Directory.CreateDirectory(Folder);
        Assert.Equal(StoreRemoval.NotThere, ThumbStore.RemoveFolder(Folder));
        Assert.True(Directory.Exists(Folder));
    }

    [Fact]
    public void Work_that_finished_before_a_crash_blames_nothing()
    {
        using (var store = Open())
        {
            int slot = store.Marker.Acquire();
            uint epoch = store.Marker.Enter(slot, Key(1));
            store.Marker.Leave(slot, epoch);
            store.Abandon();
        }
        using var again = Open();
        Assert.Equal(0, again.CrashedAtOpen);
        Assert.False(again.TryGet(Key(1), out _));
    }

    [Fact]
    public void A_second_store_on_the_same_folder_stores_nothing()
    {
        using var first = Open();
        using var second = Open();
        Assert.True(first.IsEnabled);
        Assert.False(second.IsEnabled);
        Assert.Equal(ThumbStoreProblem.InUse, second.Problem);
        Assert.False(second.Put(Key(1), StoredKind.Jpeg, 256, 192, Payload(1)));
        Assert.False(second.TryGet(Key(1), out _));
    }

    [Fact]
    public void A_folder_that_cannot_be_made_gives_a_store_that_stores_nothing()
    {
        // A file where the folder should be.
        string blocked = Path.Combine(_dir.FullName, "blocked");
        File.WriteAllText(blocked, "x");
        using var store = ThumbStore.Open(Path.Combine(blocked, "Thumbs"));
        Assert.False(store.IsEnabled);
        Assert.Equal(ThumbStoreProblem.FolderUnusable, store.Problem);
        Assert.Equal(Path.Combine(blocked, "Thumbs"), store.Folder);
        Assert.False(store.PutNone(Key(1)));
        store.Clear();
        Assert.Equal(0, store.SizeBytes);
    }

    [Fact]
    public void Bad_arguments_are_not_stored()
    {
        using var store = Open();
        Assert.False(store.Put(Key(1), StoredKind.None, 256, 192, Payload(1)));
        Assert.False(store.Put(Key(1), StoredKind.Jpeg, 0, 192, Payload(1)));
        Assert.False(store.Put(Key(1), StoredKind.Jpeg, 256, 70000, Payload(1)));
        Assert.False(store.Put(Key(1), StoredKind.Png, 256, 192, []));
        Assert.False(store.Put(Key(1), StoredKind.Png, 256, 192, new byte[ThumbStore.MaxPayloadBytes + 1]));
        Assert.False(store.TryGet(Key(1), out _));
    }

    [Fact]
    public void A_closed_store_refuses_and_misses()
    {
        var store = Open();
        PutMany(store, 1);
        store.Dispose();
        Assert.False(store.Put(Key(2), StoredKind.Jpeg, 256, 192, Payload(2)));
        Assert.False(store.TryGet(Key(0), out _));
        store.Dispose();
    }

    [Fact]
    public void The_folder_is_beside_the_database()
    {
        Assert.Equal(@"X:\data\Rock Preview\Thumbs", ThumbStore.FolderFor(@"X:\data\Rock Preview\index.db"));
    }

    [Fact]
    public void The_cap_setting_is_clamped_and_counted_in_decimal_megabytes()
    {
        Assert.Equal(100_000_000L, ThumbStore.CapBytesFor(1));
        Assert.Equal(500_000_000L, ThumbStore.CapBytesFor(500));
        // No top at 8 GB any more: only the 4 TB sanity bound.
        Assert.Equal(100_000_000_000L, ThumbStore.CapBytesFor(100_000));
        Assert.Equal(4_000_000_000_000L, ThumbStore.CapBytesFor(int.MaxValue));
    }

    [Fact]
    public void A_folder_holding_only_the_store_is_removed()
    {
        using (var store = Open())
        {
            PutMany(store, 2);
            store.SaveIndex();
        }
        Assert.True(ThumbStore.HoldsStore(Folder));
        Assert.True(ThumbStore.MeasureFolder(Folder) >= 2 * 132);
        Assert.Equal(StoreRemoval.Removed, ThumbStore.RemoveFolder(Folder));
        Assert.False(Directory.Exists(Folder));
        Assert.Equal(StoreRemoval.NotThere, ThumbStore.RemoveFolder(Folder));
        Assert.Equal(0, ThumbStore.MeasureFolder(Folder));
    }

    [Fact]
    public void A_folder_with_a_foreign_file_keeps_it_and_loses_only_the_stores_files()
    {
        using (var store = Open()) PutMany(store, 2);
        string mine = Path.Combine(Folder, "notes.txt");
        File.WriteAllText(mine, "keep me");
        var before = Directory.GetFiles(Folder).Order(StringComparer.Ordinal).ToArray();
        // The foreign file doesn't count toward the store's size.
        Assert.Equal(ThumbStore.MeasureFolder(Folder), before.Where(p => !p.EndsWith("notes.txt")).Sum(p => new FileInfo(p).Length));
        Assert.Equal(StoreRemoval.LeftAlone, ThumbStore.RemoveFolder(Folder));
        // The folder stays with the user's file in it; the saved thumbnails are gone, so turning the option off frees them.
        Assert.Equal([mine], Directory.GetFiles(Folder));
        Assert.Equal("keep me", File.ReadAllText(mine));
        Assert.Equal(0, ThumbStore.MeasureFolder(Folder));
        // Safe to repeat.
        Assert.Equal(StoreRemoval.NotThere, ThumbStore.RemoveFolder(Folder));
    }

    [Fact]
    public void A_folder_with_a_foreign_subfolder_is_left_alone_and_says_so()
    {
        using (var store = Open()) PutMany(store, 1);
        // Even an empty folder, and even one named like a pack: it isn't the store's.
        Directory.CreateDirectory(Path.Combine(Folder, "pack-00000009.bin"));
        Assert.Equal(StoreRemoval.LeftAlone, ThumbStore.RemoveFolder(Folder));
        Assert.True(Directory.Exists(Path.Combine(Folder, "pack-00000009.bin")));
        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Fact]
    public void A_users_file_named_like_a_pack_is_never_counted_or_deleted_by_the_store()
    {
        // The user's own file, bigger than the limit, in a folder that already has the store's name.
        Directory.CreateDirectory(Folder);
        string theirs = Path.Combine(Folder, "pack-00000001.bin");
        var bytes = new byte[3000];
        new Random(7).NextBytes(bytes);
        bytes[0] = (byte)'X';
        File.WriteAllBytes(theirs, bytes);
        var options = new ThumbStoreOptions { PackBytes = 1024, CapBytes = 2048 };
        using (var store = Open(options))
        {
            Assert.True(store.IsEnabled);
            Assert.Equal(0, store.SizeBytes);
            // Filling past the limit drops the store's own oldest packs, never the user's file.
            PutMany(store, 30);
            Assert.True(store.WaitUntilWritten(Wait));
            Assert.True(File.Exists(theirs));
            store.Clear();
            Assert.True(File.Exists(theirs));
            Assert.Equal(0, store.SizeBytes);
        }
        // What the store's own files take (its marker and index), never the user's file.
        Assert.Equal(Directory.GetFiles(Folder).Where(p => p != theirs).Sum(p => new FileInfo(p).Length), ThumbStore.MeasureFolder(Folder));
        Assert.Equal(StoreRemoval.LeftAlone, ThumbStore.RemoveFolder(Folder));
        Assert.Equal(bytes, File.ReadAllBytes(theirs));
        Assert.Equal(0, ThumbStore.MeasureFolder(Folder));
        // Opened again, it still isn't the store's.
        using (var again = Open(options))
        {
            Assert.Equal(0, again.SizeBytes);
            PutMany(again, 1);
            Assert.True(again.WaitUntilWritten(Wait));
        }
        Assert.Equal(bytes, File.ReadAllBytes(theirs));
    }

    [Fact]
    public void A_file_of_the_users_named_like_the_stores_is_never_deleted()
    {
        foreach (var name in new[] { "index.bin", "index.tmp", "inflight.bin", "pack-00000001.bin" })
        {
            Directory.CreateDirectory(Folder);
            string theirs = Path.Combine(Folder, name);
            File.WriteAllText(theirs, "the user's own file");
            Assert.False(ThumbStore.HoldsStore(Folder), name);
            Assert.NotEqual(StoreRemoval.Removed, ThumbStore.RemoveFolder(Folder));
            Assert.Equal("the user's own file", File.ReadAllText(theirs));
            File.Delete(theirs);
        }
    }

    [Fact]
    public void A_folder_whose_plain_named_file_is_someone_elses_is_not_used()
    {
        Directory.CreateDirectory(Folder);
        string theirs = Path.Combine(Folder, "index.bin");
        File.WriteAllText(theirs, "another program's index");
        using (var store = Open())
        {
            Assert.False(store.IsEnabled);
            Assert.Equal(ThumbStoreProblem.FolderUnusable, store.Problem);
        }
        Assert.Equal("another program's index", File.ReadAllText(theirs));
        Assert.False(File.Exists(Path.Combine(Folder, "inflight.bin")));
    }

    [Fact]
    public void A_location_whose_folder_is_not_there_is_not_built()
    {
        // The chosen place (say F:\Photos on a stick that has another letter now) isn't there: nothing is made.
        string chosen = Path.Combine(_dir.FullName, "gone", "Photos");
        using (var store = ThumbStore.Open(ThumbStore.FolderIn(chosen)))
        {
            Assert.False(store.IsEnabled);
            Assert.Equal(ThumbStoreProblem.FolderUnusable, store.Problem);
        }
        Assert.False(Directory.Exists(Path.Combine(_dir.FullName, "gone")));
    }

    [Fact]
    public void An_open_store_is_not_removed()
    {
        using var store = Open();
        PutMany(store, 1);
        Assert.Equal(StoreRemoval.Failed, ThumbStore.RemoveFolder(Folder));
        Assert.True(Directory.Exists(Folder));
    }

    [Fact]
    public void A_folder_reached_another_way_resolves_to_the_same_place()
    {
        string real = Path.Combine(_dir.FullName, "real");
        Directory.CreateDirectory(real);
        string resolved = ThumbStore.FinalPath(real);
        Assert.Equal(resolved, ThumbStore.FinalPath(real + @"\."), ignoreCase: true);
        // A folder not made yet is resolved through the one above it.
        Assert.Equal(Path.Combine(resolved, ThumbStore.ChosenFolderName), ThumbStore.FinalPath(Path.Combine(real, ThumbStore.ChosenFolderName)), ignoreCase: true);
        // Through a junction (made without rights by cmd's mklink; skipped where that isn't possible).
        string junction = Path.Combine(_dir.FullName, "alias");
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{real}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!)
            p.WaitForExit(10_000);
        if (Directory.Exists(junction))
        {
            Assert.NotEqual(junction, real, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(resolved, ThumbStore.FinalPath(junction), ignoreCase: true);
            Assert.Equal(Path.Combine(resolved, ThumbStore.ChosenFolderName), ThumbStore.FinalPath(Path.Combine(junction, ThumbStore.ChosenFolderName)), ignoreCase: true);
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void Store_files_are_known_by_name()
    {
        Assert.True(ThumbStore.IsStoreFileName("pack-00000001.bin"));
        Assert.True(ThumbStore.IsStoreFileName("PACK-12345678.BIN"));
        Assert.True(ThumbStore.IsStoreFileName("index.bin"));
        Assert.True(ThumbStore.IsStoreFileName("index.tmp"));
        Assert.True(ThumbStore.IsStoreFileName("inflight.bin"));
        Assert.False(ThumbStore.IsStoreFileName("pack-1.bin"));
        Assert.False(ThumbStore.IsStoreFileName("pack-00000000.bin"));
        Assert.False(ThumbStore.IsStoreFileName("desktop.ini"));
        Assert.False(ThumbStore.IsStoreFileName("index.bin.bak"));
    }

    [Fact]
    public void A_chosen_location_gets_a_folder_of_its_own()
    {
        Assert.Equal(@"Y:\Space\Rock Preview Thumbnails", ThumbStore.FolderIn(@"Y:\Space"));
        Assert.Equal(@"Y:\Rock Preview Thumbnails", ThumbStore.FolderIn(@"Y:\"));
        string chosen = Path.Combine(_dir.FullName, "chosen");
        Directory.CreateDirectory(chosen);
        File.WriteAllText(Path.Combine(chosen, "the users file.txt"), "x");
        using (var store = ThumbStore.Open(ThumbStore.FolderIn(chosen)))
        {
            Assert.True(store.IsEnabled);
            Assert.Equal(ThumbStoreProblem.None, store.Problem);
            Assert.True(store.Put(Key(1), StoredKind.Jpeg, 256, 192, Payload(1)));
        }
        string folder = Path.Combine(chosen, ThumbStore.ChosenFolderName);
        Assert.True(ThumbStore.HoldsStore(folder));
        Assert.Equal(StoreRemoval.Removed, ThumbStore.RemoveFolder(folder));
        // The place the user chose and their file in it stay.
        Assert.True(File.Exists(Path.Combine(chosen, "the users file.txt")));
    }

    [Fact]
    public void The_default_limit_is_within_the_bounds_and_counted_in_decimal_megabytes()
    {
        Assert.All(new[] { 250, 500, 1000, 8000 }, mb => Assert.Equal(mb * 1_000_000L, ThumbStore.CapBytesFor(mb)));
        Assert.Equal((100, 500, 4_000_000), (ThumbStore.MinCapMegabytes, ThumbStore.DefaultCapMegabytes, ThumbStore.MaxCapMegabytes));
    }

    [Fact]
    public void A_disabled_store_does_nothing()
    {
        int before = Directory.GetFileSystemEntries(_dir.FullName).Length;
        using var store = ThumbStore.Disabled();
        Assert.False(store.IsEnabled);
        Assert.Equal(ThumbStoreProblem.Off, store.Problem);
        Assert.Equal("", store.Folder);
        // The setting off writes nothing anywhere, not even the in-flight marker.
        int slot = store.Marker.Acquire();
        store.Marker.Leave(slot, store.Marker.Enter(slot, Key(1)));
        store.Clear();
        Assert.Equal(before, Directory.GetFileSystemEntries(_dir.FullName).Length);
        Assert.False(store.Put(Key(1), StoredKind.Jpeg, 256, 192, Payload(1)));
        Assert.False(store.TryGet(Key(1), out _));
        Assert.False(store.Marker.HasFile);
    }

    static void FlipByte(string path, long offset)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        f.Position = offset;
        int b = f.ReadByte();
        f.Position = offset;
        f.WriteByte((byte)(b ^ 0xFF));
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

    sealed class FixedPower(PowerState state) : IPowerPolicy
    {
        public PowerState Now(string contentFolder) => state;
    }
}

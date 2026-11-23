using System.Buffers.Binary;
using System.Diagnostics;
using Anything.Core.Content.Indexing;
using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

/// <summary>
/// How the store closes (the writer does the final work, so Dispose never waits on a slow disk) and how suspects are
/// asked about and let go.
/// </summary>
public sealed class ThumbStoreCloseTests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rp-thumbs-");
    string Folder => Path.Combine(_dir.FullName, ThumbStore.FolderName);
    string IndexPath => Path.Combine(Folder, "index.bin");
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

    ThumbStore Open(ThumbStoreOptions? options = null) => ThumbStore.Open(Folder, options ?? new ThumbStoreOptions { Power = Roomy });

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

    /// <summary>A crash of the process while <paramref name="keys"/> are being made.</summary>
    static void EndWhileMaking(ThumbStore store, params ThumbKey[] keys)
    {
        foreach (var key in keys) store.Marker.Enter(store.Marker.Acquire(), key);
        store.Abandon();
    }

    // ── Closing ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_has_the_writer_save_an_index_that_covers_the_packs()
    {
        using (var store = Open()) PutMany(store, 3);
        Assert.True(File.Exists(IndexPath));
        // The index's one pack entry says the whole pack is covered, so the next open reads it instead of scanning.
        var index = File.ReadAllBytes(IndexPath);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(12)));
        Assert.Equal(new FileInfo(Packs().Single()).Length, BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(24 + 4)));
        using var again = Open();
        Assert.Equal(3, again.Count);
        for (int i = 0; i < 3; i++) AssertPicture(again, Key(i), Payload(i));
    }

    [Fact]
    public void Dispose_lets_go_of_the_folder_before_it_returns_when_the_disk_answers()
    {
        var first = Open();
        PutMany(first, 1);
        first.Dispose();
        // Turning the option off and on again, or a test reopening at once: the folder is free.
        using var again = Open();
        Assert.True(again.IsEnabled);
        AssertPicture(again, Key(0), Payload(0));
    }

    [Fact]
    public void A_clean_close_blames_nothing_that_was_being_made()
    {
        using (var store = Open())
        {
            store.Marker.Enter(store.Marker.Acquire(), Key(1));
        }
        using var again = Open();
        Assert.Equal(0, again.CrashedAtOpen);
        Assert.False(again.IsSuspect(Key(1)));
        Assert.Empty(again.Marker.Live());
    }

    [Fact]
    public void Abandon_saves_no_index_and_still_leaves_what_was_in_flight()
    {
        using (var store = Open())
        {
            PutMany(store, 2);
            EndWhileMaking(store, Key(9));
        }
        Assert.False(File.Exists(IndexPath));
        using var again = Open();
        // Found again by scanning the packs.
        AssertPicture(again, Key(0), Payload(0));
        AssertPicture(again, Key(1), Payload(1));
        Assert.True(again.IsSuspect(Key(9)));
    }

    [Fact]
    public void Dispose_quiets_the_marker_and_WaitUntilClosed_tracks_the_folder_hold()
    {
        using var release = new ManualResetEventSlim(false);
        using var reached = new ManualResetEventSlim(false);
        var store = Open(new ThumbStoreOptions
        {
            Power = Roomy,
            CloseWait = TimeSpan.FromMilliseconds(50),
            // A network drive that stops answering just as the program exits.
            BeforeFinalSave = () =>
            {
                reached.Set();
                release.Wait(Wait);
            },
        });
        PutMany(store, 2);
        int slot = store.Marker.Acquire();
        store.Marker.Enter(slot, Key(3));
        var watch = Stopwatch.StartNew();
        store.Dispose();
        watch.Stop();
        try
        {
            // Generous for a busy PC, and still far below the 30 seconds the stuck save would hold a Dispose that waited.
            Assert.True(watch.ElapsedMilliseconds < 10_000, $"Dispose took {watch.ElapsedMilliseconds} ms");
            Assert.True(reached.Wait(Wait));
            // Even a worker starting another decode during the last save cannot leave a strike behind.
            Assert.Empty(store.Marker.Live());
            store.Marker.Enter(slot, Key(4));
            Assert.Empty(store.Marker.Live());
            Assert.False(store.WaitUntilClosed(TimeSpan.Zero));
            using var meanwhile = Open();
            Assert.False(meanwhile.IsEnabled);
            Assert.Equal(ThumbStoreProblem.InUse, meanwhile.Problem);
            Assert.False(File.Exists(IndexPath));
        }
        finally { release.Set(); }
        Assert.True(store.WaitUntilClosed(Wait));
        // The writer finished the close by itself: the index is saved and the folder is free.
        Assert.True(File.Exists(IndexPath));
        using var again = Open();
        Assert.True(again.IsEnabled);
        Assert.Equal(2, again.Count);
        AssertPicture(again, Key(1), Payload(1));
    }

    [Fact]
    public void WaitUntilClosed_is_public_for_the_service()
    {
        // The tests can see internal methods too, while the service uses the public API.
        Assert.NotNull(typeof(ThumbStore).GetMethod(nameof(ThumbStore.WaitUntilClosed), [typeof(TimeSpan)]));
        using var off = ThumbStore.Disabled();
        Assert.True(off.WaitUntilClosed(TimeSpan.Zero));
    }

    [Fact]
    public void A_second_Dispose_or_an_Abandon_after_Dispose_does_nothing()
    {
        var store = Open();
        PutMany(store, 1);
        store.Dispose();
        store.Dispose();
        store.Abandon();
        using var again = Open();
        AssertPicture(again, Key(0), Payload(0));
    }

    // ── Suspects ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_key_in_flight_once_is_a_suspect_until_its_picture_is_put()
    {
        using (var store = Open())
        {
            Assert.False(store.IsSuspect(Key(1)));
            EndWhileMaking(store, Key(1));
        }
        using (var again = Open())
        {
            Assert.True(again.IsSuspect(Key(1)));
            Assert.False(again.TryGet(Key(1), out _));
            // Another stamp of the same file is another key.
            Assert.False(again.IsSuspect(ThumbKey.Of(@"D:\Pictures\photo 1.jpg", new ThumbStamp(2000, 6000), 256)));
            Assert.True(again.Put(Key(1), StoredKind.Jpeg, 256, 192, Payload(1)));
            // Queued, the picture is already the newest word on the key.
            Assert.False(again.IsSuspect(Key(1)));
            Assert.True(again.WaitUntilWritten(Wait));
            Assert.False(again.IsSuspect(Key(1)));
        }
        using var third = Open();
        Assert.False(third.IsSuspect(Key(1)));
        AssertPicture(third, Key(1), Payload(1));
    }

    [Fact]
    public void ClearSuspect_lets_only_a_suspect_go_and_for_good()
    {
        using (var store = Open())
        {
            PutMany(store, 1, from: 2);
            Assert.True(store.PutNone(Key(3)));
            Assert.True(store.PutCrashed(Key(4)));
            Assert.True(store.WaitUntilWritten(Wait));
            EndWhileMaking(store, Key(1));
        }
        using (var again = Open())
        {
            Assert.True(again.IsSuspect(Key(1)));
            Assert.True(again.ClearSuspect(Key(1)));
            Assert.False(again.IsSuspect(Key(1)));
            Assert.False(again.ClearSuspect(Key(1)));
            // A picture, a None and a Crashed record stay as they are; a key with no record is no suspect either.
            Assert.False(again.ClearSuspect(Key(2)));
            Assert.False(again.ClearSuspect(Key(3)));
            Assert.False(again.ClearSuspect(Key(4)));
            Assert.False(again.ClearSuspect(Key(5)));
            AssertPicture(again, Key(2), Payload(2));
            Assert.True(again.TryGet(Key(3), out var none));
            Assert.Equal(StoredKind.None, none.Kind);
            Assert.True(again.TryGet(Key(4), out var crashed));
            Assert.Equal(StoredKind.Crashed, crashed.Kind);
        }
        using (var third = Open())
        {
            // Let go for good: the saved index no longer names it.
            Assert.False(third.IsSuspect(Key(1)));
            // So being in flight again is only a first strike, not a crash.
            EndWhileMaking(third, Key(1));
        }
        using var fourth = Open();
        Assert.Equal(0, fourth.CrashedAtOpen);
        Assert.True(fourth.IsSuspect(Key(1)));
        Assert.False(fourth.TryGet(Key(1), out _));
    }

    [Fact]
    public void Of_two_keys_in_flight_once_only_the_one_in_flight_again_becomes_Crashed()
    {
        using (var store = Open()) EndWhileMaking(store, Key(1), Key(2));
        using (var again = Open())
        {
            Assert.Equal(0, again.CrashedAtOpen);
            Assert.True(again.IsSuspect(Key(1)));
            Assert.True(again.IsSuspect(Key(2)));
            // Retried one at a time: only key 1 is being made when the program dies again.
            EndWhileMaking(again, Key(1));
        }
        using var third = Open();
        Assert.Equal(1, third.CrashedAtOpen);
        Assert.True(third.TryGet(Key(1), out var t));
        Assert.Equal(StoredKind.Crashed, t.Kind);
        Assert.False(third.IsSuspect(Key(1)));
        // Key 2 is still only a suspect: a miss, tried again.
        Assert.False(third.TryGet(Key(2), out _));
        Assert.True(third.IsSuspect(Key(2)));
    }

    [Fact]
    public void A_partial_crash_record_write_keeps_the_slots_and_retries_the_same_strikes()
    {
        using (var first = Open()) EndWhileMaking(first, Key(2));
        using (var second = Open())
        {
            Assert.True(second.IsSuspect(Key(2)));
            EndWhileMaking(second, Key(1), Key(2));
        }

        int writes = 0;
        using (var partial = Open(new ThumbStoreOptions { Power = Roomy, PackBytes = 32, FailWrite = () => ++writes == 2 }))
        {
            // Key 1's first strike landed. Key 2 still has its old first strike because its second strike failed.
            Assert.Equal(2, writes);
            Assert.True(partial.IsSuspect(Key(1)));
            Assert.True(partial.IsSuspect(Key(2)));
            partial.Marker.Enter(partial.Marker.Acquire(), Key(3));
            var expected = new[] { Key(1), Key(2), Key(3) }.Select(key => new InFlightEntry(key.Hash64, key.Check32));
            Assert.Equal(expected, partial.Marker.Live());
        }

        using var retry = Open();
        Assert.True(retry.IsSuspect(Key(1)));
        Assert.True(retry.TryGet(Key(2), out var crashed));
        Assert.Equal(StoredKind.Crashed, crashed.Kind);
        Assert.False(retry.IsSuspect(Key(3)));
        Assert.Empty(retry.Marker.Live());
    }

    [Fact]
    public void A_disabled_or_closed_store_has_no_suspects()
    {
        using var off = ThumbStore.Disabled();
        Assert.False(off.IsSuspect(Key(1)));
        Assert.False(off.ClearSuspect(Key(1)));
        using (var store = Open()) EndWhileMaking(store, Key(1));
        var again = Open();
        Assert.True(again.IsSuspect(Key(1)));
        again.Dispose();
        Assert.False(again.IsSuspect(Key(1)));
        Assert.False(again.ClearSuspect(Key(1)));
    }

    sealed class FixedPower(PowerState state) : IPowerPolicy
    {
        public PowerState Now(string path) => state;
    }
}

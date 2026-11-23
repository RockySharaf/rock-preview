using System.Buffers.Binary;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;
using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

/// <summary>
/// The store keeps pictures, videos and documents in packs of their own: documents go first when it is full, then
/// videos, then the oldest pictures; a kind switched off is deleted; a store written by 1.7.30 or 1.7.31 opens and goes
/// first; and 1.7.31 still reads a store written now.
/// </summary>
public sealed class ThumbStoreGroupTests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rp-thumbs-groups-");
    string Folder => Path.Combine(_dir.FullName, ThumbStore.FolderName);
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static ThumbKey Key(ThumbKeepGroup group, int n) => ThumbKey.Of(group switch
    {
        ThumbKeepGroup.Videos => $@"D:\Videos\clip {n}.mp4",
        ThumbKeepGroup.Documents => $@"D:\Documents\report {n}.pdf",
        _ => $@"D:\Pictures\photo {n}.jpg",
    }, new ThumbStamp(1000, 5000), 256);

    static byte[] Payload(int n, int length = 100)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(n * 31 + i + 1);
        return bytes;
    }

    ThumbStore Open(ThumbStoreOptions? options = null) => ThumbStore.Open(Folder, options);

    // Seven 132-byte records fill a 1 KB pack.
    static ThumbStoreOptions Small(long cap = 1 << 30, int maxRecords = ThumbStore.MaxRecords) => new() { PackBytes = 1024, CapBytes = cap, MaxRecords = maxRecords };

    static void PutMany(ThumbStore store, ThumbKeepGroup group, int count, int from = 0)
    {
        for (int i = from; i < from + count; i++) Assert.True(store.Put(Key(group, i), StoredKind.Jpeg, 256, 192, Payload(i), group));
        Assert.True(store.WaitUntilWritten(Wait));
    }

    static int Found(ThumbStore store, ThumbKeepGroup group, int count, int from = 0)
        => Enumerable.Range(from, count).Count(i => store.TryGet(Key(group, i), out _));

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
    public void Documents_go_first_then_videos_then_the_oldest_pictures_open_packs_included()
    {
        using var store = Open(Small());
        // The pictures are the oldest, the documents the newest: age doesn't save a document.
        PutMany(store, ThumbKeepGroup.Pictures, 14);
        PutMany(store, ThumbKeepGroup.Videos, 14);
        PutMany(store, ThumbKeepGroup.Documents, 14);
        Assert.Equal(6, store.PackCount);
        Assert.Equal(new ThumbStoreSplit(2 * 924, 2 * 924, 2 * 924, 0), store.Split);

        // Room for four packs: both document packs go, the one still open too.
        store.CapBytes = 4096;
        Assert.True(SpinUntil(() => store.PackCount == 4));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.Equal(0, Found(store, ThumbKeepGroup.Documents, 14));
        Assert.Equal(14, Found(store, ThumbKeepGroup.Videos, 14));
        Assert.Equal(14, Found(store, ThumbKeepGroup.Pictures, 14));
        Assert.True(store.IsFull);

        // Room for two: the videos go, not a picture.
        store.CapBytes = 2048;
        Assert.True(SpinUntil(() => store.PackCount == 2));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.Equal(0, Found(store, ThumbKeepGroup.Videos, 14));
        Assert.Equal(14, Found(store, ThumbKeepGroup.Pictures, 14));

        // Room for one: the oldest pictures go, the newest stay.
        store.CapBytes = 1024;
        Assert.True(SpinUntil(() => store.PackCount == 1));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.Equal(0, Found(store, ThumbKeepGroup.Pictures, 7));
        Assert.Equal(7, Found(store, ThumbKeepGroup.Pictures, 7, from: 7));
        Assert.Equal(new ThumbStoreSplit(924, 0, 0, 0), store.Split);

        // A group whose open pack went starts a new one.
        store.CapBytes = 1 << 30;
        PutMany(store, ThumbKeepGroup.Documents, 1, from: 100);
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Documents, 100), out _));
        Assert.Equal(2, store.PackCount);
    }

    [Fact]
    public void A_store_holding_only_pictures_drops_its_oldest_as_before()
    {
        using var store = Open(Small(cap: 2048));
        PutMany(store, ThumbKeepGroup.Pictures, 30);
        Assert.True(SpinUntil(() => store.SizeBytes <= 2048));
        Assert.True(store.IsFull);
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Pictures, 29), out _));
        Assert.False(store.TryGet(Key(ThumbKeepGroup.Pictures, 0), out _));
        // The pack being written stays: only older ones went.
        Assert.Equal(store.PackGroups.Select(p => p.Group), Enumerable.Repeat(ThumbStore.PackGroup.Pictures, store.PackCount));
    }

    [Fact]
    public void A_full_store_of_pictures_doesnt_write_documents_or_videos_it_would_drop_first()
    {
        using var store = Open(Small(cap: 2048));
        PutMany(store, ThumbKeepGroup.Pictures, 30);
        Assert.True(SpinUntil(() => store.SizeBytes <= 2048));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.True(store.IsFull);
        int passes = store.EvictionPasses;

        // Written, each would go at the next eviction, a pass over the whole map for a few records: none is written.
        for (int i = 0; i < 20; i++)
        {
            Assert.True(store.Put(Key(ThumbKeepGroup.Documents, i), StoredKind.Jpeg, 256, 192, Payload(i), ThumbKeepGroup.Documents));
            Assert.True(store.Put(Key(ThumbKeepGroup.Videos, i), StoredKind.Jpeg, 256, 192, Payload(i), ThumbKeepGroup.Videos));
        }
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.Equal(40, store.RefusedWhileFull);
        Assert.Equal(passes, store.EvictionPasses);
        Assert.Equal(0, Found(store, ThumbKeepGroup.Documents, 20));
        Assert.Equal(0, Found(store, ThumbKeepGroup.Videos, 20));
        Assert.DoesNotContain(store.PackGroups, p => p.Group is ThumbStore.PackGroup.Documents or ThumbStore.PackGroup.Videos);

        // Pictures still are, and the oldest pictures make room; a "crashed" record of any kind is always kept.
        PutMany(store, ThumbKeepGroup.Pictures, 7, from: 100);
        Assert.Equal(7, Found(store, ThumbKeepGroup.Pictures, 7, from: 100));
        Assert.True(store.PutCrashed(Key(ThumbKeepGroup.Documents, 99)));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Documents, 99), out var crashed));
        Assert.Equal(StoredKind.Crashed, crashed.Kind);

        // A raised limit takes documents again.
        store.CapBytes = 1 << 30;
        PutMany(store, ThumbKeepGroup.Documents, 1, from: 200);
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Documents, 200), out _));
    }

    static readonly ThumbKeepGroup[] AllGroups = [ThumbKeepGroup.None, ThumbKeepGroup.Pictures, ThumbKeepGroup.Videos, ThumbKeepGroup.Documents];

    static ThumbKeepGroup[] Refused(ThumbStore store) => [.. AllGroups.Where(store.Refuses)];

    [Fact]
    public void Refuses_says_which_kinds_the_full_store_would_throw_away()
    {
        // A store that stores nothing refuses nothing (Put says no for it).
        Assert.Empty(Refused(ThumbStore.Disabled()));

        using var store = Open(Small(cap: 3 * 1024));
        // Not full: nothing refused.
        PutMany(store, ThumbKeepGroup.Pictures, 14);
        Assert.False(store.IsFull);
        Assert.Empty(Refused(store));

        // Full with documents held: a document or a video would not be the first thing dropped.
        PutMany(store, ThumbKeepGroup.Documents, 14);
        Assert.True(SpinUntil(() => store.PackCount == 3));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.True(store.IsFull);
        Assert.Empty(Refused(store));

        // Full with only pictures: documents and videos are refused, pictures are not.
        store.CapBytes = 2 * 1024;
        Assert.True(SpinUntil(() => store.Split.Documents == 0));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.True(store.IsFull);
        Assert.Equal([ThumbKeepGroup.Videos, ThumbKeepGroup.Documents], Refused(store));
        // The answer is the writer's: a document handed in now is thrown away.
        int before = store.RefusedWhileFull;
        Assert.True(store.Put(Key(ThumbKeepGroup.Documents, 300), StoredKind.Jpeg, 256, 192, Payload(300), ThumbKeepGroup.Documents));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.Equal(before + 1, store.RefusedWhileFull);

        // A raised limit: nothing refused.
        store.CapBytes = 1 << 30;
        Assert.Empty(Refused(store));

        // Full of pictures again, then cleared: nothing refused.
        store.CapBytes = 2 * 1024;
        PutMany(store, ThumbKeepGroup.Pictures, 14, from: 400);
        Assert.True(SpinUntil(() => store.IsFull));
        Assert.Equal([ThumbKeepGroup.Videos, ThumbKeepGroup.Documents], Refused(store));
        store.Clear();
        Assert.True(SpinUntil(() => !store.IsFull));
        Assert.Empty(Refused(store));
    }

    [Fact]
    public void A_full_store_still_writes_a_kind_it_holds_and_kinds_ranked_above_it()
    {
        using var store = Open(Small(cap: 3 * 1024));
        PutMany(store, ThumbKeepGroup.Pictures, 14);
        PutMany(store, ThumbKeepGroup.Documents, 14);
        // Four packs for room for three: the oldest documents went.
        Assert.True(SpinUntil(() => store.PackCount == 3));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.True(store.IsFull);
        // Documents are still held, so neither a document nor a video would be the first thing dropped.
        PutMany(store, ThumbKeepGroup.Documents, 1, from: 50);
        PutMany(store, ThumbKeepGroup.Videos, 1);
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Documents, 50), out _));
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Videos, 0), out _));
        Assert.Equal(0, store.RefusedWhileFull);
        Assert.Equal(14, Found(store, ThumbKeepGroup.Pictures, 14));
    }

    [Fact]
    public void Records_without_a_picture_have_packs_of_their_own_kept_longest()
    {
        using var store = Open(Small());
        PutMany(store, ThumbKeepGroup.Documents, 7);
        Assert.True(store.PutCrashed(Key(ThumbKeepGroup.Videos, 1)));
        Assert.True(store.PutNone(Key(ThumbKeepGroup.Documents, 50)));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.Equal([ThumbStore.PackGroup.Documents, ThumbStore.PackGroup.NoPicture], store.PackGroups.Select(p => p.Group));
        // Over the limit: the documents go, the "crashed" record stays (it keeps a file that kills the program from being tried again).
        store.CapBytes = 100;
        Assert.True(SpinUntil(() => store.PackCount == 1));
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Videos, 1), out var crashed));
        Assert.Equal(StoredKind.Crashed, crashed.Kind);
        // A group switched off never takes these with it.
        Assert.Equal(0, store.DropGroup(ThumbKeepGroup.Videos));
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Videos, 1), out _));
    }

    [Fact]
    public void Records_without_a_picture_outlast_older_pictures_and_new_pictures_are_still_saved()
    {
        using var store = Open(Small());
        // The marks come first, so their pack has the lowest number: age doesn't make them go before a picture.
        Assert.True(store.PutCrashed(Key(ThumbKeepGroup.Videos, 1)));
        Assert.True(store.PutNone(Key(ThumbKeepGroup.Documents, 50)));
        Assert.True(store.WaitUntilWritten(Wait));
        PutMany(store, ThumbKeepGroup.Pictures, 7);
        // Room for the marks and two packs of pictures: the oldest pictures go, every mark stays.
        store.CapBytes = 2048;
        PutMany(store, ThumbKeepGroup.Pictures, 14, from: 7);
        Assert.True(SpinUntil(() => store.SizeBytes <= 2048));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.True(store.IsFull);
        Assert.Equal(0, Found(store, ThumbKeepGroup.Pictures, 7));
        Assert.Equal(14, Found(store, ThumbKeepGroup.Pictures, 14, from: 7));
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Videos, 1), out var crashed));
        Assert.Equal(StoredKind.Crashed, crashed.Kind);
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Documents, 50), out var none));
        Assert.Equal(StoredKind.None, none.Kind);
        Assert.Contains(store.PackGroups, p => p.Group == ThumbStore.PackGroup.NoPicture);

        // New pictures are still saved; documents and videos, which would go first, are not.
        Assert.Equal([ThumbKeepGroup.Videos, ThumbKeepGroup.Documents], Refused(store));
        PutMany(store, ThumbKeepGroup.Pictures, 7, from: 100);
        Assert.Equal(7, Found(store, ThumbKeepGroup.Pictures, 7, from: 100));
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Videos, 1), out _));
    }

    [Fact]
    public void A_full_store_holding_only_records_without_a_picture_still_takes_pictures()
    {
        using var store = Open(Small());
        Assert.True(store.PutCrashed(Key(ThumbKeepGroup.Videos, 1)));
        Assert.True(store.PutNone(Key(ThumbKeepGroup.Documents, 50)));
        Assert.True(store.WaitUntilWritten(Wait));
        PutMany(store, ThumbKeepGroup.Pictures, 7);
        // Room for the marks alone: the pictures go, the marks stay, and the store is full holding only them.
        store.CapBytes = 100;
        Assert.True(SpinUntil(() => store.PackCount == 1));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.True(store.IsFull);
        Assert.Equal([ThumbStore.PackGroup.NoPicture], store.PackGroups.Select(p => p.Group));
        // The marks count as pictures held: a picture is still taken, documents and videos are refused.
        Assert.Equal([ThumbKeepGroup.Videos, ThumbKeepGroup.Documents], Refused(store));
        int refused = store.RefusedWhileFull;
        Assert.True(store.Put(Key(ThumbKeepGroup.Pictures, 50), StoredKind.Jpeg, 256, 192, Payload(50), ThumbKeepGroup.Pictures));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.Equal(refused, store.RefusedWhileFull);
        // Over the limit again, the new picture went before the marks.
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Videos, 1), out _));
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Documents, 50), out _));

        // With room again, pictures are kept beside the marks.
        store.CapBytes = 1 << 30;
        PutMany(store, ThumbKeepGroup.Pictures, 1, from: 60);
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Pictures, 60), out _));
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Videos, 1), out _));
    }

    [Fact]
    public void Past_the_record_ceiling_documents_go_before_pictures()
    {
        using var store = Open(Small(maxRecords: 10));
        PutMany(store, ThumbKeepGroup.Pictures, 7);
        PutMany(store, ThumbKeepGroup.Documents, 14);
        Assert.True(SpinUntil(() => store.Count <= 10));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.True(store.IsFull);
        Assert.Equal(7, Found(store, ThumbKeepGroup.Pictures, 7));
        // Once only pictures are left past the ceiling, the oldest of them go.
        PutMany(store, ThumbKeepGroup.Pictures, 14, from: 7);
        Assert.True(SpinUntil(() => store.Count <= 10));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.Equal(0, Found(store, ThumbKeepGroup.Documents, 14));
        Assert.Equal(0, Found(store, ThumbKeepGroup.Pictures, 7));
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Pictures, 20), out _));
    }

    [Fact]
    public void Groups_survive_the_index_and_each_goes_on_with_its_own_pack()
    {
        var options = Small();
        using (var store = Open(options))
        {
            PutMany(store, ThumbKeepGroup.Pictures, 10);
            PutMany(store, ThumbKeepGroup.Videos, 3);
            PutMany(store, ThumbKeepGroup.Documents, 9);
        }
        using var again = Open(options);
        Assert.Equal(22, again.IndexEntriesLoaded);
        Assert.Equal(22, again.Count);
        Assert.Equal([ThumbStore.PackGroup.Pictures, ThumbStore.PackGroup.Pictures, ThumbStore.PackGroup.Videos,
            ThumbStore.PackGroup.Documents, ThumbStore.PackGroup.Documents], again.PackGroups.Select(p => p.Group));
        Assert.Equal(new ThumbStoreSplit(10 * 132, 3 * 132, 9 * 132, 0), again.Split);
        // Each group appends to its newest pack while it has room: no new pack for one more video.
        PutMany(again, ThumbKeepGroup.Videos, 1, from: 3);
        Assert.Equal(5, again.PackCount);
        // And the order holds after reopening: documents first.
        again.CapBytes = 2 * 1024;
        Assert.True(SpinUntil(() => again.PackCount == 3));
        Assert.True(again.WaitUntilWritten(Wait));
        Assert.Equal(0, Found(again, ThumbKeepGroup.Documents, 9));
        Assert.Equal(4, Found(again, ThumbKeepGroup.Videos, 4));
        Assert.Equal(10, Found(again, ThumbKeepGroup.Pictures, 10));
    }

    [Fact]
    public void Switching_a_kind_off_deletes_it_while_thumbnails_are_read_and_written()
    {
        using var store = Open(Small());
        PutMany(store, ThumbKeepGroup.Pictures, 70);
        PutMany(store, ThumbKeepGroup.Documents, 140);
        var documentPacks = store.PackGroups.Where(p => p.Group == ThumbStore.PackGroup.Documents).Select(p => p.Number).ToList();
        Assert.Equal(20, documentPacks.Count);

        using var stop = new CancellationTokenSource();
        Exception? failed = null;
        int written = 0, pictureMisses = 0;
        var reader = new Thread(() =>
        {
            try
            {
                for (int round = 0; !stop.IsCancellationRequested && round < 1_000_000; round++)
                {
                    if (!store.TryGet(Key(ThumbKeepGroup.Pictures, round % 70), out _)) Interlocked.Increment(ref pictureMisses);
                    store.TryGet(Key(ThumbKeepGroup.Documents, round % 140), out _);
                }
            }
            catch (Exception e) { failed = e; }
        });
        var writer = new Thread(() =>
        {
            try
            {
                for (int i = 0; !stop.IsCancellationRequested && i < 2000; i++)
                {
                    var group = i % 2 == 0 ? ThumbKeepGroup.Pictures : ThumbKeepGroup.Videos;
                    if (store.Put(Key(group, 1000 + i), StoredKind.Jpeg, 256, 192, Payload(i), group)) Interlocked.Increment(ref written);
                }
            }
            catch (Exception e) { failed = e; }
        });
        reader.Start();
        writer.Start();
        Assert.Equal(20, store.DropGroup(ThumbKeepGroup.Documents));
        stop.Cancel();
        Assert.True(reader.Join(Wait) && writer.Join(Wait));
        Assert.Null(failed);
        Assert.True(store.WaitUntilWritten(Wait));

        Assert.Equal(0, pictureMisses);
        Assert.Equal(0, Found(store, ThumbKeepGroup.Documents, 140));
        Assert.Equal(70, Found(store, ThumbKeepGroup.Pictures, 70));
        Assert.Equal(70 + Volatile.Read(ref written), store.Count);
        Assert.Equal(0, store.Split.Documents);
        Assert.DoesNotContain(store.PackGroups, p => p.Group == ThumbStore.PackGroup.Documents);
        foreach (int number in documentPacks) Assert.False(File.Exists(Path.Combine(Folder, $"pack-{number:D8}.bin")));
        // Nothing left to do the second time, and a group that is no box does nothing.
        Assert.Equal(0, store.DropGroup(ThumbKeepGroup.Documents));
        Assert.Equal(0, store.DropGroup(ThumbKeepGroup.None));
        // Documents saved from now on go to a new pack and are kept.
        PutMany(store, ThumbKeepGroup.Documents, 1, from: 500);
        Assert.True(store.TryGet(Key(ThumbKeepGroup.Documents, 500), out _));
    }

    [Fact]
    public void Switching_a_kind_off_makes_room_and_the_store_is_not_full_any_more()
    {
        using var store = Open(Small(cap: 3 * 1024));
        PutMany(store, ThumbKeepGroup.Pictures, 7);
        PutMany(store, ThumbKeepGroup.Documents, 21);
        Assert.True(SpinUntil(() => store.IsFull));
        Assert.True(store.DropGroup(ThumbKeepGroup.Documents) > 0);
        Assert.False(store.IsFull);
        Assert.Equal(7, Found(store, ThumbKeepGroup.Pictures, 7));
        // Nothing in the group: the store stays as it is.
        Assert.Equal(0, store.DropGroup(ThumbKeepGroup.Videos));
        // Reopened, the documents stay gone (their packs are deleted).
        store.Dispose();
        Assert.True(store.WaitUntilClosed(Wait));
        using var again = Open(Small(cap: 3 * 1024));
        Assert.Equal(7, again.Count);
        Assert.Equal(0, Found(again, ThumbKeepGroup.Documents, 21));
    }

    [Fact]
    public void Queued_thumbnails_of_a_kind_switched_off_are_not_written()
    {
        using var store = Open(Small());
        PutMany(store, ThumbKeepGroup.Documents, 3);
        // Puts the writer may not have taken yet.
        for (int i = 3; i < 50; i++) store.Put(Key(ThumbKeepGroup.Documents, i), StoredKind.Jpeg, 256, 192, Payload(i), ThumbKeepGroup.Documents);
        store.DropGroup(ThumbKeepGroup.Documents);
        Assert.True(store.WaitUntilWritten(Wait));
        // Whatever reached a pack before the drop went with it; nothing queued was written after it.
        Assert.Equal(0, Found(store, ThumbKeepGroup.Documents, 50));
        Assert.Equal(0, store.Count);
    }

    // ── A store written by 1.7.30 or 1.7.31 ─────────────────────────────────────────────────────────────────────────

    /// <summary>A record as 1.7.30 and 1.7.31 wrote it: byte 6 (now the group) zero.</summary>
    static byte[] OldRecord(ThumbKey key, StoredKind kind, byte[] payload)
    {
        var s = new byte[32 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x48545052); // "RPTH"
        s[4] = 1;
        s[5] = (byte)kind;
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(8), key.Hash64);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(16), key.Check32);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(20), (ushort)(payload.Length > 0 ? 256 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(22), (ushort)(payload.Length > 0 ? 192 : 0));
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(24), payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(28), Crc32C.Compute(payload));
        payload.CopyTo(s, 32);
        return s;
    }

    /// <summary>
    /// Writes two packs of seven pictures each, as 1.7.31 left them, and with <paramref name="withIndex"/> the index.bin
    /// it saved: "RPTHIDX1", version 1, the pack list, 36-byte entries and a CRC-32C of all of it.
    /// </summary>
    void WriteOldStore(bool withIndex)
    {
        Directory.CreateDirectory(Folder);
        var entries = new List<(ThumbKey Key, int Pack, int Offset, int Length)>();
        var packs = new List<(int Number, long Length)>();
        for (int pack = 1; pack <= 2; pack++)
        {
            using var f = new MemoryStream();
            for (int i = 0; i < 7; i++)
            {
                int n = (pack - 1) * 7 + i;
                var record = OldRecord(Key(ThumbKeepGroup.Pictures, n), StoredKind.Jpeg, Payload(n));
                entries.Add((Key(ThumbKeepGroup.Pictures, n), pack, (int)f.Length, record.Length));
                f.Write(record);
            }
            File.WriteAllBytes(Path.Combine(Folder, $"pack-{pack:D8}.bin"), f.ToArray());
            packs.Add((pack, f.Length));
        }
        if (!withIndex) return;
        var index = new byte[24 + packs.Count * 12 + entries.Count * 36 + 4];
        var s = index.AsSpan();
        "RPTHIDX1"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], packs.Count);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], entries.Count);
        int at = 24;
        foreach (var (number, length) in packs)
        {
            BinaryPrimitives.WriteInt32LittleEndian(s[at..], number);
            BinaryPrimitives.WriteInt64LittleEndian(s[(at + 4)..], length);
            at += 12;
        }
        foreach (var (key, pack, offset, length) in entries)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(s[at..], key.Hash64);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(at + 8)..], key.Check32);
            BinaryPrimitives.WriteInt32LittleEndian(s[(at + 12)..], pack);
            BinaryPrimitives.WriteInt64LittleEndian(s[(at + 16)..], offset);
            BinaryPrimitives.WriteInt32LittleEndian(s[(at + 24)..], length);
            s[at + 28] = (byte)StoredKind.Jpeg;
            BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 30)..], 256);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 32)..], 192);
            at += 36;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(s[at..], Crc32C.Compute(s[..at]));
        File.WriteAllBytes(Path.Combine(Folder, "index.bin"), index);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_store_of_1_7_31_opens_serves_its_thumbnails_and_goes_first(bool withIndex)
    {
        WriteOldStore(withIndex);
        using var store = Open(Small());
        // Its index is read as it is (the layout didn't change); without one the packs are scanned.
        Assert.Equal(withIndex ? 14 : 0, store.IndexEntriesLoaded);
        Assert.Equal(14, store.Count);
        for (int n = 0; n < 14; n++)
        {
            Assert.True(store.TryGet(Key(ThumbKeepGroup.Pictures, n), out var t));
            Assert.Equal(Payload(n), t.Payload.ToArray());
        }
        Assert.Equal([ThumbStore.PackGroup.Earlier, ThumbStore.PackGroup.Earlier], store.PackGroups.Select(p => p.Group));
        Assert.Equal(new ThumbStoreSplit(0, 0, 0, 2 * 924), store.Split);

        // New thumbnails go to packs of their own, never appended to the old ones.
        PutMany(store, ThumbKeepGroup.Documents, 7, from: 100);
        PutMany(store, ThumbKeepGroup.Pictures, 7, from: 200);
        Assert.Equal(924, new FileInfo(Path.Combine(Folder, "pack-00000002.bin")).Length);
        Assert.Equal(4, store.PackCount);

        // Over the limit: the earlier version's packs go first, though they hold pictures, then the documents.
        store.CapBytes = 2 * 1024;
        Assert.True(SpinUntil(() => store.PackCount == 2));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.Equal(0, Found(store, ThumbKeepGroup.Pictures, 14));
        Assert.Equal(7, Found(store, ThumbKeepGroup.Documents, 7, from: 100));
        Assert.Equal(7, Found(store, ThumbKeepGroup.Pictures, 7, from: 200));
        store.CapBytes = 1024;
        Assert.True(SpinUntil(() => store.PackCount == 1));
        Assert.True(store.WaitUntilWritten(Wait));
        Assert.Equal(0, Found(store, ThumbKeepGroup.Documents, 7, from: 100));
        Assert.Equal(7, Found(store, ThumbKeepGroup.Pictures, 7, from: 200));
    }

    [Fact]
    public void Switching_a_kind_off_leaves_what_an_earlier_version_saved()
    {
        WriteOldStore(withIndex: true);
        using var store = Open(Small());
        Assert.Equal(0, store.DropGroup(ThumbKeepGroup.Pictures));
        Assert.Equal(14, Found(store, ThumbKeepGroup.Pictures, 14));
    }

    // ── What 1.7.31 sees after a downgrade ──────────────────────────────────────────────────────────────────────────

    /// <summary>1.7.31's check of a pack's name: "pack-" and eight digits.</summary>
    static bool OldPackName(string name)
        => name.Length == 17 && name.StartsWith("pack-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
           && int.TryParse(name.AsSpan(5, 8), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0;

    /// <summary>1.7.31's Parse, rule for rule (it never looked at bytes 6 and 7): the record's length, or 0 when it would stop there.</summary>
    static int OldParse(byte[] b, int at)
    {
        if (b.Length - at < 32) return 0;
        var s = b.AsSpan(at);
        if (BinaryPrimitives.ReadUInt32LittleEndian(s) != 0x48545052 || s[4] != 1) return 0;
        var kind = (StoredKind)s[5];
        if (kind is < StoredKind.Jpeg or > StoredKind.Suspect) return 0;
        int payload = BinaryPrimitives.ReadInt32LittleEndian(s[24..]);
        if (payload < 0 || payload > ThumbStore.MaxPayloadBytes || payload > s.Length - 32) return 0;
        if ((kind is StoredKind.Jpeg or StoredKind.Png) != (payload > 0)) return 0;
        if (BinaryPrimitives.ReadUInt32LittleEndian(s[28..]) != Crc32C.Compute(s.Slice(32, payload))) return 0;
        if (BinaryPrimitives.ReadUInt64LittleEndian(s[8..]) == 0) return 0;
        return 32 + payload;
    }

    [Fact]
    public void A_store_written_now_reads_in_1_7_31_as_its_own()
    {
        var keys = new List<ulong>();
        using (var store = Open(Small()))
        {
            foreach (var group in new[] { ThumbKeepGroup.Pictures, ThumbKeepGroup.Videos, ThumbKeepGroup.Documents })
            {
                PutMany(store, group, 9);
                keys.AddRange(Enumerable.Range(0, 9).Select(i => Key(group, i).Hash64));
            }
            Assert.True(store.PutCrashed(Key(ThumbKeepGroup.Pictures, 99)));
            keys.Add(Key(ThumbKeepGroup.Pictures, 99).Hash64);
            Assert.True(store.WaitUntilWritten(Wait));
        }
        // Every file is one 1.7.31 knows: its packs, its index, its marker.
        var names = Directory.GetFiles(Folder).Select(Path.GetFileName).ToList();
        Assert.All(names, n => Assert.True(OldPackName(n!) || n is "index.bin" or "inflight.bin", n));
        // Each pack scans to its end by 1.7.31's rules, and finds every record.
        var found = new HashSet<ulong>();
        foreach (var path in Directory.GetFiles(Folder, "pack-*.bin"))
        {
            var bytes = File.ReadAllBytes(path);
            int at = 0;
            for (int length; at < bytes.Length && (length = OldParse(bytes, at)) > 0; at += length)
                found.Add(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at + 8)));
            Assert.Equal(bytes.Length, at);
        }
        Assert.True(found.SetEquals(keys));
        // The index passes 1.7.31's checks: its mark, version 1, a length that fits its counts, and its CRC.
        var index = File.ReadAllBytes(Path.Combine(Folder, "index.bin"));
        Assert.True(index.AsSpan(0, 8).SequenceEqual("RPTHIDX1"u8));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(index.AsSpan(8)));
        int packs = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(12)), entries = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(16));
        Assert.Equal(keys.Count, entries);
        Assert.Equal(24L + packs * 12L + entries * 36L + 4, index.Length);
        Assert.Equal(Crc32C.Compute(index.AsSpan(0, index.Length - 4)), BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(index.Length - 4)));
    }
}

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;
using Microsoft.Win32.SafeHandles;

namespace Anything.Core.Thumbs;

// How to use the store (Rock Preview's thumbnails on disk, step 2 of the ladder):
//
//   The store exists only while the setting "Instant thumbnails" is on (ini thumbnail_store=1; off by default). Off,
//   or in demo mode, use ThumbStore.Disabled(): it makes no folder and writes nothing, not even the marker.
//
//   string folder = chosenPath is { Length: > 0 } ? ThumbStore.FolderIn(chosenPath)      // "<chosen>\Rock Preview Thumbnails"
//                                                  : ThumbStore.FolderFor(databasePath); // "Thumbs" beside the database
//   var store = ThumbStore.Open(folder, new ThumbStoreOptions { CapBytes = ThumbStore.CapBytesFor(megabytes) });
//       Off the UI thread: opening reads the index and checks it against the packs (about 5 ms for 100,000 pictures,
//       longer when the index is missing and the packs are scanned). It never throws: a folder that can't be opened, or
//       one another process is using, gives a store that stores nothing (IsEnabled false); store.Problem says why, for
//       the line in Settings ("this location can't be used").
//   store.TryGet(key, out var thumb)       any thread; a positional read of one record (microseconds). A miss is false.
//       thumb.Kind Jpeg/Png: thumb.Payload is the encoded master at the key's size class (decode and scale it).
//       thumb.Kind None: every source answered cleanly and none could make a picture: show the icon.
//       thumb.Kind Crashed: making this picture once killed the program: show the icon, don't try again.
//   store.Put(key, StoredKind.Jpeg, w, h, bytes, ThumbKeep.GroupOf(path)) / PutNone(key) / PutCrashed(key)
//       any thread; queued for the writer thread and readable at once through TryGet. The store keeps the array: don't
//       change it afterwards. False means "not stored" (closed, disabled, too much queued, or bad arguments). The group
//       (pictures, videos, documents) decides which packs the picture goes to, and so how long it is kept.
//   store.Marker                           the in-flight marker: each decode/extract worker takes a slot (Acquire) and
//       wraps each piece of work in Enter(slot, key) ... Leave(slot, epoch); a slot left set by a crash becomes a
//       Suspect record at the next Open, or Crashed on a second strike.
//   store.CapBytes = ...; store.SizeBytes   the cap and what the packs take now. Over the cap, whole packs are deleted:
//       those of earlier versions first, then documents, then videos, then pictures, the oldest first within each. The
//       store also holds at most MaxRecords records (the memory its map takes); past that packs go in the same order.
//       Once full, a thumbnail of a kind ranked below everything the store holds (a document in a store of pictures)
//       isn't written: it would be the first thing dropped, and each drop is a pass over the whole map.
//   store.Split                            what the packs of each group take now (Settings' "Saved now"); never waits.
//   store.DropGroup(group)                 deletes every thumbnail of a group the user switched off; off the UI thread.
//   store.IsFull / store.BecameFull        packs are being dropped because the limit (or the record ceiling) is reached;
//       false again when the limit is raised, the store is cleared, a group dropped makes room, or it is reopened with
//       room. BecameFull comes on the writer
//       thread: subscribe, then read IsFull (it may have become full while opening).
//   store.DiskLow                          the last picture was not kept because the drive is nearly full.
//   store.Clear()                          deletes every picture; the store stays open (the setting's Clear button).
//   store.Dispose()                        when the program exits: the writer writes what is queued and saves the index;
//       Dispose waits for it 2 s at most. Until the writer has let go, opening the same folder again gives InUse.
//   store.IsPrivate                        whether only this user can read the thumbnails (checked once at open).
//
// Turning the setting off, or moving the store: call store.Dispose(), then store.WaitUntilClosed(timeout) off the UI
// thread. On success, ThumbStore.RemoveFolder(store.Folder) removes the store's files. A folder that still holds the
// user's files stays (StoreRemoval.LeftAlone). Uninstall uses the same function.
// ThumbStore.MeasureFolder(folder) gives what a store folder takes without opening it (Settings, the "free" question).
//
// Files in the folder: pack-00000001.bin ... (append-only, 16 MB at most each), index.bin (saved on close and every
// minute while it changes, less often when it is large; written to index.tmp and moved), inflight.bin (the marker). The
// store holds no file names: keys are hashes.
//
// Groups: each pack holds one group (PackGroup), written in byte 6 of every record's header and read from its first
// record at open, so the names, the index and the records keep the layout of 1.7.30 and 1.7.31. Those versions wrote that
// byte as zero and never read it: their packs open here as "earlier versions", and they open this version's store as
// their own (they append their records to its newest pack and drop packs oldest first; nothing is lost or damaged).

/// <summary>What a stored record says about a key.</summary>
public enum StoredKind : byte
{
    /// <summary>A JPEG master.</summary>
    Jpeg = 1,
    /// <summary>A PNG master.</summary>
    Png = 2,
    /// <summary>Nothing can make a picture of this file (every source answered cleanly): show its icon.</summary>
    None = 3,
    /// <summary>Making its picture killed the program twice: show its icon until the file changes.</summary>
    Crashed = 4,
    /// <summary>
    /// The program ended once while making its picture: a miss, so it is tried again (the end may have been a logoff or
    /// Task Manager, not this file); ending again while it is made turns it into <see cref="Crashed"/>.
    /// </summary>
    Suspect = 5,
}

/// <summary>A record found in the store. For Jpeg and Png, <see cref="Payload"/> is the encoded picture (don't change it); for None and Crashed it is empty.</summary>
public readonly record struct StoredThumb(StoredKind Kind, int Width, int Height, ReadOnlyMemory<byte> Payload)
{
    /// <summary>Whether this is a picture (Jpeg or Png) rather than a "no picture" record.</summary>
    public bool HasPicture => Kind is StoredKind.Jpeg or StoredKind.Png;
}

/// <summary>What the packs of each group take on disk, in bytes (<see cref="ThumbStore.Split"/>).</summary>
/// <param name="Earlier">Packs written by 1.7.31 and earlier, whose thumbnails carry no group.</param>
public readonly record struct ThumbStoreSplit(long Pictures, long Videos, long Documents, long Earlier);

/// <summary>Why a store stores nothing (<see cref="ThumbStore.Problem"/>).</summary>
public enum ThumbStoreProblem : byte
{
    /// <summary>The store works.</summary>
    None,
    /// <summary>Made with <see cref="ThumbStore.Disabled"/>: the setting is off, or demo mode.</summary>
    Off,
    /// <summary>The folder can't be made or written (a drive that isn't there, no rights).</summary>
    FolderUnusable,
    /// <summary>Another process has this store open.</summary>
    InUse,
    /// <summary>
    /// The folder belongs to another Windows account (its permissions give that account full control): the store
    /// writes nothing there, or that account could read this user's thumbnails.
    /// </summary>
    OtherAccount,
}

/// <summary>What <see cref="ThumbStore.RemoveFolder"/> did.</summary>
public enum StoreRemoval : byte
{
    /// <summary>The folder and the store's files in it are gone.</summary>
    Removed,
    /// <summary>There was no such folder: nothing to do.</summary>
    NotThere,
    /// <summary>The folder holds something that isn't the store's (a file or a folder): it stays with that in it, and only the store's own files were deleted.</summary>
    LeftAlone,
    /// <summary>Some of the store's files couldn't be deleted (still open, no rights); the folder stays.</summary>
    Failed,
}

/// <summary>Settings for <see cref="ThumbStore.Open"/>; the defaults are the product's.</summary>
public sealed class ThumbStoreOptions
{
    /// <summary>The cap on the packs' total size (the setting: 500 MB unless the user changed it).</summary>
    public long CapBytes { get; init; } = ThumbStore.CapBytesFor(ThumbStore.DefaultCapMegabytes);
    /// <summary>A pack grows to this size, then a new one is started (16 MB; tests use less).</summary>
    public int PackBytes { get; init; } = 16 << 20;
    /// <summary>Where the free disk space comes from (tests inject a low disk).</summary>
    internal IPowerPolicy Power { get; init; } = DefaultPowerPolicy.Current;
    /// <summary>How often appended records are flushed to the disk.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>How often a changed index is saved while the program runs, at most (a large index is saved less often).</summary>
    public TimeSpan IndexSaveInterval { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>How often the free disk space is checked at most.</summary>
    public TimeSpan DiskCheckInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Bytes queued for the writer at most; a Put beyond it is not stored.</summary>
    public long MaxQueuedBytes { get; init; } = 64L << 20;
    /// <summary>Tests: asked before each append to a pack; true makes that write fail as a full or broken disk would.</summary>
    internal Func<bool>? FailWrite { get; init; }
    /// <summary>Tests: called by Clear once it has emptied and saved the index and let go of the store's locks (it still holds the marker), before it deletes the packs.</summary>
    internal Action? BeforeClearDeletes { get; init; }
    /// <summary>How long Dispose waits for the writer to finish closing (it returns then; the writer finishes alone).</summary>
    internal TimeSpan CloseWait { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Tests: called on the writer thread at a clean close, before the final flush and index save.</summary>
    internal Action? BeforeFinalSave { get; init; }
    /// <summary>Tests: whether the folder's drive keeps permissions (false: FAT or exFAT; null: can't tell), instead of asking the volume.</summary>
    internal Func<string, bool?>? KeepsPermissions { get; init; }
    /// <summary>Tests: whether an account named in a folder's permissions exists (a made-up one stands for another user), instead of asking Windows.</summary>
    internal Func<System.Security.Principal.SecurityIdentifier, bool>? AccountExists { get; init; }
    /// <summary>The most records the store holds (tests use less); past it packs go in the same order as past the cap.</summary>
    internal int MaxRecords { get; init; } = ThumbStore.MaxRecords;
    /// <summary>The largest index.bin saved or read; 0: what <see cref="MaxRecords"/> needs, with room to spare (tests set less).</summary>
    internal long MaxIndexBytes { get; init; }
    /// <summary>Records read at a time when the index is loaded (tests use few, so a load crosses many parts).</summary>
    internal int IndexPartRecords { get; init; } = 1 << 16;
}

/// <summary>
/// Rock Preview's thumbnail store on disk (see "How to use" at the top of this file). One process uses a folder at a
/// time (the marker file is held open to make sure). Every public member is thread-safe and nothing throws to callers:
/// an IO error is a miss or "not stored". Reads are positional and run in parallel; writes go through one writer thread
/// that appends in batches. A damaged record (CRC or second hash wrong) is a miss and leaves the index.
/// </summary>
public sealed class ThumbStore : IDisposable
{
    /// <summary>The folder's name beside the index database.</summary>
    public const string FolderName = "Thumbs";
    /// <summary>The smallest limit and the default, in megabytes. The user may type any whole number from the smallest up.</summary>
    public const int MinCapMegabytes = 100, DefaultCapMegabytes = 500;
    /// <summary>
    /// 4,000,000 MB (4 TB): not a choice offered to anyone, only a sanity bound for a number typed in or edited into the
    /// ini, so it stays a sensible int of megabytes.
    /// </summary>
    public const int MaxCapMegabytes = 4_000_000;
    /// <summary>
    /// The most records the store holds, whatever the limit: its map takes about 50 bytes of memory per record (measured
    /// 148 MB at 3 million, opened from its 108 MB index in 0.3 s), so the program's memory stays bounded. Past it packs
    /// go in the same order as past the limit. At today's sizes (13 KB a small photo) it is about 39 GB of pictures.
    /// </summary>
    public const int MaxRecords = 3_000_000;
    /// <summary>
    /// A megabyte of the cap: 1,000,000 bytes, the unit every size in Settings is shown in, so the limit, what the store
    /// takes, the estimate and the free space read alike (a full 500 MB store reads "500 MB of 500 MB").
    /// </summary>
    public const long BytesPerMegabyte = 1_000_000;
    /// <summary>A picture's encoded bytes at most (a 512 px PNG is far below it).</summary>
    public const int MaxPayloadBytes = 8 << 20;

    const string IndexName = "index.bin", IndexTempName = "index.tmp", MarkerName = "inflight.bin";
    const string PackPrefix = "pack-", PackSuffix = ".bin";
    const int RecordHeaderBytes = 32;
    const uint RecordMagic = 0x48545052; // "RPTH"
    const byte RecordVersion = 1;
    static ReadOnlySpan<byte> IndexMagic => "RPTHIDX1"u8;
    const ushort IndexVersion = 1;
    const int IndexHeaderBytes = 24, IndexPackBytes = 12, IndexEntryBytes = 36;
    const long MaxScanBytes = 64L << 20;
    // A large index is saved less often, so writing it costs about this much disk work a minute at most.
    internal const long IndexBytesPerMinute = 20_000_000;

    /// <summary>
    /// Which thumbnails a pack holds: byte 6 of each record's header (zero, and never read, in 1.7.30 and 1.7.31). The
    /// first three match <see cref="ThumbKeepGroup"/>.
    /// </summary>
    internal enum PackGroup : byte
    {
        /// <summary>Written by 1.7.31 or earlier (or a pack whose first record can't be read): no group known.</summary>
        Earlier = 0,
        Pictures = 1,
        Videos = 2,
        Documents = 3,
        /// <summary>Records without a picture ("nothing to show", "crashed", "suspect"): 32 bytes each.</summary>
        NoPicture = 4,
    }

    const int GroupCount = 5;

    /// <summary>
    /// The order packs are dropped in when the store is over its limit, lowest first: earlier versions' packs, then
    /// documents, videos, pictures. Records without a picture go with the pictures, by age: a "crashed" record lost
    /// early would let a file that kills the program be tried again, and they take next to no room.
    /// </summary>
    static int DropRank(PackGroup group) => group switch
    {
        PackGroup.Documents => 1,
        PackGroup.Videos => 2,
        PackGroup.Pictures or PackGroup.NoPicture => 3,
        _ => 0,
    };

    sealed class Pack(int number, string path, long length, PackGroup group = PackGroup.Earlier)
    {
        public readonly int Number = number;
        public readonly string Path = path;
        public readonly PackGroup Group = group;
        // Written to since it was last flushed (under _io).
        public bool Dirty;
        // Physical bytes: what the pack takes on disk, counted toward the size and the cap.
        public long Length = length;
        // Where the last record that passed the checks ends. The index saves this, not Length, so a pack with a damaged
        // tail is scanned from here again at the next open, found damaged again and never appended to.
        public long Verified = length;
        public SafeFileHandle? Handle;
        public bool Writable;
        // Made by this store with CreateNew in this session: certainly its own, so it may be deleted when nothing got in.
        public bool Made;
        // Records in the map that point into this pack (under _gate): how many the record ceiling frees when it goes.
        public int Live;
    }

    // 24 bytes: an offset inside a pack fits an int (a pack ends near PackBytes), which keeps the map small.
    readonly record struct Entry(int Pack, int Offset, int Length, uint Check, ushort Width, ushort Height, StoredKind Kind);

    sealed class Pending(ulong hash, uint check, StoredKind kind, ushort width, ushort height, byte[] payload, PackGroup group)
    {
        public readonly ulong Hash = hash;
        public readonly uint Check = check;
        public readonly StoredKind Kind = kind;
        public readonly PackGroup Group = group;
        public readonly ushort Width = width;
        public readonly ushort Height = height;
        public readonly byte[] Payload = payload;
        public int Bytes => RecordHeaderBytes + Payload.Length;
    }

    /// <summary>
    /// The records by key, in shards: no single array grows huge, a resize copies one shard, and a pass over every record
    /// (evicting, saving the index) can let the store's lock go between shards. Shards grow by a quarter once large, not
    /// double, so the memory stays near what the records need. Not thread-safe: used under _gate.
    /// </summary>
    sealed class EntryMap
    {
        public const int ShardCount = 64;
        readonly Dictionary<ulong, Entry>[] _shards = new Dictionary<ulong, Entry>[ShardCount];
        int _count;

        public EntryMap() { for (int i = 0; i < ShardCount; i++) _shards[i] = []; }

        public int Count => _count;

        // The keys are hashes already: their top bits spread evenly.
        static int ShardOf(ulong hash) => (int)(hash >> 58);

        public Dictionary<ulong, Entry> Shard(int index) => _shards[index];

        public bool TryGetValue(ulong hash, out Entry entry) => _shards[ShardOf(hash)].TryGetValue(hash, out entry);

        /// <summary>Sets the record for <paramref name="hash"/>; true when it replaced one (<paramref name="old"/>).</summary>
        public bool Set(ulong hash, Entry entry, out Entry old)
        {
            var shard = _shards[ShardOf(hash)];
            ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(shard, hash);
            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref slot))
            {
                old = slot;
                slot = entry;
                return true;
            }
            int capacity = shard.EnsureCapacity(0);
            if (shard.Count >= capacity) shard.EnsureCapacity(capacity < 4096 ? Math.Max(64, capacity * 2) : capacity + capacity / 4);
            shard.Add(hash, entry);
            _count++;
            old = default;
            return false;
        }

        public bool Remove(ulong hash, out Entry old)
        {
            if (!_shards[ShardOf(hash)].Remove(hash, out old)) return false;
            _count--;
            return true;
        }

        /// <summary>Removes the shard's records that point into the packs marked in <paramref name="gone"/> (pack number minus <paramref name="first"/>); how many went.</summary>
        public int RemovePacks(int index, bool[] gone, int first)
        {
            var shard = _shards[index];
            int removed = 0;
            // Removing while enumerating is allowed for Dictionary since .NET Core 3.0.
            foreach (var (hash, e) in shard)
            {
                uint at = (uint)(e.Pack - first);
                if (at < (uint)gone.Length && gone[at] && shard.Remove(hash)) removed++;
            }
            _count -= removed;
            return removed;
        }

        /// <summary>Makes room for <paramref name="total"/> records in all, spread over the shards (an index about to be loaded).</summary>
        public void Reserve(int total)
        {
            int each = (int)Math.Min(int.MaxValue, total / (long)ShardCount + total / (16L * ShardCount) + 16);
            foreach (var shard in _shards) shard.EnsureCapacity(each);
        }

        /// <summary>Empties the map and lets its memory go.</summary>
        public void Clear()
        {
            for (int i = 0; i < ShardCount; i++) _shards[i] = [];
            _count = 0;
        }
    }

    readonly string _folder;
    readonly ThumbStoreOptions _options;
    readonly bool _enabled;
    readonly object _gate = new();   // the map, the queue, the pack list and the sizes
    readonly object _io = new();     // the writer's file work, Clear, SaveIndex and Close
    readonly EntryMap _map = new();
    // Packs the cap let go whose file couldn't be deleted yet (a backup reading it without sharing deletes): they still
    // take their space, so they count toward the cap and the size until a later try deletes them.
    readonly List<Pack> _undeleted = [];
    long _undeletedBytes;
    // The marker is let go only when both the writer and Dispose are done with the folder: a writer stuck in the disk past
    // Dispose's wait still writes there, so no other store may open the folder until it ends.
    int _markerHolders = 2;
    Action? _markerClose;
    readonly Dictionary<ulong, Pending> _pending = [];
    List<Pending> _queue = [];
    readonly SortedDictionary<int, Pack> _packs = [];
    readonly AutoResetEvent _wake = new(false);
    readonly Thread? _writer;
    // The pack each group appends to now, by PackGroup (none for earlier versions). Changed under _io and _gate.
    readonly Pack?[] _currents = new Pack?[GroupCount];
    // What each group's packs take, by PackGroup: written under _gate, read without a lock (Split).
    readonly long[] _groupBytes = new long[GroupCount];
    int _nextNumber = 1;
    int _packFloor; // Packs below this number were cleared, even if something still holds them on disk.
    int _generation;
    long _total;
    long _queuedBytes;
    long _cap;
    bool _indexDirty;
    volatile bool _saveIndexSoon;
    bool _unflushed;
    bool _writing;
    bool _closed;
    volatile bool _stopping;
    // Set with _stopping by Dispose only (not Abandon): the writer, as it ends, flushes, saves the index and closes the
    // files itself, so the thread that called Dispose (the UI thread at exit) never waits on a slow disk.
    bool _saveOnStop;
    long _lastFlush = Environment.TickCount64, _lastIndexSave = Environment.TickCount64;
    long _diskCheckedAt = long.MinValue;
    bool _diskLow;
    // The signals for the window (IsFull, DiskLow): read from any thread without a lock.
    volatile bool _full, _diskLowRefused;
    // 1 when the store became full; the writer raises BecameFull once it has let go of the locks.
    int _becameFullPending;
    // How often a changed index may be saved, from the size of the last one written.
    long _indexSaveEveryMs;
    readonly int _maxRecords;
    readonly long _maxIndexBytes;
    // When a pack write last failed (a full disk, a quota, a share that went away): for one disk-check interval after it,
    // batches are dropped without making a new pack, so failures don't leave a trail of empty files.
    long _writeFailedAt = long.MinValue;

    ThumbStore(string folder, ThumbStoreOptions options, ThumbMarker marker, bool enabled, ThumbStoreProblem problem = ThumbStoreProblem.None)
    {
        _folder = folder;
        _options = options;
        _cap = Math.Max(1, options.CapBytes);
        _maxRecords = Math.Max(1, options.MaxRecords);
        _maxIndexBytes = options.MaxIndexBytes > 0 ? options.MaxIndexBytes : MaxIndexBytesFor(_maxRecords);
        _indexSaveEveryMs = (long)options.IndexSaveInterval.TotalMilliseconds;
        Marker = marker;
        _enabled = enabled;
        Problem = problem;
        if (enabled)
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "Thumbnail store writer" };
    }

    /// <summary>The store's folder beside the index database: a folder named Thumbs in the database's folder.</summary>
    public static string FolderFor(string databasePath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? "", FolderName);

    /// <summary>The folder made inside a place the user chose in Settings ("Change location...").</summary>
    public const string ChosenFolderName = "Rock Preview Thumbnails";

    /// <summary>
    /// The store's folder inside <paramref name="chosenFolder"/>, the place the user picked (ini thumbnail_store_path):
    /// a folder named "Rock Preview Thumbnails" in it, so the store never mixes its files with the user's.
    /// </summary>
    public static string FolderIn(string chosenFolder) => Path.Combine(Path.GetFullPath(chosenFolder), ChosenFolderName);

    /// <summary>The cap in bytes for the setting's megabytes (decimal, <see cref="BytesPerMegabyte"/>), clamped to <see cref="MinCapMegabytes"/> to <see cref="MaxCapMegabytes"/>.</summary>
    public static long CapBytesFor(int megabytes) => Math.Clamp(megabytes, MinCapMegabytes, MaxCapMegabytes) * BytesPerMegabyte;

    /// <summary>A store that stores nothing and touches no disk (demo mode, the setting off).</summary>
    public static ThumbStore Disabled() => Disabled(ThumbStoreProblem.Off, "");

    static ThumbStore Disabled(ThumbStoreProblem problem, string folder)
        => new(folder, new ThumbStoreOptions(), ThumbMarker.InMemory(), false, problem);

    /// <summary>
    /// Opens (or creates) the store in <paramref name="folder"/>: loads the index, scans what it doesn't cover, turns
    /// keys left in the in-flight marker into Suspect or Crashed records and starts the writer. Call it off the UI
    /// thread. Never throws: when the folder can't be opened, or another process uses it, the store stores nothing.
    /// </summary>
    public static ThumbStore Open(string folder, ThumbStoreOptions? options = null)
    {
        options ??= new ThumbStoreOptions();
        FolderAccess access;
        try
        {
            folder = Path.GetFullPath(folder);
            access = EnsurePrivateFolder(folder, options);
            // Another account's store: nothing is written there, not even the marker.
            if (access == FolderAccess.OtherAccount) return Disabled(ThumbStoreProblem.OtherAccount, folder);
            // A file with one of the store's plain names (index.bin, index.tmp, inflight.bin) that isn't the store's own
            // belongs to someone else: the store would write over it, so it doesn't use this folder.
            foreach (var name in new[] { IndexName, IndexTempName, MarkerName })
                if (OwnFile(Path.Combine(folder, name)) == false) return Disabled(ThumbStoreProblem.FolderUnusable, folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Disabled(ThumbStoreProblem.FolderUnusable, folder);
        }
        var marker = ThumbMarker.Open(Path.Combine(folder, MarkerName), out var leftovers, out bool inUse);
        if (marker == null) return Disabled(inUse ? ThumbStoreProblem.InUse : ThumbStoreProblem.FolderUnusable, folder);
        var store = new ThumbStore(folder, options, marker, true) { IsPrivate = access == FolderAccess.Private };
        try
        {
            lock (store._io)
            {
                store.Load();
                if (leftovers.Count > 0)
                {
                    // Two strikes: the first time a key is found in flight it only becomes a suspect (tried again), since
                    // the program may have been ended from outside (a logoff, Task Manager, a crash elsewhere); found in
                    // flight again, it is the file that kills it.
                    var crashed = leftovers.Select(e => new Pending(e.Hash64, e.Check32, marker.KindForLeftover(e,
                        store.Has(e.Hash64, e.Check32, StoredKind.Suspect) ? StoredKind.Crashed : StoredKind.Suspect), 0, 0, [], PackGroup.NoPicture)).ToList();
                    var missing = crashed.Where(p => !store.Has(p.Hash, p.Check, p.Kind)).ToList();
                    lock (store._gate)
                        foreach (var p in missing)
                        {
                            store._pending[p.Hash] = p;
                            store._queuedBytes += p.Bytes;
                        }
                    store.WriteBatch(missing);
                    store.CrashedAtOpen = crashed.Count(p => p.Kind == StoredKind.Crashed && store.Has(p.Hash, p.Check, StoredKind.Crashed));
                    // Keep the slots for the next open if any of these writes failed.
                    if (crashed.All(p => store.Has(p.Hash, p.Check, p.Kind))) marker.ClearLeftovers();
                }
                store.EnforceCap();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Whatever was loaded stays usable; the writer still starts.
        }
        store._writer!.Start();
        return store;
    }

    /// <summary>Whether this store keeps pictures (false: every TryGet misses and every Put is refused).</summary>
    public bool IsEnabled => _enabled;

    /// <summary>The store's folder: "" for <see cref="Disabled()"/>; the folder that could not be used when <see cref="Problem"/> says so.</summary>
    public string Folder => _folder;

    /// <summary>Why this store stores nothing, or <see cref="ThumbStoreProblem.None"/> when it works.</summary>
    public ThumbStoreProblem Problem { get; }

    /// <summary>
    /// Whether only this user (with SYSTEM and administrators) can read the stored thumbnails, as checked once at open.
    /// False for a disabled store, on a drive that keeps no permissions (FAT, exFAT, some network shares), when the
    /// permissions can't be read or set, and for a folder that lets others read and holds something that isn't the
    /// store's (its permissions are left as they are). A store that isn't private still works: Settings says so.
    /// </summary>
    public bool IsPrivate { get; private init; }

    /// <summary>The in-flight marker the decode and extract workers use (an in-memory one for a disabled store).</summary>
    public ThumbMarker Marker { get; }

    /// <summary>Keys the last session was making when it died, recorded as Crashed at open.</summary>
    public int CrashedAtOpen { get; private set; }

    /// <summary>The cap on the packs' total size; lowering it deletes packs soon after, in the order of <see cref="DropRank"/>, (on the writer thread).</summary>
    public long CapBytes
    {
        get { lock (_gate) return _cap; }
        set
        {
            lock (_gate)
            {
                long cap = Math.Max(1, value);
                // More room: the store isn't full any more (it says so again if it fills the new limit too).
                if (cap > _cap) _full = false;
                _cap = cap;
            }
            if (_enabled) _wake.Set();
        }
    }

    /// <summary>
    /// Whether the store is full: true from the moment a pack was dropped because the limit (or <see cref="MaxRecords"/>)
    /// was reached, until the limit is raised, the store is cleared, a group dropped (<see cref="DropGroup"/>) leaves it
    /// below the limit, or it is opened again with room. Any thread; never waits.
    /// Lowering the limit makes it full too, as packs are dropped to fit.
    /// </summary>
    public bool IsFull => _full;

    /// <summary>
    /// Whether a thumbnail of <paramref name="group"/>, handed to <see cref="Put"/> now, would be thrown away because the
    /// store is full and holds nothing that goes before it (a full store of pictures refuses documents and videos). Any
    /// thread; never waits. False for <see cref="ThumbKeepGroup.None"/> and for a store that stores nothing.
    /// </summary>
    public bool Refuses(ThumbKeepGroup group)
        => _enabled && group is (ThumbKeepGroup.Pictures or ThumbKeepGroup.Videos or ThumbKeepGroup.Documents) && RefusesNow((PackGroup)group);

    /// <summary>
    /// Raised once each time <see cref="IsFull"/> turns true, on the store's writer thread (never under a lock of the
    /// store's, so a handler may call into it; it must not block for long, and should post to the UI thread). A store that
    /// filled while it opened raises it on the writer's first round: subscribe, then read <see cref="IsFull"/>. Never
    /// raised by a disabled store.
    /// </summary>
    public event Action? BecameFull;

    /// <summary>
    /// Whether the last picture was not kept because the drive is nearly full (<see cref="PowerRules.LowDisk"/>); false again once a
    /// picture is written. Any thread; never waits. False for a disabled store.
    /// </summary>
    public bool DiskLow => _diskLowRefused;

    /// <summary>What the packs take on disk now, in bytes. Never waits (Settings reads it on the UI thread while the writer may hold the store's lock).</summary>
    public long SizeBytes => Interlocked.Read(ref _total) + Interlocked.Read(ref _undeletedBytes);

    /// <summary>
    /// What the packs of each group take now, from the figures the store keeps in memory (packs that couldn't be deleted
    /// yet are in <see cref="SizeBytes"/> only). Never waits.
    /// </summary>
    public ThumbStoreSplit Split => new(Interlocked.Read(ref _groupBytes[(int)PackGroup.Pictures]), Interlocked.Read(ref _groupBytes[(int)PackGroup.Videos]),
        Interlocked.Read(ref _groupBytes[(int)PackGroup.Documents]), Interlocked.Read(ref _groupBytes[(int)PackGroup.Earlier]));

    /// <summary>The packs and their groups, oldest first (tests).</summary>
    internal (int Number, PackGroup Group)[] PackGroups { get { lock (_gate) return [.. _packs.Values.Select(p => (p.Number, p.Group))]; } }

    /// <summary>Keys the store can answer (records written; queued ones not counted).</summary>
    public int Count { get { lock (_gate) return _map.Count; } }

    /// <summary>The packs on disk (tests and diagnostics).</summary>
    public int PackCount { get { lock (_gate) return _packs.Count; } }

    /// <summary>Passes over the whole map to drop evicted packs' records (tests: many packs at once cost one pass).</summary>
    internal int EvictionPasses => Volatile.Read(ref _evictionPasses);
    int _evictionPasses;

    /// <summary>Thumbnails not written because the full store would have dropped them first (tests).</summary>
    internal int RefusedWhileFull { get { lock (_gate) return _refusedWhileFull; } }
    int _refusedWhileFull;

    /// <summary>Records the last open took from index.bin (tests: the index was used, not a scan of the packs).</summary>
    internal int IndexEntriesLoaded { get; private set; }

    /// <summary>Who still holds the marker and with it the folder: the writer, Dispose and a running Clear (tests).</summary>
    internal int MarkerHolders => Volatile.Read(ref _markerHolders);

    // ── Reading ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The record for <paramref name="key"/>, from the queue or the packs. False on a miss, a damaged record or any IO error.</summary>
    public bool TryGet(in ThumbKey key, out StoredThumb thumb) => TryGet(key.Hash64, key.Check32, out thumb);

    /// <summary>The record for a key given by its two hashes (the second must match too).</summary>
    internal bool TryGet(ulong hash, uint check, out StoredThumb thumb)
    {
        thumb = default;
        if (!_enabled) return false;
        Entry e;
        Pack? pack;
        SafeFileHandle? handle;
        lock (_gate)
        {
            if (_closed) return false;
            if (_pending.TryGetValue(hash, out var p))
            {
                if (p.Check != check || p.Kind == StoredKind.Suspect) return false;
                thumb = new StoredThumb(p.Kind, p.Width, p.Height, p.Payload);
                return true;
            }
            // A suspect is tried again: a miss.
            if (!_map.TryGetValue(hash, out e) || e.Check != check || e.Kind == StoredKind.Suspect) return false;
            if (e.Kind is StoredKind.None or StoredKind.Crashed)
            {
                thumb = new StoredThumb(e.Kind, 0, 0, ReadOnlyMemory<byte>.Empty);
                return true;
            }
            if (!_packs.TryGetValue(e.Pack, out pack)) return false;
            handle = pack.Handle;
        }
        try
        {
            handle ??= ReadHandle(pack);
            if (handle == null) return false;
            var bytes = new byte[e.Length];
            int n = ReadFully(handle, bytes, e.Offset);
            if (n == bytes.Length && Parse(bytes, 0, bytes.Length, out var r) && r.Length == e.Length
                && r.Hash == hash && r.Check == check && r.Kind == e.Kind)
            {
                thumb = new StoredThumb(r.Kind, r.Width, r.Height, bytes.AsMemory(RecordHeaderBytes, r.PayloadLength));
                return true;
            }
            Forget(hash, e);
            return false;
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException)
        {
            // A pack deleted by the cap or Clear while this read ran, or the disk went away: a miss.
            return false;
        }
    }

    bool Has(ulong hash, uint check, StoredKind kind)
    {
        lock (_gate) return _map.TryGetValue(hash, out var e) && e.Check == check && e.Kind == kind;
    }

    SafeFileHandle? ReadHandle(Pack pack)
    {
        SafeFileHandle opened;
        try { opened = File.OpenHandle(pack.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        lock (_gate)
        {
            if (pack.Handle == null && !_closed && _packs.TryGetValue(pack.Number, out var still) && ReferenceEquals(still, pack))
            {
                pack.Handle = opened;
                return opened;
            }
            var existing = pack.Handle;
            opened.Dispose();
            return existing;
        }
    }

    /// <summary>
    /// Lets the record for <paramref name="key"/> go (a picture whose bytes passed the checks but don't decode as a
    /// thumbnail): the next request makes it again. Any thread; never throws.
    /// </summary>
    public void Forget(in ThumbKey key)
    {
        if (!_enabled) return;
        lock (_gate)
        {
            if (_closed) return;
            if (_pending.TryGetValue(key.Hash64, out var p) && p.Check == key.Check32) _pending.Remove(key.Hash64);
            if (_map.TryGetValue(key.Hash64, out var e) && e.Check == key.Check32)
            {
                RemoveEntry(key.Hash64);
                _indexDirty = true;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="key"/> is a suspect: the program ended once while its picture was being made (TryGet
    /// misses it, so it is tried again; ending again while it is made turns it into Crashed). One lookup under the
    /// store's lock; any thread; never throws. False for a disabled or closed store.
    /// </summary>
    public bool IsSuspect(in ThumbKey key)
    {
        if (!_enabled) return false;
        lock (_gate)
        {
            if (_closed) return false;
            // A record still queued is the newest word on the key.
            if (_pending.TryGetValue(key.Hash64, out var p)) return p.Check == key.Check32 && p.Kind == StoredKind.Suspect;
            return _map.TryGetValue(key.Hash64, out var e) && e.Check == key.Check32 && e.Kind == StoredKind.Suspect;
        }
    }

    /// <summary>
    /// Lets the suspicion on <paramref name="key"/> go (its work has just ended without taking the program down): its
    /// record is removed only when it is a Suspect record, queued or written; a picture, None or Crashed record stays.
    /// True when one was removed. Any thread; never throws.
    /// </summary>
    public bool ClearSuspect(in ThumbKey key)
    {
        if (!_enabled) return false;
        bool removed = false;
        lock (_gate)
        {
            if (_closed) return false;
            // The writer skips a queued record that is no longer the pending one, and counts its bytes off then.
            if (_pending.TryGetValue(key.Hash64, out var p) && p.Check == key.Check32 && p.Kind == StoredKind.Suspect)
                removed = _pending.Remove(key.Hash64);
            if (_map.TryGetValue(key.Hash64, out var e) && e.Check == key.Check32 && e.Kind == StoredKind.Suspect)
            {
                RemoveEntry(key.Hash64);
                _indexDirty = true;
                // A later crash should see that this key was made successfully, even between regular saves.
                _saveIndexSoon = true;
                _wake.Set();
                removed = true;
            }
        }
        return removed;
    }

    void Forget(ulong hash, Entry e)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(hash, out var now) && now == e)
            {
                RemoveEntry(hash);
                _indexDirty = true;
            }
        }
    }

    /// <summary>Removes a record and counts it off its pack. Under _gate.</summary>
    void RemoveEntry(ulong hash)
    {
        if (_map.Remove(hash, out var old) && _packs.TryGetValue(old.Pack, out var pack)) pack.Live--;
    }

    /// <summary>Sets a record written to <paramref name="pack"/>, counting it on its pack and a replaced one off its own. Under _gate.</summary>
    void SetEntry(ulong hash, Entry entry, Pack pack)
    {
        if (_map.Set(hash, entry, out var old) && _packs.TryGetValue(old.Pack, out var before)) before.Live--;
        pack.Live++;
    }

    /// <summary>Counts each pack's records again in one pass (after opening, when the map was filled before the packs were known). Under _gate.</summary>
    void RecountLive()
    {
        foreach (var pack in _packs.Values) pack.Live = 0;
        Pack? last = null;
        for (int i = 0; i < EntryMap.ShardCount; i++)
            foreach (var e in _map.Shard(i).Values)
            {
                if (last?.Number != e.Pack && !_packs.TryGetValue(e.Pack, out last)) continue;
                last!.Live++;
            }
    }

    static int ReadFully(SafeFileHandle handle, byte[] buffer, long offset)
    {
        int n = 0;
        while (n < buffer.Length)
        {
            int got = RandomAccess.Read(handle, buffer.AsSpan(n), offset + n);
            if (got <= 0) break;
            n += got;
        }
        return n;
    }

    // ── Writing ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores the master picture of <paramref name="key"/> (Jpeg or Png, <paramref name="width"/> × <paramref name="height"/>).
    /// The store keeps <paramref name="payload"/>: don't change it afterwards. Readable at once; written soon after.
    /// <paramref name="group"/> is the file's group (<see cref="ThumbKeep.GroupOf"/>): pictures are kept longest. False
    /// when not stored (disabled, closed, too much queued, a kind other than Jpeg or Png, sizes out of range, or no group).
    /// </summary>
    public bool Put(in ThumbKey key, StoredKind kind, int width, int height, byte[] payload, ThumbKeepGroup group = ThumbKeepGroup.Pictures)
    {
        if (kind is not (StoredKind.Jpeg or StoredKind.Png) || payload == null || payload.Length == 0 || payload.Length > MaxPayloadBytes
            || width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue
            || group is not (ThumbKeepGroup.Pictures or ThumbKeepGroup.Videos or ThumbKeepGroup.Documents)) return false;
        return Enqueue(new Pending(key.Hash64, key.Check32, kind, (ushort)width, (ushort)height, payload, (PackGroup)group));
    }

    /// <summary>Records that nothing can make a picture of this file (only when every source answered cleanly).</summary>
    public bool PutNone(in ThumbKey key) => Enqueue(new Pending(key.Hash64, key.Check32, StoredKind.None, 0, 0, [], PackGroup.NoPicture));

    /// <summary>Records that making this file's picture killed the program.</summary>
    public bool PutCrashed(in ThumbKey key) => Enqueue(new Pending(key.Hash64, key.Check32, StoredKind.Crashed, 0, 0, [], PackGroup.NoPicture));

    bool Enqueue(Pending p)
    {
        if (!_enabled) return false;
        lock (_gate)
        {
            if (_closed || _stopping) return false;
            if (_queuedBytes + p.Bytes > _options.MaxQueuedBytes) return false;
            // A record put again replaces the queued one; the writer skips the older (and counts its bytes off then).
            _pending[p.Hash] = p;
            _queue.Add(p);
            _queuedBytes += p.Bytes;
        }
        _wake.Set();
        return true;
    }

    void WriterLoop()
    {
        try { WriterLoopCore(); }
        finally { LetGoOfMarker(null); }
    }

    /// <summary>
    /// The writer (<paramref name="close"/> null) or Dispose and Abandon (with the marker's way of closing) are done with
    /// the folder: the second of the two closes the marker, so another store may open the folder only once nothing here
    /// writes to it any more.
    /// </summary>
    void LetGoOfMarker(Action? close)
    {
        if (close != null) Volatile.Write(ref _markerClose, close);
        if (Interlocked.Decrement(ref _markerHolders) == 0) Volatile.Read(ref _markerClose)?.Invoke();
    }

    void WriterLoopCore()
    {
        int wait = (int)Math.Clamp(_options.FlushInterval.TotalMilliseconds, 10, 60_000);
        while (true)
        {
            _wake.WaitOne(wait);
            List<Pending> batch;
            int generation;
            lock (_gate)
            {
                batch = _queue;
                _queue = [];
                _writing = true;
                generation = _generation;
            }
            try
            {
                lock (_io)
                {
                    if (_closed) return;
                    // A Clear between taking the batch and writing it emptied the store: the batch goes with it.
                    if (batch.Count > 0 && generation == _generation) WriteBatch(batch);
                    EnforceCap();
                    long now = Environment.TickCount64;
                    if (_unflushed && now - _lastFlush >= _options.FlushInterval.TotalMilliseconds) FlushCurrent();
                    if (_saveIndexSoon || (_indexDirty && now - _lastIndexSave >= _indexSaveEveryMs)) SaveIndexLocked();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // A batch that couldn't be written is simply not stored.
            }
            finally
            {
                lock (_gate) _writing = false;
            }
            RaiseBecameFull();
            if (_stopping)
            {
                bool drained, save;
                lock (_gate)
                {
                    drained = _queue.Count == 0;
                    save = _saveOnStop;
                }
                if (drained)
                {
                    // Abandon (a crash, in tests) leaves the files as they are; Dispose has the writer close them.
                    if (save) FinishClosing();
                    return;
                }
            }
        }
    }

    /// <summary>Raises <see cref="BecameFull"/> when the store became full since the last round. On the writer thread, holding no lock.</summary>
    void RaiseBecameFull()
    {
        if (Interlocked.Exchange(ref _becameFullPending, 0) == 0) return;
        try { BecameFull?.Invoke(); }
        // A handler's mistake must not end the writer: the store would stop writing.
        catch (Exception) { }
    }

    /// <summary>
    /// The end of a clean close, on the writer thread: flushes the current pack, saves the index when it changed, tries
    /// once more to delete the packs Clear or the cap couldn't, and closes the files. Dispose doesn't wait for it past
    /// its own short wait, so a disk that stalls here (a network drive gone quiet) never holds up the program's exit. If
    /// the process ends first, the next open finds the packs through the old index, the scan and the checksums.
    /// </summary>
    void FinishClosing()
    {
        lock (_io)
        {
            if (_closed) return;
            try
            {
                _options.BeforeFinalSave?.Invoke();
                FlushCurrent();
                if (_indexDirty) SaveIndexLocked();
                // A last try at packs Clear or the cap couldn't delete.
                RetryUndeleted();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
            finally { CloseFiles(); }
        }
    }

    /// <summary>Appends records to the current pack (rolling to a new one when full), then lets readers find them. Under _io.</summary>
    void WriteBatch(List<Pending> batch)
    {
        lock (_gate)
        {
            // A full store would drop a picture of a kind ranked below everything it holds first, at the next eviction:
            // a whole pass over the map to free a few records, again and again while such thumbnails keep coming.
            // So it isn't written at all. Records without a picture always are (a "crashed" one must stay).
            // Records put again since (or cleared) are not written.
            batch = batch.Where(p =>
            {
                if (_pending.TryGetValue(p.Hash, out var q) && ReferenceEquals(q, p))
                {
                    if (p.Payload.Length == 0 || !RefusesNow(p.Group)) return true;
                    _pending.Remove(p.Hash);
                    _refusedWhileFull++;
                }
                _queuedBytes -= p.Bytes;
                return false;
            }).ToList();
        }
        if (batch.Count == 0) return;
        if (_writeFailedAt != long.MinValue && Environment.TickCount64 - _writeFailedAt < _options.DiskCheckInterval.TotalMilliseconds)
        {
            // A write failed moments ago: the disk is most likely full or gone, and a new pack would only fail too.
            Unqueue(batch);
            return;
        }
        if (IsDiskLowNow())
        {
            // Pictures wait for space; records without one (32 bytes: "none", "suspect", "crashed") are still written, or
            // a file that killed the program would be tried again and again while the disk is low.
            var pictures = batch.Where(p => p.Payload.Length > 0).ToList();
            if (pictures.Count > 0) _diskLowRefused = true;
            Unqueue(pictures);
            batch = batch.Where(p => p.Payload.Length == 0).ToList();
            if (batch.Count == 0) return;
        }
        // Each group goes to its own pack: the records of one group one after another (a stable sort keeps their order).
        if (batch.Exists(p => p.Group != batch[0].Group)) batch = [.. batch.OrderBy(p => p.Group)];
        int i = 0;
        while (i < batch.Count)
        {
            var group = batch[i].Group;
            var pack = WritablePack(batch[i].Bytes, group);
            if (pack == null) { Unqueue(batch.GetRange(i, batch.Count - i)); return; }
            // As many records of this group as fit in its pack go in one write.
            long room = Math.Max(_options.PackBytes - pack.Length, batch[i].Bytes);
            int end = i;
            long bytes = 0;
            while (end < batch.Count && batch[end].Group == group && bytes + batch[end].Bytes <= room) bytes += batch[end++].Bytes;
            var buffer = new byte[bytes];
            var entries = new Entry[end - i];
            long offset = pack.Length;
            int at = 0;
            bool picture = false;
            for (int k = i; k < end; k++)
            {
                var p = batch[k];
                Encode(buffer.AsSpan(at), p);
                // A pack is written only below PackBytes (an int), so the offset fits.
                entries[k - i] = new Entry(pack.Number, (int)(offset + at), p.Bytes, p.Check, p.Width, p.Height, p.Kind);
                at += p.Bytes;
                picture |= p.Payload.Length > 0;
            }
            try
            {
                if (_options.FailWrite?.Invoke() == true) throw new IOException("A write failure made by a test.");
                RandomAccess.Write(pack.Handle!, buffer, offset);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                WriteFailed(pack);
                Unqueue(batch.GetRange(i, batch.Count - i));
                return;
            }
            pack.Dirty = true;
            lock (_gate)
            {
                pack.Length += bytes;
                if (pack.Verified == offset) pack.Verified = pack.Length;
                _total += bytes;
                _groupBytes[(int)pack.Group] += bytes;
                for (int k = i; k < end; k++)
                {
                    var p = batch[k];
                    SetEntry(p.Hash, entries[k - i], pack);
                    if (_pending.TryGetValue(p.Hash, out var q) && ReferenceEquals(q, p)) _pending.Remove(p.Hash);
                    _queuedBytes -= p.Bytes;
                }
                _indexDirty = true;
                _unflushed = true;
            }
            if (picture) _diskLowRefused = false;
            i = end;
        }
    }

    /// <summary>
    /// Whether a thumbnail of <paramref name="group"/>, handed in now, would not be written (see <see cref="WriteBatch"/>).
    /// The writer (under _gate) and <see cref="Refuses"/> (any thread, no lock) both ask this.
    /// </summary>
    bool RefusesNow(PackGroup group) => _full && DropRank(group) < LowestRankHeld();

    /// <summary>The lowest <see cref="DropRank"/> among the groups whose packs take any space; 0 when none do. Needs no lock.</summary>
    int LowestRankHeld()
    {
        int lowest = int.MaxValue;
        for (int g = 0; g < GroupCount; g++)
            if (Interlocked.Read(ref _groupBytes[g]) > 0) lowest = Math.Min(lowest, DropRank((PackGroup)g));
        return lowest == int.MaxValue ? 0 : lowest;
    }

    void Unqueue(List<Pending> dropped)
    {
        lock (_gate)
            foreach (var p in dropped)
            {
                if (_pending.TryGetValue(p.Hash, out var q) && ReferenceEquals(q, p)) _pending.Remove(p.Hash);
                _queuedBytes -= p.Bytes;
            }
    }

    /// <summary>
    /// A write to <paramref name="pack"/> failed. Nothing is appended after a write that may have half landed, so the pack
    /// is written to no more. One this store just made that holds nothing is deleted (CreateNew made it, so it is
    /// certainly the store's own); one with records keeps its handle for readers. Under _io.
    /// </summary>
    void WriteFailed(Pack pack)
    {
        SafeFileHandle? drop = null;
        lock (_gate)
        {
            pack.Writable = false;
            if (ReferenceEquals(_currents[(int)pack.Group], pack)) _currents[(int)pack.Group] = null;
            if (pack.Made && pack.Length == 0 && _packs.TryGetValue(pack.Number, out var still) && ReferenceEquals(still, pack))
            {
                _packs.Remove(pack.Number);
                drop = pack.Handle;
                pack.Handle = null;
            }
        }
        _writeFailedAt = Environment.TickCount64;
        // The free space is looked at again with the next batch after the pause, not up to 30 s later.
        _diskCheckedAt = long.MinValue;
        if (drop != null)
        {
            drop.Dispose();
            TryDelete(pack.Path);
        }
    }

    /// <summary>The pack the next record of <paramref name="bytes"/> in <paramref name="group"/> goes to: the group's current one while it has room, else a new one. Under _io.</summary>
    Pack? WritablePack(int bytes, PackGroup group)
    {
        var current = _currents[(int)group];
        if (current != null && current.Handle != null && current.Writable && (current.Length == 0 || current.Length + bytes <= _options.PackBytes))
            return current;
        if (current != null && current.Writable) FlushPack(current);
        for (int attempt = 0; attempt < 16; attempt++)
        {
            int number = _nextNumber++;
            string path = Path.Combine(_folder, PackName(number));
            try
            {
                var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                var pack = new Pack(number, path, 0, group) { Handle = handle, Writable = true, Made = true };
                lock (_gate)
                {
                    _packs[number] = pack;
                    _currents[(int)group] = pack;
                }
                return pack;
            }
            catch (IOException) when (File.Exists(path)) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    /// <summary>Flushes the packs being written that have changed since. Under _io.</summary>
    void FlushCurrent()
    {
        _lastFlush = Environment.TickCount64;
        _unflushed = false;
        foreach (var pack in _currents)
            if (pack is { Dirty: true }) FlushPack(pack);
    }

    static void FlushPack(Pack pack)
    {
        pack.Dirty = false;
        if (pack.Handle == null) return;
        try { RandomAccess.FlushToDisk(pack.Handle); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    bool IsDiskLowNow()
    {
        long now = Environment.TickCount64;
        if (_diskCheckedAt != long.MinValue && now - _diskCheckedAt < _options.DiskCheckInterval.TotalMilliseconds) return _diskLow;
        _diskCheckedAt = now;
        try { _diskLow = PowerRules.LowDisk(_options.Power.Now(_folder)); }
        catch (Exception) { _diskLow = false; }
        return _diskLow;
    }

    /// <summary>
    /// Deletes packs while the store is over its cap or holds more than <see cref="ThumbStoreOptions.MaxRecords"/> records:
    /// earlier versions' packs first, then documents, videos and pictures (<see cref="DropRank"/>), the oldest first within
    /// each, so no picture goes while a document or video thumbnail is left. A group's open pack goes in its turn (the
    /// group starts a new one); the last pack holding anything only when it alone is over. Every pack that has to go is
    /// chosen first, so their records leave the map in one pass, whatever their number. While full, WriteBatch doesn't
    /// write what this would drop first. Under _io.
    /// </summary>
    void EnforceCap()
    {
        RetryUndeleted();
        // Again only when a pack couldn't be deleted: its space still counts, so older ones may have to go as well.
        while (EvictOldest()) { }
    }

    /// <summary>One round of <see cref="EnforceCap"/>; true when it should run again. Under _io.</summary>
    bool EvictOldest()
    {
        var evict = new List<Pack>();
        lock (_gate)
        {
            long bytes = _total + _undeletedBytes, packsBytes = _total;
            int records = _map.Count;
            if (bytes <= _cap && records <= _maxRecords) return false;
            // _packs is by number (age); the stable sort keeps that order within each rank.
            var order = _packs.Values.OrderBy(p => DropRank(p.Group)).ToList();
            int holding = 0;
            foreach (var pack in order) if (pack.Length > 0) holding++;
            foreach (var pack in order)
            {
                if (bytes <= _cap && records <= _maxRecords) break;
                if (ReferenceEquals(pack, _currents[(int)pack.Group]))
                {
                    // Empty: deleting it frees nothing. The last pack holding anything is kept unless it alone is over the
                    // cap or the ceiling (a held file still takes its space).
                    if (pack.Length == 0) continue;
                    if (holding == 1 && packsBytes <= _cap && records <= _maxRecords) break;
                    _currents[(int)pack.Group] = null;
                }
                evict.Add(pack);
                if (pack.Length > 0) holding--;
                bytes -= pack.Length;
                packsBytes -= pack.Length;
                records -= pack.Live;
            }
            if (evict.Count == 0) return false;
            foreach (var pack in evict)
            {
                _packs.Remove(pack.Number);
                _total -= pack.Length;
                _groupBytes[(int)pack.Group] -= pack.Length;
            }
            _indexDirty = true;
            if (!_full)
            {
                _full = true;
                Interlocked.Exchange(ref _becameFullPending, 1);
            }
        }
        RemoveRecordsOf(evict);
        Interlocked.Increment(ref _evictionPasses);
        bool held = false;
        foreach (var pack in evict)
        {
            pack.Handle?.Dispose();
            if (!TryDelete(pack.Path))
            {
                KeepUndeleted(pack);
                held = true;
            }
        }
        if (!held) return false;
        lock (_gate) return _total + _undeletedBytes > _cap && _packs.Count > 0;
    }

    /// <summary>
    /// A short pause between two holds of the store's lock in a long pass. The lock isn't fair: a thread that takes it
    /// back at once keeps a reader that is still waking up waiting for the whole pass. 50 microseconds lets it in.
    /// </summary>
    static void LetWaitersIn()
    {
        long until = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 20_000;
        Thread.Yield();
        while (System.Diagnostics.Stopwatch.GetTimestamp() < until) Thread.SpinWait(20);
    }

    /// <summary>
    /// Removes every record that points into <paramref name="gone"/> (packs already out of the pack list) in one pass over
    /// the map, one shard at a time, so readers wait for one shard, not the whole map; a reader in between finds the pack
    /// gone and misses. Under _io.
    /// </summary>
    void RemoveRecordsOf(List<Pack> gone)
    {
        int first = int.MaxValue, last = int.MinValue;
        foreach (var pack in gone)
        {
            first = Math.Min(first, pack.Number);
            last = Math.Max(last, pack.Number);
        }
        // Pack numbers only grow, and those between the first and last that stay are few: a small table, read per record.
        var marked = new bool[last - first + 1];
        foreach (var pack in gone) marked[pack.Number - first] = true;
        for (int i = 0; i < EntryMap.ShardCount; i++)
        {
            lock (_gate) _map.RemovePacks(i, marked, first);
            LetWaitersIn();
        }
    }

    /// <summary>
    /// Deletes every saved thumbnail of <paramref name="group"/> (the user switched that kind off): its packs, and what is
    /// queued for it. Thumbnails saved by 1.7.31 and earlier carry no group and stay until they are dropped for room.
    /// Safe while thumbnails are read and written; does nothing for a group with nothing saved. A large group takes a
    /// while (one pass over the map, then the files): call it off the UI thread. Makes the store not full when the drop
    /// leaves it below its limit and the ceiling. Returns how many packs went. Throws only on a fault of the program
    /// itself, such as memory running out, after letting go of the marker; the service catches it.
    /// </summary>
    public int DropGroup(ThumbKeepGroup group)
    {
        if (!_enabled || group is not (ThumbKeepGroup.Pictures or ThumbKeepGroup.Videos or ThumbKeepGroup.Documents)) return 0;
        var g = (PackGroup)group;
        List<Pack> drop;
        lock (_io)
        {
            lock (_gate)
            {
                // A closing store has let go of the folder, or is about to: its files are not this one's to delete.
                if (_closed || _stopping) return 0;
                // Queued for this group: not written (the writer skips a record that is no longer the pending one, and
                // counts its bytes off then).
                List<ulong>? unqueue = null;
                foreach (var (hash, p) in _pending)
                    if (p.Group == g) (unqueue ??= []).Add(hash);
                if (unqueue != null) foreach (var hash in unqueue) _pending.Remove(hash);
                drop = [.. _packs.Values.Where(p => p.Group == g)];
                if (drop.Count == 0) return 0;
                // One more holder of the marker until the deletes are done, as Clear is.
                Interlocked.Increment(ref _markerHolders);
                foreach (var pack in drop)
                {
                    _packs.Remove(pack.Number);
                    _total -= pack.Length;
                    _groupBytes[(int)g] -= pack.Length;
                }
                _currents[(int)g] = null;
                _indexDirty = true;
            }
            try { RemoveRecordsOf(drop); }
            catch (Exception)
            {
                LetGoOfMarker(null);
                throw;
            }
        }
        try
        {
            foreach (var pack in drop)
            {
                pack.Handle?.Dispose();
                if (!TryDelete(pack.Path)) KeepUndeleted(pack);
            }
        }
        finally { LetGoOfMarker(null); }
        lock (_gate)
            if (_full && _total + _undeletedBytes < _cap && _map.Count < _maxRecords) _full = false;
        return drop.Count;
    }

    /// <summary>Tries again to delete the packs the cap or Clear let go whose files couldn't be deleted then. The writer and Clear may both run it.</summary>
    void RetryUndeleted()
    {
        List<Pack> waiting;
        lock (_gate)
        {
            if (_undeleted.Count == 0) return;
            waiting = [.. _undeleted];
        }
        foreach (var p in waiting)
            if (TryDelete(p.Path) || !File.Exists(p.Path))
                lock (_gate)
                {
                    // Clear may try the same pack at the same time: it is counted off once.
                    if (_undeleted.Remove(p)) _undeletedBytes -= p.Length;
                }
    }

    // ── Clear, index, close ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes every picture and saves an empty index. A held pack keeps counting in <see cref="SizeBytes"/> until it
    /// can be deleted. Its pictures stay cleared across restarts. Does nothing once the store is closing. The files are
    /// deleted before it returns, but without holding up the writer: thousands of packs take a while.
    /// </summary>
    public void Clear()
    {
        if (!_enabled) return;
        List<Pack> packs;
        var others = new List<(int Number, string Path)>();
        lock (_io)
        {
            lock (_gate)
            {
                // A closing store has let go of the folder, or is about to: another store may own it now, and its files
                // are not this one's to delete.
                if (_closed || _stopping) return;
                // Clear is one more holder of the marker until its deletes are done, so the folder can't pass to another
                // store while it still deletes, even if Dispose gives up waiting for it.
                Interlocked.Increment(ref _markerHolders);
                _generation++;
                _queue.Clear();
                _pending.Clear();
                _queuedBytes = 0;
                _map.Clear();
                packs = [.. _packs.Values];
                _packs.Clear();
                Array.Clear(_currents);
                Array.Clear(_groupBytes);
                _total = 0;
                _indexDirty = true;
                _unflushed = false;
                _full = false;
            }
            try
            {
                // Packs left on disk by an earlier run that the store didn't load. Only their names here: who they belong
                // to is looked at below, outside the lock.
                HashSet<int> known;
                lock (_gate) known = [.. _undeleted.Select(p => p.Number), .. packs.Select(p => p.Number)];
                try
                {
                    foreach (var path in Directory.EnumerateFiles(_folder, PackPrefix + "*" + PackSuffix))
                    {
                        if (!TryParsePackName(Path.GetFileName(path), out int number)) continue;
                        _nextNumber = Math.Max(_nextNumber, number + 1);
                        if (!known.Contains(number)) others.Add((number, path));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                foreach (var name in new[] { IndexName, IndexTempName })
                {
                    string path = Path.Combine(_folder, name);
                    if (OwnFile(path) == true) TryDelete(path);
                }
                // Saved before the deletes: every pack below the floor is deleted at the next open, should this end
                // halfway. The writer makes new packs only from the floor up, so the deletes below never meet them.
                _packFloor = _nextNumber;
                SaveIndexLocked();
            }
            catch (Exception)
            {
                LetGoOfMarker(null);
                throw;
            }
        }
        try
        {
            _options.BeforeClearDeletes?.Invoke();
            foreach (var p in packs)
            {
                p.Handle?.Dispose();
                if (!TryDelete(p.Path)) KeepUndeleted(p);
            }
            RetryUndeleted();
            // Only the store's own (a file named like a pack without the store's mark is someone else's).
            foreach (var (number, path) in others)
            {
                if (OwnFile(path) != true) continue;
                long length;
                try { length = new FileInfo(path).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { length = 0; }
                if (!TryDelete(path)) KeepUndeleted(new Pack(number, path, length));
            }
        }
        finally { LetGoOfMarker(null); }
    }

    /// <summary>
    /// A pack whose file couldn't be deleted (another program holds it without delete sharing): it still takes its space,
    /// so it counts in the size and the cap, and the writer tries again until it goes.
    /// </summary>
    void KeepUndeleted(Pack p)
    {
        lock (_gate)
        {
            _undeleted.Add(p);
            _undeletedBytes += p.Length;
        }
    }

    /// <summary>Saves the index now (it is also saved every minute while it changes, and on close).</summary>
    public void SaveIndex()
    {
        if (!_enabled) return;
        lock (_io) if (!_closed) SaveIndexLocked();
    }

    /// <summary>
    /// Waits until everything queued so far is written (tests; the app never waits on the store). False when
    /// <paramref name="timeout"/> passed first or the store is closed.
    /// </summary>
    internal bool WaitUntilWritten(TimeSpan timeout)
    {
        if (!_enabled) return true;
        long until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            lock (_gate)
            {
                if (_closed) return false;
                if (_queue.Count == 0 && !_writing) return true;
            }
            if (Environment.TickCount64 > until) return false;
            _wake.Set();
            Thread.Sleep(2);
        }
    }

    /// <summary>
    /// How often a changed index may be saved: <paramref name="least"/> (a minute) while it is small, and so that writing
    /// it takes about <see cref="IndexBytesPerMinute"/> a minute at most once it is large.
    /// </summary>
    internal static TimeSpan IndexSaveIntervalFor(long indexBytes, TimeSpan least)
        => TimeSpan.FromMinutes(Math.Max(least.TotalMinutes, indexBytes / (double)IndexBytesPerMinute));

    /// <summary>
    /// The largest index.bin saved or read for a ceiling of <paramref name="maxRecords"/>: twice the records it may hold
    /// (the map passes the ceiling by a batch before the oldest packs go) and 64 MB for the pack list (5 million packs).
    /// </summary>
    internal static long MaxIndexBytesFor(int maxRecords) => (long)maxRecords * 2 * IndexEntryBytes + (64L << 20);

    /// <summary>The size of an index of <paramref name="records"/> records over <paramref name="packs"/> packs.</summary>
    internal static long IndexBytes(long records, long packs) => IndexHeaderBytes + packs * IndexPackBytes + records * IndexEntryBytes + 4;

    /// <summary>
    /// Writes the index to index.tmp, part by part, and moves it over index.bin. The map is copied one shard at a time under
    /// the store's lock and written outside it, so readers wait for one shard's copy, never for the disk. Under _io: the
    /// writer adds nothing meanwhile, and what Forget or ClearSuspect remove meanwhile is saved again next time.
    /// </summary>
    void SaveIndexLocked()
    {
        _lastIndexSave = Environment.TickCount64;
        int entries, floor;
        (int Number, long Verified)[] packs;
        lock (_gate)
        {
            _saveIndexSoon = false;
            // Changes from here on mark it again, for the next save.
            _indexDirty = false;
            entries = _map.Count;
            floor = _packFloor;
            packs = [.. _packs.Values.Select(p => (p.Number, p.Verified))];
        }
        long size = IndexBytes(entries, packs.Length);
        // More records than an index the store would read back (the record ceiling keeps far below it): the packs are
        // scanned at the next open instead.
        if (size > _maxIndexBytes) return;
        // The packs the index covers go to the disk before the index that trusts them.
        FlushCurrent();
        string temp = Path.Combine(_folder, IndexTempName), index = Path.Combine(_folder, IndexName);
        // The folder was checked at open, but a file may have arrived since: one that isn't the store's own (or can't be
        // read to tell) is never written over or deleted. The save waits; the index stays marked as changed.
        if (File.Exists(temp) && (OwnFile(temp) != true || !TryDelete(temp)))
        {
            lock (_gate) _indexDirty = true;
            return;
        }
        bool made = false;
        try
        {
            // CreateNew: a file that appeared after the look above makes this fail instead of being emptied.
            using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                made = true;
                uint crc = Crc32C.Start;
                var buffer = new byte[4096 * IndexEntryBytes];
                var s = buffer.AsSpan();
                IndexMagic.CopyTo(s);
                BinaryPrimitives.WriteUInt16LittleEndian(s[8..], IndexVersion);
                s[10] = s[11] = 0;
                BinaryPrimitives.WriteInt32LittleEndian(s[12..], packs.Length);
                BinaryPrimitives.WriteInt32LittleEndian(s[16..], entries);
                // Version 1 left these four bytes zero, which means no packs have been cleared.
                BinaryPrimitives.WriteInt32LittleEndian(s[20..], floor);
                crc = WritePart(f, crc, s[..IndexHeaderBytes]);
                foreach (var (number, verified) in packs)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(s, number);
                    // How far the pack is covered: the end of its last good record, so a damaged tail is looked at again.
                    BinaryPrimitives.WriteInt64LittleEndian(s[4..], verified);
                    crc = WritePart(f, crc, s[..IndexPackBytes]);
                }
                int written = 0;
                for (int i = 0; i < EntryMap.ShardCount && written < entries; i++)
                {
                    int at = 0;
                    lock (_gate)
                    {
                        var shard = _map.Shard(i);
                        int room = Math.Min(shard.Count, entries - written) * IndexEntryBytes;
                        if (buffer.Length < room) buffer = new byte[room];
                        s = buffer.AsSpan();
                        foreach (var (hash, e) in shard)
                        {
                            if (at == room) break;
                            BinaryPrimitives.WriteUInt64LittleEndian(s[at..], hash);
                            BinaryPrimitives.WriteUInt32LittleEndian(s[(at + 8)..], e.Check);
                            BinaryPrimitives.WriteInt32LittleEndian(s[(at + 12)..], e.Pack);
                            BinaryPrimitives.WriteInt64LittleEndian(s[(at + 16)..], e.Offset);
                            BinaryPrimitives.WriteInt32LittleEndian(s[(at + 24)..], e.Length);
                            s[at + 28] = (byte)e.Kind;
                            s[at + 29] = 0;
                            BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 30)..], e.Width);
                            BinaryPrimitives.WriteUInt16LittleEndian(s[(at + 32)..], e.Height);
                            s[at + 34] = s[at + 35] = 0;
                            at += IndexEntryBytes;
                        }
                    }
                    crc = WritePart(f, crc, s[..at]);
                    written += at / IndexEntryBytes;
                }
                // Records removed while the shards were copied leave their places as zeros, which no open takes (pack 0
                // isn't a pack), so the count in the header stays true.
                if (written < entries)
                {
                    s = buffer.AsSpan();
                    s.Clear();
                    for (int left = entries - written; left > 0;)
                    {
                        int n = Math.Min(left, buffer.Length / IndexEntryBytes);
                        crc = WritePart(f, crc, s[..(n * IndexEntryBytes)]);
                        left -= n;
                    }
                }
                BinaryPrimitives.WriteUInt32LittleEndian(s, Crc32C.Finish(crc));
                f.Write(s[..4]);
                f.Flush(flushToDisk: true);
            }
            // If the name was free, the move must still fail if another file takes it first.
            File.Move(temp, index, overwrite: OwnFile(index) == true);
            _indexSaveEveryMs = (long)IndexSaveIntervalFor(size, _options.IndexSaveInterval).TotalMilliseconds;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lock (_gate) _indexDirty = true;
            // Only the copy this save made; a file that beat it to the name is someone else's.
            if (made) TryDelete(temp);
        }
    }

    static uint WritePart(FileStream f, uint crc, ReadOnlySpan<byte> part)
    {
        f.Write(part);
        return Crc32C.Update(crc, part);
    }

    /// <summary>
    /// Quiets the marker and closes the store: the writer thread writes what is queued, saves the index and closes the
    /// files. Waits for it 2 s at most, then returns without touching the files (the writer finishes alone). Until the
    /// writer has let go, opening the same folder again gives <see cref="ThumbStoreProblem.InUse"/>.
    /// </summary>
    public void Dispose()
    {
        if (!_enabled)
        {
            Marker.Dispose();
            return;
        }
        Thread? writer;
        lock (_gate)
        {
            if (_closed || _stopping) return;
            Marker.Quiet();
            // Hand the folder to the writer before it can see the stop, so its last step releases the folder.
            writer = _writer is { IsAlive: true } ? _writer : null;
            if (writer != null) LetGoOfMarker(Marker.Dispose);
            _saveOnStop = true;
            _stopping = true;
        }
        if (writer != null)
        {
            _wake.Set();
            writer.Join(_options.CloseWait);
            return;
        }
        // The writer ended already (it never does while the store is open; belt and braces): close here, as before.
        if (Monitor.TryEnter(_io, TimeSpan.FromSeconds(1)))
        {
            try
            {
                FlushCurrent();
                if (_indexDirty) SaveIndexLocked();
                // A last try at packs Clear or the cap couldn't delete.
                RetryUndeleted();
                CloseFiles();
            }
            finally { Monitor.Exit(_io); }
        }
        else CloseFiles();
        // The writer has let go already: this closes the marker now.
        LetGoOfMarker(Marker.Dispose);
    }

    /// <summary>Waits for the writer to finish and release the folder. False when <paramref name="timeout"/> passes first.</summary>
    public bool WaitUntilClosed(TimeSpan timeout) => _writer == null || _writer.Join(timeout);

    /// <summary>Stops without saving the index or clearing the marker, as a process crash would (tests). Queued records are written first.</summary>
    internal void Abandon()
    {
        if (!_enabled) return;
        lock (_gate)
        {
            if (_closed || _stopping) return;
            _stopping = true;
        }
        _wake.Set();
        _writer?.Join(TimeSpan.FromSeconds(5));
        lock (_io) CloseFiles();
        LetGoOfMarker(Marker.Abandon);
    }

    void CloseFiles()
    {
        List<Pack> packs;
        lock (_gate)
        {
            _closed = true;
            packs = [.. _packs.Values];
            _pending.Clear();
            _queue.Clear();
        }
        foreach (var p in packs) p.Handle?.Dispose();
        _wake.Set();
    }

    // ── Opening ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Finds the packs, loads the index for what it covers and scans the rest. Under _io, before the writer starts.</summary>
    void Load()
    {
        var onDisk = new SortedDictionary<int, (long Length, bool ZeroHead, PackGroup Group)>();
        foreach (var path in Directory.EnumerateFiles(_folder, PackPrefix + "*" + PackSuffix))
        {
            if (!TryParsePackName(Path.GetFileName(path), out int number)) continue;
            // Reserve the number even for a foreign file. Only the store's own packs are counted or deleted.
            _nextNumber = Math.Max(_nextNumber, number + 1);
            // The pack's group comes with the look at its head that tells it is the store's own: no more reads at open.
            if (OwnFile(path, out bool zeroHead, out var group) != true) continue;
            try { onDisk[number] = (new FileInfo(path).Length, zeroHead, group); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        // A save the last session didn't finish; only when it is surely the store's own (one that can't be read stays).
        string temp = Path.Combine(_folder, IndexTempName);
        if (OwnFile(temp) == true) TryDelete(temp);
        var covered = LoadIndex(onDisk);
        _nextNumber = Math.Max(_nextNumber, _packFloor);
        // The newest pack of each group, and whether it ended cleanly.
        var newest = new (Pack Pack, bool Clean)?[GroupCount];
        foreach (var (number, file) in onDisk)
        {
            long length = file.Length;
            var pack = new Pack(number, Path.Combine(_folder, PackName(number)), length, file.Group);
            if (number < _packFloor)
            {
                if (!TryDelete(pack.Path)) KeepUndeleted(pack);
                continue;
            }
            long from = covered.TryGetValue(number, out long c) ? c : 0;
            long verified = length;
            bool clean = from >= length || Scan(pack, from, out verified);
            pack.Verified = clean ? length : verified;
            lock (_gate)
            {
                _packs[number] = pack;
                _total += length;
                _groupBytes[(int)pack.Group] += length;
            }
            newest[(int)pack.Group] = (pack, clean);
        }
        lock (_gate) RecountLive();
        // Each group goes on appending to its newest pack, only when that ended cleanly: records after a damaged one would
        // never be found by a scan. Earlier versions' packs are never appended to: new records go to a group's own packs.
        for (int g = 1; g < GroupCount; g++)
        {
            if (newest[g] is not { Clean: true } n || n.Pack.Length >= _options.PackBytes) continue;
            var last = n.Pack;
            try
            {
                last.Handle = File.OpenHandle(last.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                last.Writable = true;
                _currents[g] = last;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Loads index.bin into the map for packs that still exist and are at least as long as it says; returns how far each is
    /// covered. Read part by part (never the whole file at once); the records go into the map as they come and leave it
    /// again if the checksum at the end is wrong, so a damaged index costs a scan of the packs, as before. Under _io,
    /// before the packs are scanned (the map holds nothing else yet).
    /// </summary>
    Dictionary<int, long> LoadIndex(SortedDictionary<int, (long Length, bool ZeroHead, PackGroup Group)> onDisk)
    {
        var covered = new Dictionary<int, long>();
        bool filled = false;
        try
        {
            string path = Path.Combine(_folder, IndexName);
            if (!File.Exists(path)) return covered;
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            long fileLength = f.Length;
            if (fileLength < IndexHeaderBytes + 4 || fileLength > _maxIndexBytes) return covered;
            var head = new byte[IndexHeaderBytes];
            f.ReadExactly(head);
            var s = head.AsSpan();
            if (!s[..8].SequenceEqual(IndexMagic) || BinaryPrimitives.ReadUInt16LittleEndian(s[8..]) != IndexVersion) return covered;
            int packs = BinaryPrimitives.ReadInt32LittleEndian(s[12..]);
            int entries = BinaryPrimitives.ReadInt32LittleEndian(s[16..]);
            if (packs < 0 || entries < 0 || IndexBytes(entries, packs) != fileLength)
                return covered;
            uint crc = Crc32C.Update(Crc32C.Start, s);
            int floor = BinaryPrimitives.ReadInt32LittleEndian(s[20..]);
            int part = Math.Max(1, _options.IndexPartRecords);
            var buffer = new byte[Math.Max(part * IndexEntryBytes, IndexPackBytes * 1024)];
            for (int left = packs; left > 0;)
            {
                int n = Math.Min(left, buffer.Length / IndexPackBytes);
                var chunk = buffer.AsSpan(0, n * IndexPackBytes);
                f.ReadExactly(chunk);
                crc = Crc32C.Update(crc, chunk);
                for (int at = 0; at < chunk.Length; at += IndexPackBytes)
                {
                    int number = BinaryPrimitives.ReadInt32LittleEndian(chunk[at..]);
                    if (number < floor) continue;
                    long length = BinaryPrimitives.ReadInt64LittleEndian(chunk[(at + 4)..]);
                    // A shortened pack or one whose head was zeroed lost data the index describes. Scan it from the start.
                    if (onDisk.TryGetValue(number, out var file) && !file.ZeroHead && length >= 0 && length <= file.Length) covered[number] = length;
                }
                left -= n;
            }
            filled = true;
            lock (_gate) _map.Reserve(entries);
            for (int left = entries; left > 0;)
            {
                int n = Math.Min(left, buffer.Length / IndexEntryBytes);
                var chunk = buffer.AsSpan(0, n * IndexEntryBytes);
                f.ReadExactly(chunk);
                crc = Crc32C.Update(crc, chunk);
                lock (_gate)
                    for (int at = 0; at < chunk.Length; at += IndexEntryBytes)
                    {
                        ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(chunk[at..]);
                        int pack = BinaryPrimitives.ReadInt32LittleEndian(chunk[(at + 12)..]);
                        if (hash == 0 || !covered.TryGetValue(pack, out long upTo)) continue;
                        long offset = BinaryPrimitives.ReadInt64LittleEndian(chunk[(at + 16)..]);
                        int length = BinaryPrimitives.ReadInt32LittleEndian(chunk[(at + 24)..]);
                        var kind = (StoredKind)chunk[at + 28];
                        if (offset < 0 || offset > int.MaxValue || length < RecordHeaderBytes || length > RecordHeaderBytes + MaxPayloadBytes
                            || offset + length > upTo || kind is < StoredKind.Jpeg or > StoredKind.Suspect) continue;
                        _map.Set(hash, new Entry(pack, (int)offset, length, BinaryPrimitives.ReadUInt32LittleEndian(chunk[(at + 8)..]),
                            BinaryPrimitives.ReadUInt16LittleEndian(chunk[(at + 30)..]), BinaryPrimitives.ReadUInt16LittleEndian(chunk[(at + 32)..]), kind), out _);
                    }
                left -= n;
            }
            Span<byte> tail = stackalloc byte[4];
            f.ReadExactly(tail);
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail) != Crc32C.Finish(crc))
            {
                lock (_gate) _map.Clear();
                return [];
            }
            _packFloor = floor;
            IndexEntriesLoaded = entries;
            // A large index is saved less often from the start.
            _indexSaveEveryMs = (long)IndexSaveIntervalFor(fileLength, _options.IndexSaveInterval).TotalMilliseconds;
            return covered;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Cut short (EndOfStreamException is an IOException) or unreadable: as if there were no index.
            if (filled) lock (_gate) _map.Clear();
            return [];
        }
    }

    /// <summary>
    /// Reads a pack's records from <paramref name="from"/> into the map; a bad record ends the scan. True when it reached
    /// the end cleanly. <paramref name="verified"/> is where the last good record ends.
    /// </summary>
    bool Scan(Pack pack, long from, out long verified)
    {
        verified = from;
        long length = Math.Min(pack.Length - from, MaxScanBytes);
        if (length <= 0) return true;
        byte[] buffer = new byte[length];
        int n;
        try
        {
            using var handle = File.OpenHandle(pack.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            n = ReadFully(handle, buffer, from);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        int at = 0;
        bool dirty = false;
        lock (_gate)
        {
            while (at < n)
            {
                // No pack the store writes is that long; a record past an int's reach can't be kept in the map.
                if (from + at > int.MaxValue || !Parse(buffer, at, n, out var r)) break;
                _map.Set(r.Hash, new Entry(pack.Number, (int)(from + at), r.Length, r.Check, r.Width, r.Height, r.Kind), out _);
                at += r.Length;
                dirty = true;
            }
            if (dirty) _indexDirty = true;
        }
        verified = from + at;
        return at == n && n == pack.Length - from;
    }

    // ── Records ──────────────────────────────────────────────────────────────────────────────────────────────────────

    readonly record struct Record(ulong Hash, uint Check, StoredKind Kind, ushort Width, ushort Height, int PayloadLength)
    {
        public int Length => RecordHeaderBytes + PayloadLength;
    }

    // A record: magic (4) "RPTH", version (1), kind (1), group (1, PackGroup; 0 before this version), reserved (1),
    // hash (8), second hash (4), width (2), height (2), payload length (4), CRC-32C of the payload (4), then the payload.
    // Little-endian.
    static void Encode(Span<byte> s, Pending p)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(s, RecordMagic);
        s[4] = RecordVersion;
        s[5] = (byte)p.Kind;
        // Was reserved (zero) and is never read by 1.7.31 and earlier, which keeps their reading of these packs unchanged.
        s[6] = (byte)p.Group;
        s[7] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(s[8..], p.Hash);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], p.Check);
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], p.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], p.Height);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], p.Payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], Crc32C.Compute(p.Payload));
        p.Payload.CopyTo(s[RecordHeaderBytes..]);
    }

    /// <summary>Checks and reads the record at <paramref name="at"/> of the first <paramref name="end"/> bytes; lengths are never trusted past <paramref name="end"/>.</summary>
    static bool Parse(byte[] buffer, int at, int end, out Record record)
    {
        record = default;
        if (end - at < RecordHeaderBytes) return false;
        var s = buffer.AsSpan(at, end - at);
        if (BinaryPrimitives.ReadUInt32LittleEndian(s) != RecordMagic || s[4] != RecordVersion) return false;
        var kind = (StoredKind)s[5];
        if (kind is < StoredKind.Jpeg or > StoredKind.Suspect) return false;
        int payload = BinaryPrimitives.ReadInt32LittleEndian(s[24..]);
        if (payload < 0 || payload > MaxPayloadBytes || payload > s.Length - RecordHeaderBytes) return false;
        bool picture = kind is StoredKind.Jpeg or StoredKind.Png;
        if (picture != (payload > 0)) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(s[28..]) != Crc32C.Compute(s.Slice(RecordHeaderBytes, payload))) return false;
        ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(s[8..]);
        if (hash == 0) return false;
        record = new Record(hash, BinaryPrimitives.ReadUInt32LittleEndian(s[16..]), kind,
            BinaryPrimitives.ReadUInt16LittleEndian(s[20..]), BinaryPrimitives.ReadUInt16LittleEndian(s[22..]), payload);
        return true;
    }

    // ── Files ────────────────────────────────────────────────────────────────────────────────────────────────────────

    static string PackName(int number) => $"{PackPrefix}{number:D8}{PackSuffix}";

    static bool TryParsePackName(string name, out int number)
    {
        number = 0;
        if (name.Length != PackPrefix.Length + 8 + PackSuffix.Length || !name.StartsWith(PackPrefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(PackSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(name.AsSpan(PackPrefix.Length, 8), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out number) && number > 0;
    }

    static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>What <see cref="EnsurePrivateFolder"/> found.</summary>
    enum FolderAccess : byte { Private, NotPrivate, OtherAccount }

    static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier CreatorOwnerSid = new(WellKnownSidType.CreatorOwnerSid, null);
    static readonly SecurityIdentifier OwnerRightsSid = new("S-1-3-4");

    /// <summary>
    /// Makes the store's folder when it isn't there, readable and writable only by this user (with SYSTEM and
    /// administrators, so uninstall and backups still reach it): a place chosen on a second drive usually lets every
    /// account on the PC read, and the packs are plain pictures of the user's files. Windows keeps its own thumbnail cache
    /// per user in the same way. Then says whether the folder really is private. Once per open, never per thumbnail.
    /// Throws only when the folder can't be made (the place above it isn't there); permissions that can't be read or set
    /// only make the answer "not private".
    /// </summary>
    static FolderAccess EnsurePrivateFolder(string folder, ThumbStoreOptions options)
    {
        bool made = false;
        if (!Directory.Exists(folder))
        {
            // Only the store's own folder is made, never the folders above it: a chosen place that isn't there (a stick
            // with another drive's letter now, a folder renamed since) is a location that can't be used, not one to build
            // again, and the folders of the user's own tree keep the permissions they inherit.
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) throw new DirectoryNotFoundException("The folder the store goes in isn't there.");
            try
            {
                using var me = WindowsIdentity.GetCurrent();
                if (me.User is { } user) new DirectoryInfo(folder).Create(PrivateFolderSecurity(user));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                          or InvalidOperationException or System.Security.SecurityException or ArgumentException)
            {
                // No permissions on this drive, or they can't be set: the folder as Windows makes it.
            }
            Directory.CreateDirectory(folder);
            made = true;
        }
        try { return CheckPrivate(folder, made, options); }
        catch (Exception)
        {
            // Permissions that can't be read or set (a share that refuses it, an identity Windows can't look up) never
            // stop the store; they only mean it can't be called private.
            return FolderAccess.NotPrivate;
        }
    }

    /// <summary>
    /// Whether only this user (with SYSTEM and administrators) can read the folder and the store's files in it. A folder
    /// that lets others read is tightened only when it holds nothing but the store's own files. Another account's store
    /// is never written into. May throw.
    /// </summary>
    static FolderAccess CheckPrivate(string folder, bool made, ThumbStoreOptions options)
    {
        // FAT, exFAT and some network shares keep no permissions at all: nothing to set, and nothing private.
        if ((options.KeepsPermissions != null ? options.KeepsPermissions(folder) : VolumeKeepsPermissions(folder)) != true)
            return FolderAccess.NotPrivate;
        using var me = WindowsIdentity.GetCurrent();
        if (me.User is not { } user) return FolderAccess.NotPrivate;
        var dir = new DirectoryInfo(folder);
        var security = dir.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        // A folder found here, not made now, whose permissions say it is another account's store: that account could
        // read whatever this one wrote into it.
        if (!made && OtherAccountsFolder(security, user, me.Groups, options)) return FolderAccess.OtherAccount;
        if (!OnlyPrivateReaders(security.GetAccessRules(true, true, typeof(SecurityIdentifier)), user))
        {
            // The program changes permissions only on what is its own: a folder holding anything else keeps them.
            if (!HoldsOnlyStoreFiles(folder)) return FolderAccess.NotPrivate;
            // Setting a folder's permissions passes them on to the files inside that inherit them.
            dir.SetAccessControl(PrivateFolderSecurity(user));
            if (!OnlyPrivateReaders(dir.GetAccessControl(AccessControlSections.Access).GetAccessRules(true, true, typeof(SecurityIdentifier)), user))
                return FolderAccess.NotPrivate;
        }
        else if (security.AreAccessRulesProtected) return FolderAccess.Private;
        // Normally files inherit from a protected private folder. Check their permissions individually only when
        // the folder has just been tightened or it still takes permissions from its parent.
        bool allPrivate = true;
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            if (!IsStoreFileName(Path.GetFileName(path))) continue;
            var file = new FileInfo(path);
            if (OnlyPrivateReaders(file.GetAccessControl(AccessControlSections.Access).GetAccessRules(true, true, typeof(SecurityIdentifier)), user)) continue;
            if (OwnFile(path) != true) { allPrivate = false; continue; }
            var own = file.GetAccessControl(AccessControlSections.Access);
            own.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in own.GetAccessRules(true, false, typeof(SecurityIdentifier))) own.RemoveAccessRuleSpecific(rule);
            file.SetAccessControl(own);
            if (!OnlyPrivateReaders(file.GetAccessControl(AccessControlSections.Access).GetAccessRules(true, true, typeof(SecurityIdentifier)), user))
                allPrivate = false;
        }
        // A private folder that takes its permissions from above would have every file looked at again at each open
        // (thousands of packs in a large store). Holding only the store's files, it keeps the same private permissions
        // as its own from now on, so the next opens stop at the check above.
        if (allPrivate && !dir.GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected)
        {
            try { if (HoldsOnlyStoreFiles(folder)) dir.SetAccessControl(PrivateFolderSecurity(user)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                          or System.Security.SecurityException or ArgumentException) { }
        }
        return allPrivate ? FolderAccess.Private : FolderAccess.NotPrivate;
    }

    /// <summary>The store folder's permissions: full control for this user, SYSTEM and administrators only, passed on to everything inside, nothing inherited from above.</summary>
    static DirectorySecurity PrivateFolderSecurity(SecurityIdentifier user)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var who in new[] { user, SystemSid, AdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Whether every permission that lets someone read goes to this user, SYSTEM, administrators, or the creator or owner
    /// (who is this user for what the store makes). Permissions only passed on to what is made inside count too: the
    /// packs made later get them.
    /// </summary>
    static bool OnlyPrivateReaders(AuthorizationRuleCollection rules, SecurityIdentifier user)
    {
        // ReadData (ListDirectory on a folder), GENERIC_ALL and GENERIC_READ.
        const int Reads = 0x1 | 0x10000000 | unchecked((int)0x80000000);
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || ((int)rule.FileSystemRights & Reads) == 0) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid) return false;
            if (!(sid == user || sid == SystemSid || sid == AdministratorsSid || sid == CreatorOwnerSid || sid == OwnerRightsSid)) return false;
        }
        return true;
    }

    /// <summary>
    /// Whether the folder is another Windows account's: full control given by the folder itself (not inherited from the
    /// place above, which says nothing about whose this folder is) to a user account other than this one, or an owner
    /// that is such an account with full control. Groups this user is in, and deleted local accounts, don't count.
    /// </summary>
    static bool OtherAccountsFolder(DirectorySecurity security, SecurityIdentifier user, IdentityReferenceCollection? groups, ThumbStoreOptions options)
    {
        bool Foreign(SecurityIdentifier sid)
        {
            if (sid == user || !(sid.IsAccountSid() || sid.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal))) return false;
            if (groups != null)
                foreach (var g in groups)
                    if (g is SecurityIdentifier gs && gs == sid) return false;
            return true;
        }
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid) continue;
            bool full = (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl || ((int)rule.FileSystemRights & 0x10000000) != 0;
            if (!full || !Foreign(sid) || (rule.IsInherited && sid != owner)) continue;
            if (AccountExists(sid, options)) return true;
        }
        return false;
    }

    /// <summary>Whether Windows knows the account; one it can't ask about (a domain out of reach) counts as existing, so its folder is left alone.</summary>
    static bool AccountExists(SecurityIdentifier sid, ThumbStoreOptions options)
    {
        try
        {
            if (options.AccountExists != null) return options.AccountExists(sid);
            sid.Translate(typeof(NTAccount));
            return true;
        }
        // Only this PC can say for sure that one of its accounts is gone. Another domain might just be offline.
        catch (IdentityNotMappedException) when (sid.AccountDomainSid is { } domain && domain == LocalAccountDomain()) { return false; }
        catch (Exception) { return true; }
    }

    internal static SecurityIdentifier? LocalAccountDomain()
    {
        // Ask the local account database, even when the signed-in user belongs to another domain.
        if (NetUserModalsGet(null, 2, out var info) != 0) return null;
        // USER_MODALS_INFO_2 contains two pointers: the domain name followed by its SID.
        try { return new SecurityIdentifier(Marshal.ReadIntPtr(info, IntPtr.Size)); }
        finally { NetApiBufferFree(info); }
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int NetUserModalsGet(string? server, int level, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>Whether the folder holds nothing, or only files that are surely the store's own (named so and carrying its mark).</summary>
    static bool HoldsOnlyStoreFiles(string folder)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            if (Directory.Exists(path) || !IsStoreFileName(Path.GetFileName(path)) || OwnFile(path) != true) return false;
        return true;
    }

    /// <summary>
    /// Whether the folder's drive keeps permissions (FILE_PERSISTENT_ACLS), asked of the volume through a handle so a
    /// network path (\\server\share) is answered too. Null when it can't be asked.
    /// </summary>
    static bool? VolumeKeepsPermissions(string folder)
    {
        // FILE_READ_ATTRIBUTES with FILE_FLAG_BACKUP_SEMANTICS opens a folder without locking anything.
        using var h = CreateFileW(folder, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (h.IsInvalid) return null;
        if (!GetVolumeInformationByHandleW(h, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, out uint flags, IntPtr.Zero, 0)) return null;
        return (flags & 0x8) != 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, IntPtr volumeName, uint volumeNameSize, IntPtr serial,
        IntPtr maxComponentLength, out uint fileSystemFlags, IntPtr fileSystemName, uint fileSystemNameSize);

    /// <summary>Whether a folder holds a thumbnail store (packs, its index or its marker, each carrying the store's own mark).</summary>
    public static bool HoldsStore(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            foreach (var path in Directory.EnumerateFiles(folder))
                if (IsStoreFileName(Path.GetFileName(path)) && OwnFile(path) != false) return true;
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>
    /// Whether a file named like one of the store's really is the store's: empty, zero-filled after a crash, or starting
    /// with the store's mark for that file (a pack's first record, the index's or the marker's header). The names alone
    /// prove nothing: "index.bin" is a name anyone may use. Null when there is no such file or it can't be read.
    /// </summary>
    static bool? OwnFile(string path) => OwnFile(path, out _);

    static bool? OwnFile(string path, out bool zeroHead) => OwnFile(path, out zeroHead, out _);

    /// <summary><see cref="OwnFile(string)"/>, and for a pack the group its first record names (<see cref="PackGroup.Earlier"/> when it names none).</summary>
    static bool? OwnFile(string path, out bool zeroHead, out PackGroup group)
    {
        zeroHead = false;
        group = PackGroup.Earlier;
        string name = Path.GetFileName(path);
        bool marker = name.Equals(MarkerName, StringComparison.OrdinalIgnoreCase);
        bool index = name.Equals(IndexName, StringComparison.OrdinalIgnoreCase) || name.Equals(IndexTempName, StringComparison.OrdinalIgnoreCase);
        bool pack = !marker && !index;
        ReadOnlySpan<byte> mark = marker ? ThumbMarker.Magic : index ? IndexMagic : "RPTH"u8; // a pack's first record
        Span<byte> head = stackalloc byte[8];
        // A pack's head is read as far as its group byte; only the mark decides whether it is the store's own.
        int want = pack ? 7 : mark.Length, n = 0;
        try
        {
            if (!File.Exists(path)) return null;
            using var f = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            while (n < want)
            {
                int got = RandomAccess.Read(f, head[n..want], n);
                if (got <= 0) break;
                n += got;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        int m = Math.Min(n, mark.Length);
        // A crash can leave the file at its new length before any of the header reaches disk.
        zeroHead = m > 0 && !head[..m].ContainsAnyExcept((byte)0);
        bool own = zeroHead || head[..m].SequenceEqual(mark[..m]);
        if (own && pack && n >= 7 && m == mark.Length && !zeroHead && head[4] == RecordVersion && head[6] < GroupCount) group = (PackGroup)head[6];
        return own;
    }

    /// <summary>Whether a file name is one of the store's own files (a pack, the index, its temporary copy, the marker).</summary>
    public static bool IsStoreFileName(string name)
        => name.Equals(IndexName, StringComparison.OrdinalIgnoreCase) || name.Equals(IndexTempName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(MarkerName, StringComparison.OrdinalIgnoreCase) || TryParsePackName(name, out _);

    /// <summary>
    /// Removes a store's folder (the setting turned off, a new location chosen, uninstall), only when it holds nothing
    /// but the store's own files. A folder that holds anything of anyone else's keeps it and stays: only the store's own
    /// files in it are deleted (<see cref="StoreRemoval.LeftAlone"/>). Close the store first: an open store's files can't
    /// be deleted (<see cref="StoreRemoval.Failed"/>). Safe to repeat. Never throws.
    /// </summary>
    public static StoreRemoval RemoveFolder(string folder)
    {
        try
        {
            // A folder with none of the store's files was never a store (an empty folder of the user's that happens to
            // have the name): not Rock Preview's to delete.
            if (string.IsNullOrWhiteSpace(folder) || !HoldsStore(folder)) return StoreRemoval.NotThere;
            var entries = Directory.GetFileSystemEntries(folder);
            // Look at everything before deleting anything: a file is the store's only when its contents say so, and a
            // folder that holds anything else stays, with everything that isn't the store's.
            var own = new List<string>(entries.Length);
            bool foreign = false, unreadable = false;
            foreach (var path in entries)
            {
                if (Directory.Exists(path) || !IsStoreFileName(Path.GetFileName(path))) { foreign = true; continue; }
                switch (OwnFile(path))
                {
                    case true: own.Add(path); break;
                    case false: foreign = true; break;
                    default: unreadable = true; break;
                }
            }
            // A store open in this or another process holds its marker without sharing writes. Its files could still be
            // deleted under it (they are opened to allow that), so the marker is taken first: while it is held here no
            // store can open the folder either.
            string marker = Path.Combine(folder, MarkerName);
            FileStream? held = null;
            if (own.Exists(p => p.Equals(marker, StringComparison.OrdinalIgnoreCase)))
            {
                try { held = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete); }
                catch (IOException) { return StoreRemoval.Failed; }
            }
            bool clean = !unreadable;
            using (held)
                foreach (var path in own)
                    if (!path.Equals(marker, StringComparison.OrdinalIgnoreCase)) clean &= TryDelete(path);
            if (held != null) clean &= TryDelete(marker);
            if (!clean) return StoreRemoval.Failed;
            if (foreign) return StoreRemoval.LeftAlone;
            // Something may have arrived since the look: the folder only goes when it is empty (Directory.Delete
            // without recursion refuses otherwise).
            Directory.Delete(folder, recursive: false);
            return StoreRemoval.Removed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return StoreRemoval.Failed;
        }
    }

    /// <summary>
    /// Where Windows really puts <paramref name="folder"/>: the same folder reached through a junction, a subst drive, a
    /// short name or a mapped drive gives the same answer (a mapped drive gives its share, \\server\share\...). A folder
    /// that isn't there yet is resolved through the nearest one above it that is. The full path as given when nothing
    /// can be resolved. Touches the disk: off the UI thread. Never throws.
    /// </summary>
    public static string FinalPath(string folder)
    {
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            string rest = "";
            string? at = full;
            while (!string.IsNullOrEmpty(at) && !Directory.Exists(at))
            {
                rest = Path.Combine(Path.GetFileName(at), rest);
                at = Path.GetDirectoryName(at);
            }
            if (string.IsNullOrEmpty(at)) return full;
            // FILE_FLAG_BACKUP_SEMANTICS opens a folder; no access is asked for, so nothing is locked.
            using var h = CreateFileW(at, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (h.IsInvalid) return full;
            var buffer = new char[1024];
            uint n = GetFinalPathNameByHandleW(h, buffer, (uint)buffer.Length, 0);
            if (n == 0 || n >= buffer.Length) return full;
            string real = new(buffer, 0, (int)n);
            real = real.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + real[8..]
                 : real.StartsWith(@"\\?\", StringComparison.Ordinal) ? real[4..] : real;
            return Path.TrimEndingDirectorySeparator(rest.Length > 0 ? Path.Combine(real, rest) : real);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return folder;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, char[] buffer, uint length, uint flags);

    /// <summary>
    /// What a store folder's own files take on disk, in bytes, without opening it (0 when there is no such folder or it
    /// can't be read): the "123 MB of 500 MB" in Settings, and how much turning the setting off frees. Any thread.
    /// </summary>
    public static long MeasureFolder(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
            long total = 0;
            foreach (var info in new DirectoryInfo(folder).EnumerateFiles())
                if (IsStoreFileName(info.Name) && OwnFileSeen(info)) total += info.Length;
            return total;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    // What MeasureFolder found each file to be, by its name, times and length (from the folder listing, no file opened).
    // Settings measures every 2 seconds in some states: thousands of packs are then opened once, not at every look.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Created, DateTime Written, long Length, bool Own)> OwnSeen
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the file is the store's own, looked at again only when its times or length changed since the last look.</summary>
    static bool OwnFileSeen(FileInfo info)
    {
        var stamp = (info.CreationTimeUtc, info.LastWriteTimeUtc, info.Length);
        if (OwnSeen.TryGetValue(info.FullName, out var seen) && (seen.Created, seen.Written, seen.Length) == stamp) return seen.Own;
        bool? own = OwnFile(info.FullName);
        // A file that couldn't be read is looked at again next time.
        if (own == null) return false;
        if (OwnSeen.Count > 100_000) OwnSeen.Clear();
        OwnSeen[info.FullName] = (stamp.CreationTimeUtc, stamp.LastWriteTimeUtc, stamp.Length, own.Value);
        return own.Value;
    }
}

namespace Anything.Core.Content.Store;

/// <summary>A document's place in one store view: segment ordinal and local document number.</summary>
internal readonly record struct DocRef(int Segment, int Doc);

/// <summary>
/// Per-document flags in the row. <see cref="HasText"/> is the store's own: Add sets it when the stored body has a word
/// (<see cref="ContentTokenizer.HasWords"/>), whatever the caller passed; AddCopy and merges keep the source row's bit.
/// content: with no value matches Ok and Truncated rows that have it, without decompressing anything.
/// </summary>
[Flags] internal enum DocFlags : byte { None = 0, Truncated = 1, HasPicture = 2, FromOcr = 4, NeedsOcr = 8, EncryptedOptIn = 16, Attempt1 = 32, Attempt2 = 64, HasText = 128 }

/// <summary>One stored document's row (48 bytes on disk).</summary>
internal readonly record struct DocRow(ulong Key, long Modified, long Size, ushort Volume, ushort Pages, ContentKind Kind,
    DocStatus Status, byte ExtractorVersion, DocFlags Flags);

/// <summary>
/// What the store keeps of a document's words: body, fields, picture details. The body is ExtractedDoc.Body: '\n' between
/// lines, '\f' between pages and slides; in a workbook (xlsx, ods) every sheet's page starts with the sheet's name on its
/// own line, and the first sheet opens the body (no '\f' before it).
/// </summary>
internal sealed record StoredDoc(string Body, IReadOnlyList<(DocField Field, string Value)> Fields, PictureInfo? Picture);

/// <summary>
/// Per-kind totals of the live documents of a segment or a commit (the manifest's STATS; estimator, status, Settings).
/// Docs, WithText, RawTextBytes, StoredBytes and Problems count live documents exactly. Rows don't hold Pairs (distinct
/// terms), BytesRead and MsTotal, so deletes, re-key copies and merges carry those at the kind's average per document:
/// the estimator only uses averages per document (once a kind has 200 or more documents).
/// </summary>
/// <param name="WithText">Documents whose body has words (DocFlags.HasText).</param>
/// <param name="RawTextBytes">UTF-8 bytes of the stored-text blobs before compression.</param>
/// <param name="StoredBytes">Compressed stored-text bytes (TEXT).</param>
/// <param name="Pairs">(term, document) pairs, field-scoped terms included.</param>
/// <param name="BytesRead">Bytes read from the files (ExtractedDoc.BytesRead, passed to IStoreBatch.Add).</param>
/// <param name="MsTotal">Milliseconds spent reading them (ExtractedDoc.ElapsedMs).</param>
/// <param name="Problems">Documents with a problem status (not Ok, Truncated or NoText, nor left out by rule).</param>
internal readonly record struct KindStats(long Docs, long WithText, long RawTextBytes, long StoredBytes, long Pairs, long BytesRead, long MsTotal, long Problems);

/// <summary>Where a term's documents are: inline (docFreq ≤ 2) or at an offset in the postings section.</summary>
internal readonly record struct TermPostings(int DocFreq, long Offset, int Inline0, int Inline1);

/// <summary>Called per term in byte order; return false to stop.</summary>
internal delegate bool TermVisitor(ReadOnlySpan<byte> term, in TermPostings postings);

/// <summary>A volume of the store's volume table.</summary>
internal sealed record VolumeEntry(ushort Index, string Key, string DisplayRoot, DateTime LastSeenUtc);

/// <summary>One immutable segment as searches and the indexer read it; every member is safe from many threads.</summary>
internal interface IContentSegment
{
    /// <summary>Stable segment id (file name); never reused.</summary>
    uint Id { get; }
    int DocCount { get; }
    /// <summary>The tokenizer version its terms were made with.</summary>
    int TokenizerVersion { get; }
    /// <summary>
    /// When the segment was written (FILETIME UTC, from its header). Every document in it was read at or before this time,
    /// so the daily AccessDenied retry compares against it; a merge writes a new segment with a new time.
    /// </summary>
    long CreatedUtc { get; }
    DocRow Row(int doc);
    /// <summary>Local document with this key, or -1 (binary search over the key-sorted rows).</summary>
    int Find(ulong key);
    /// <summary>Visits terms in [from, to) in byte order (folded UTF-8); an empty <paramref name="to"/> means no upper bound; polls ct.</summary>
    void Terms(ReadOnlySpan<byte> from, ReadOnlySpan<byte> to, TermVisitor visit, CancellationToken ct);
    /// <summary>Exact term lookup.</summary>
    bool TryGetTerm(ReadOnlySpan<byte> term, out TermPostings postings);
    /// <summary>Adds a term's documents for this segment's ordinal to a builder; polls ct.</summary>
    void ReadPostings(in TermPostings postings, int segmentOrdinal, DocSetBuilder into, CancellationToken ct);
    /// <summary>Decompressed stored text; null when damaged.</summary>
    StoredDoc? ReadStored(int doc);
    /// <summary>The compressed stored-text bytes as written (merges and re-keys copy them).</summary>
    ReadOnlyMemory<byte> ReadStoredRaw(int doc);
    /// <summary>The volume-relative path; null when damaged.</summary>
    string? ReadPath(int doc);
    /// <summary>Documents with picture details.</summary>
    int PictureCount { get; }
    /// <summary>The i-th picture record, ascending by document.</summary>
    (int Doc, PictureInfo Info) Picture(int i);
    /// <summary>Picture details of one document.</summary>
    bool TryGetPicture(int doc, out PictureInfo info);
}

/// <summary>
/// An immutable, ref-counted view of the whole store at one commit. Whoever gets one holds a reference; every successful
/// AddRef or TryAddRef is paired with one Dispose, and segments stay mapped until the last reference goes.
/// </summary>
internal interface IStoreView : IDisposable
{
    /// <summary>
    /// Commit generation of this view: unique within the process and growing with every commit; a view opened from disk has
    /// at least its manifest's generation.
    /// </summary>
    long CommitGeneration { get; }
    IReadOnlyList<IContentSegment> Segments { get; }
    IReadOnlyList<VolumeEntry> Volumes { get; }
    bool IsDeleted(DocRef doc);
    /// <summary>The live document for a key (newest segment wins), or null.</summary>
    DocRef? Find(ulong key);
    /// <summary>Live rows in key order across segments (k-way merge), for reconcile.</summary>
    IEnumerable<(DocRef Ref, DocRow Row)> RowsByKey();
    /// <summary>Per-kind totals of this commit's live documents (the manifest's STATS); kinds with no documents are absent.</summary>
    IReadOnlyDictionary<ContentKind, KindStats> Stats { get; }
    /// <summary>Another reference to this view; the holder disposes it. Throws when the last reference is gone.</summary>
    IStoreView AddRef();
    /// <summary>Adds a reference unless the last one is gone (a CAS loop that refuses at 0); then read a newer view.</summary>
    bool TryAddRef();
}

/// <summary>Documents added and deleted together; searchable only after Commit.</summary>
internal interface IStoreBatch : IDisposable
{
    int Count { get; }
    /// <summary>Raw UTF-8 bytes of stored text buffered.</summary>
    long RawBytes { get; }
    /// <summary>
    /// Adds a document in any order (the batch sorts by key before writing; terms = ContentTokenizer over doc; a live
    /// document with the same key is deleted at commit); returns its position in the batch. bytesRead and elapsedMs are
    /// what reading it cost (ExtractedDoc.BytesRead and ElapsedMs), summed into the kind's STATS for the estimator.
    /// </summary>
    int Add(in DocRow row, string relativePath, StoredDoc doc, long bytesRead = 0, int elapsedMs = 0);
    /// <summary>
    /// Adds a copy of a live document of the batch's view under a new row and path (a re-key after a rename or move): its
    /// compressed text, picture record and terms source pass through unchanged, never compressed again. Returns its
    /// position in the batch (counted with Add). A commit whose copies cover 30% or more of a small segment's live
    /// documents rewrites that segment in the same commit (the change then carries Remaps); for a large segment,
    /// <see cref="IContentStore.Rekey"/> does that between batches.
    /// </summary>
    int AddCopy(DocRef source, in DocRow row, string relativePath);
    /// <summary>Deletes a live document of the view the batch began from.</summary>
    void Delete(DocRef doc);
    /// <summary>Writes the segment and the manifest (temp, flush, rename) and returns the change; deletes alone write only the manifest.</summary>
    StoreChange Commit();
}

/// <summary>Segments chosen for one merge.</summary>
internal sealed record MergePlan(IReadOnlyList<uint> Segments, int Docs, bool Large);

/// <summary>
/// What a commit, merge or re-key changed, so id maps follow without a reconcile. <see cref="View"/> is the receiver's own
/// reference (it adopts it, disposes the view it replaces, and disposes this one when done).
/// </summary>
/// <param name="View">The view after the change; one reference owned by whoever got the change.</param>
/// <param name="NewSegment">The segment written; null when nothing was written (deletes only, or a merge whose inputs were all deleted).</param>
/// <param name="LocalOfAdded">
/// Local document number in <paramref name="NewSegment"/> of each added document, in Add/AddCopy (or move) order; -1 for an
/// addition that didn't survive (the earlier of two additions of one key, a copy whose source text was damaged, a move
/// whose document was gone).
/// </param>
/// <param name="Remaps">
/// Old-to-new local numbers of segments rewritten into <paramref name="NewSegment"/> (-1 = dropped): a merge, a re-key, or a
/// commit that rewrote a segment its copies mostly moved. Apply them first (DocIdMap.Builder.Merge, which replaces the new
/// segment's entry), then bind LocalOfAdded.
/// </param>
/// <param name="DroppedSegments">Segments gone from the view (merged, rewritten, damaged, or with no live document left).</param>
internal sealed record StoreChange(IStoreView View, uint? NewSegment, int[] LocalOfAdded,
    IReadOnlyList<(uint OldSegment, int[] OldToNew)> Remaps, IReadOnlyList<uint> DroppedSegments);

/// <summary>
/// The store as the indexer writes it (ContentStore implements it; tests fake it). One writer thread, and at most one batch
/// open at a time: Merge and Rekey throw while one is, and DeleteAll invalidates it.
/// </summary>
internal interface IContentStore : IDisposable
{
    /// <summary>
    /// Opened for search only, made by a newer version, or the writer couldn't have the folder for now (another writer holds
    /// writer.lock, or a file was unavailable): writes throw.
    /// </summary>
    bool IsReadOnly { get; }
    /// <summary>The manifest was written by a newer version: read-only and off ("Rebuild text index to use it here"); DeleteAll still works.</summary>
    bool IsNewerFormat { get; }
    /// <summary>
    /// Some file was in use without read sharing, denied, or on a share that didn't answer: the store opened read-only for
    /// this session and wrote nothing. Open the folder again later.
    /// </summary>
    bool IsUnavailable { get; }
    /// <summary>A segment failed its checks or is gone: an otherwise empty commit drops it, and a reconcile reads its files again.</summary>
    bool HasDamage { get; }
    /// <summary>
    /// The idle scrub: verifies one not yet verified segment's sections against their CRCs per call (a failure sets
    /// HasDamage); true once every segment is verified.
    /// </summary>
    bool Scrub(CancellationToken ct);
    long BytesOnDisk { get; }
    /// <summary>The current view (the caller disposes it).</summary>
    IStoreView Acquire();
    /// <summary>The mode recorded in the manifest (what the index was built for); NamesOnly when none is recorded.</summary>
    ContentMode RecordedMode { get; }
    /// <summary>Records the mode the index is built for; persisted with the next commit's manifest.</summary>
    void RecordMode(ContentMode mode);
    /// <summary>The index of a volume key, added when new (persisted at the next commit).</summary>
    ushort VolumeIndex(string volumeKey, string displayRoot);
    /// <summary>Records that a volume was seen (90-day purge of unseen volumes).</summary>
    void SeenVolume(ushort volume, DateTime utc);
    IStoreBatch BeginBatch();
    /// <summary>The next merge by the tiered policy; null when none is due (allowLarge = idle for 2 minutes and on mains power).</summary>
    MergePlan? PlanMerge(bool allowLarge);
    /// <summary>
    /// Rebuilds the segments from stored text into one and commits. <paramref name="pace"/> runs between chunks of work (the
    /// follower waits there for its duty cycle, so a merge pauses instead of being thrown away).
    /// </summary>
    StoreChange Merge(MergePlan plan, CancellationToken ct, Action<CancellationToken>? pace = null);
    /// <summary>
    /// Rewrites one segment with some of its documents under new keys and paths (a big re-key after a folder rename), the
    /// stored text copied unchanged, and commits: for moves of 30% or more of a large segment's live documents (fewer, or a
    /// small segment, go through IStoreBatch.AddCopy). Runs between batches under ct and pace like a merge. Null when it
    /// can't run now: the segment is gone or damaged, it is large and allowLarge is false, or there isn't room on disk.
    /// LocalOfAdded follows the moves (-1 for a document gone since); Remaps maps the documents that stayed.
    /// </summary>
    StoreChange? Rekey(uint segment, IReadOnlyList<(int Doc, DocRow Row, string Path)> moves, bool allowLarge, CancellationToken ct,
        Action<CancellationToken>? pace = null);
    /// <summary>
    /// Stops publishing, waits a bounded time (up to 2 s, so call it on the follower's thread) for leases to go, then
    /// deletes every segment and the manifest. False when some files are still in use: call <see cref="FinishDeleting"/>
    /// later (the follower's timer does; Settings says "finishing deleting…"). Calling DeleteAll again instead also deletes
    /// whatever was committed since. Leaves an empty manifest behind: judge whether an index is "still here" by its live
    /// documents, never by the manifest file.
    /// </summary>
    bool DeleteAll();
    /// <summary>Retries only what DeleteAll couldn't delete (never anything committed since); true when nothing is left.</summary>
    bool FinishDeleting();
}

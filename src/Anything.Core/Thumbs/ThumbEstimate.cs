using System.Diagnostics;
using Anything.Core.Content.Indexing;
using Anything.Core.Search;

namespace Anything.Core.Thumbs;

// How to use the estimate (Settings' "Instant thumbnails" and the one-time recommendation):
//
//   Only once indexing is complete (before that Settings says "The space this needs is worked out when indexing has
//   finished."), on a background thread:
//       var census = ThumbEstimator.CountIndex(index, ct);        // read-lock slices, like ContentEstimator.CountIndex
//       long bytes = census.BytesAt(sizeClass);                    // files of each kind × the measured averages at the
//                                                                  // size class of the grid's thumbnail size now
//   census.Files is the "123,456 pictures, videos and documents"; census.Complete is false when the index was compacted
//   three times during the walk (then count again later). Count again when ThumbEstimator.ChangedMuch(census.LiveAtCount,
//   index.LiveCount) says the index has changed much since. The counts don't depend on the thumbnail size: a change of
//   size class changes BytesAt, not the census.

/// <summary>
/// How many indexed files of each kind would get a stored thumbnail, and what they would take. Immutable.
/// <see cref="IndexedFiles"/> counts the files walked (not folders); <see cref="LiveAtCount"/> is the index's own live
/// count (folders included) when the walk began, the number to compare the index with later.
/// </summary>
public readonly record struct ThumbCensus(long Photos, long Videos, long Documents, long IndexedFiles, bool Complete, long LiveAtCount = 0)
{
    /// <summary>Files that would get a stored picture: pictures, videos and documents (text pages are never stored).</summary>
    public long Files => Photos + Videos + Documents;

    /// <summary>What thumbnails for all of them would take at <paramref name="sizeClass"/> (256 or 512, or a display size), in bytes.</summary>
    public long BytesAt(int sizeClass) => ThumbEstimator.BytesFor(Photos, Videos, Documents, sizeClass);
}

/// <summary>
/// The space "Instant thumbnails" would need for the files found on this PC: the number of indexed files of each kind
/// (the table of kinds, <see cref="ThumbKinds.CostOf"/>) times the measured average size of a stored thumbnail at the
/// size class in use. The arithmetic is pure; <see cref="CountIndex"/> walks a live FileIndex safely from a background thread.
/// </summary>
public static class ThumbEstimator
{
    // What a stored thumbnail takes on average, measured by filling stores through the real pipeline (made files, and
    // the counts and averages of real ones) in November 2026. Class 256: photos 11.5 to 13.7 KB, video frames 6.6 KB,
    // flat kinds (documents, PDF, fonts, flat pictures) 8.8 to 18.6 KB. Class 512 takes about three times as much (photos
    // 34 to 46 KB, video 19 KB, flat kinds 25 to 58 KB), so each class has its own figures. Decimal, like every size
    // Settings shows (1 KB = 1,000 bytes).

    /// <summary>A stored photo thumbnail on average at class 256 (JPEG 85).</summary>
    public const long PhotoBytesSmall = 13_000;
    /// <summary>A stored video frame on average at class 256 (JPEG 85).</summary>
    public const long VideoBytesSmall = 7_000;
    /// <summary>A stored flat thumbnail on average at class 256 (documents, fonts, vector, flat pictures: PNG, or JPEG 90 when the PNG is big).</summary>
    public const long FlatBytesSmall = 17_000;
    /// <summary>A stored photo thumbnail on average at class 512.</summary>
    public const long PhotoBytesLarge = 40_000;
    /// <summary>A stored video frame on average at class 512.</summary>
    public const long VideoBytesLarge = 19_000;
    /// <summary>A stored flat thumbnail on average at class 512.</summary>
    public const long FlatBytesLarge = 50_000;

    /// <summary>The index has changed much when its live files differ from the counted ones by this share or more ...</summary>
    public const double ChangedShare = 0.10;
    /// <summary>... or by this many files.</summary>
    public const long ChangedFiles = 20_000;

    /// <summary>
    /// Bytes for this many files of each kind at <paramref name="sizeClass"/> (a display size works too: it is turned into
    /// its class): at class 256 photos 13 KB, video 7 KB and documents 17 KB each; at class 512 40, 19 and 50 KB.
    /// </summary>
    public static long BytesFor(long photos, long videos, long documents, int sizeClass)
    {
        bool large = ThumbSizeClass.For(sizeClass) == ThumbSizeClass.Large;
        return Math.Max(0, photos) * (large ? PhotoBytesLarge : PhotoBytesSmall)
             + Math.Max(0, videos) * (large ? VideoBytesLarge : VideoBytesSmall)
             + Math.Max(0, documents) * (large ? FlatBytesLarge : FlatBytesSmall);
    }

    /// <summary>
    /// The census of a list of file names (tests, the demo): each counted by its kind; with <paramref name="only"/> (the
    /// setting "Everyday files only"), names it leaves out count nothing, since the thumbnail views never show them; with
    /// <paramref name="keep"/> (the user's choice of what is kept), names it doesn't keep count nothing either.
    /// </summary>
    public static ThumbCensus Count(IEnumerable<string> names, EverydayFiles? only = null, ThumbKeep? keep = null)
    {
        var counts = new long[4];
        long files = 0;
        foreach (var n in names)
        {
            files++;
            if (only != null && !only.IsEveryday(n)) continue;
            if (keep != null && !keep.Keeps(n)) continue;
            counts[(int)ThumbKinds.CostOf(n)]++;
        }
        return Census(counts, files, complete: true, live: files);
    }

    /// <summary>
    /// Whether the index has changed much since a census of <paramref name="countedFiles"/> live entries: 10 % or 20,000
    /// either way. Then the estimate is worked out again. Compare like with like: the index's live count when it was
    /// counted (<see cref="ThumbCensus.LiveAtCount"/>, folders included) with its live count now.
    /// </summary>
    public static bool ChangedMuch(long countedFiles, long liveFilesNow)
    {
        long diff = Math.Abs(liveFilesNow - countedFiles);
        return diff >= ChangedFiles || diff >= Math.Max(1, countedFiles) * ChangedShare;
    }

    /// <summary>
    /// Counts the live name index's files by kind, in read-lock slices that adapt to how long they take, and held out of
    /// background mode (a writer waiting behind a background-priority holder stalls every search), as
    /// ContentEstimator.CountIndex does. A Compact during the walk (ids renumbered) starts it again, three times at most;
    /// after that the census is not <see cref="ThumbCensus.Complete"/>. Call it off the UI thread; it allocates nothing
    /// per file. Throws OperationCanceledException when <paramref name="ct"/> is cancelled. With <paramref name="only"/>
    /// (the setting "Everyday files only", on by default) files it leaves out count nothing: the thumbnail views never
    /// show them, so they never get a stored thumbnail (source trees would otherwise swell the number many times). With
    /// <paramref name="keep"/> (Settings' "Keep thumbnails of" and the types never kept), files it doesn't keep count nothing.
    /// </summary>
    public static ThumbCensus CountIndex(FileIndex index, CancellationToken ct, EverydayFiles? only = null, ThumbKeep? keep = null)
    {
        var counts = new long[4];
        long files = 0;
        long live = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Array.Clear(counts);
            files = 0;
            long generation = index.Generation;
            live = index.LiveCount;
            bool restart = false;
            var slice = new AdaptiveSlice(16_384, start: 256);
            for (int start = 0; ; )
            {
                ct.ThrowIfCancellationRequested();
                int size = slice.Size;
                var watch = Stopwatch.StartNew();
                bool background = ContentNative.LeaveBackgroundMode();
                index.Lock.EnterReadLock();
                try
                {
                    if (index.Generation != generation) { restart = true; break; }
                    int count = index.Count;
                    if (start >= count) break;
                    for (int id = start, end = Math.Min(count, start + size); id < end; id++)
                    {
                        if (!index.IsAlive(id) || index.IsDirectory(id)) continue;
                        files++;
                        string name = index.Name(id);
                        if (only != null && !only.IsEveryday(name)) continue;
                        var ext = ExtensionOf(name);
                        var cost = ThumbKinds.CostOfExtension(ext);
                        if (keep != null && cost != ThumbCost.Nothing && !keep.KeepsExtension(ext, cost)) continue;
                        counts[(int)cost]++;
                    }
                }
                finally
                {
                    index.Lock.ExitReadLock();
                    if (background) ContentNative.EnterBackgroundMode();
                }
                slice.Took(watch.Elapsed.TotalMilliseconds);
                start += size;
            }
            if (!restart) return Census(counts, files, complete: true, live);
        }
        return Census(counts, files, complete: false, live);
    }

    /// <summary>The extension of a file name without the dot, as FileIndex.Extension sees it (none for ".name" or "name.").</summary>
    internal static ReadOnlySpan<char> ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? [] : name.AsSpan(dot + 1);
    }

    static ThumbCensus Census(long[] c, long files, bool complete, long live)
        => new(c[(int)ThumbCost.Photo], c[(int)ThumbCost.Video], c[(int)ThumbCost.Document], files, complete, live);
}

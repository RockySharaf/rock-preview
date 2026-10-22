using Anything.Core.Content.Extract;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;

namespace Anything.Core.Content;

/// <summary>Entry points for the app, the demo and the CLI.</summary>
public static class ContentServices
{
    /// <summary>Where the text index lives for a name database path: "&lt;its folder&gt;\Content".</summary>
    public static string FolderFor(string databasePath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? "", "Content");

    /// <summary>
    /// Whether a folder holds a text index (a manifest or segment files): a folder of the user's own that happens to be
    /// called Content, beside a moved name database, doesn't.
    /// </summary>
    public static bool HoldsIndex(string folder) => ContentStore.HoldsIndex(folder);

    /// <summary>
    /// Deletes a text index's own files from a folder no index has open, then the folder only when nothing else is left
    /// in it (uninstall): files the user keeps there stay. False when some of the index's files couldn't be deleted.
    /// </summary>
    public static bool DeleteIndexFiles(string folder) => ContentStore.DeleteIndexFiles(folder);

    /// <summary>The real text index in the folder; nothing is read or written until Start.</summary>
    public static IContentIndex Create(string folder)
    {
        string full = Path.GetFullPath(folder);
        return new ContentIndex(() => ContentStore.Open(full), Files(), null, SystemPowerPolicy.Instance, new ContentIndexOptions
        {
            Folder = full,
            OpenReadOnly = () => ContentStore.Open(full, readOnly: true),
        });
    }

    /// <summary>
    /// A text index in memory reading through the source; demo = every root in scope (Ready once the names are), no power
    /// or elevation pauses, paced at about 500 documents a second.
    /// </summary>
    public static IContentIndex CreateInMemory(IContentSource source, bool demo)
        => new ContentIndex(() => ContentStore.Open(null), source, null, demo ? NoPowerPolicy.Instance : SystemPowerPolicy.Instance,
            demo ? new ContentIndexOptions(AllRootsInScope: true, IgnorePower: true, IgnoreElevation: true, PaceMs: 2) : new ContentIndexOptions());

    /// <summary>
    /// The real readers on disk. Network reads keep to a per-share speed limit: the indexer's scheduler hands each read
    /// its own share bucket (Settings' limit, keyed by volume key); other reads (calibration) share an 8 MB/s default.
    /// </summary>
    public static IContentSource Files() => new FileContentSource(ShareThrottle.Default.Wait);

    /// <summary>Opens the folder read-only and attaches it to the index for searching (CLI); false when there is none.</summary>
    public static bool AttachForSearch(FileIndex index, string folder)
    {
        try
        {
            if (!Directory.Exists(folder) || !Directory.EnumerateFiles(folder, "manifest*").Any()) return false;
            var store = ContentStore.Open(folder, readOnly: true);
            bool empty;
            using (var view = store.Acquire()) empty = view.Segments.Count == 0;
            if (empty) { store.Dispose(); return false; }
            index.Content = new ReadOnlyContentSource(store);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    /// <summary>
    /// Shares from's text index with another index (folder listings); ids are mapped on demand, kept in a
    /// ConditionalWeakTable&lt;FileIndex, OnDemandMap&gt; keyed by the other index's Generation (no Compacted subscription).
    /// </summary>
    public static void ShareWith(FileIndex from, FileIndex to)
    {
        if (ReferenceEquals(from, to)) return;
        // Forwarded, not copied: the listing follows the main index's text index as it is turned on, off or rebuilt.
        to.Content = new SharedContentSource(from);
    }

    /// <summary>Stops sharing with an index (FolderListing.Dispose): clears to.Content.</summary>
    public static void Unshare(FileIndex to) => to.Content = null;
}

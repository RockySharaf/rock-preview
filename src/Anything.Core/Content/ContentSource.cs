namespace Anything.Core.Content;

/// <summary>The text index as searches reach it (FileIndex.Content); only Core implements it.</summary>
public abstract class ContentSource
{
    internal ContentSource() { }

    /// <summary>The mode the index is built for; NamesOnly means content terms match nothing.</summary>
    internal abstract ContentMode Mode { get; }

    /// <summary>
    /// The current view with a reference the caller disposes; null while off or before the first reconcile. Called before
    /// FileIndex.Lock. Implementations read the published view and retry when TryAddRef refuses (see <see cref="Acquire"/>).
    /// </summary>
    internal abstract ContentView? AcquireView();

    /// <summary>Ids of the view's documents in this index; called under its read lock; null = stale (cleared index).</summary>
    internal abstract DocIdMap? MapFor(FileIndex index, ContentView view);

    /// <summary>
    /// Document sets kept across keystrokes and the lookups still running, shared by every engine and tab: resolved sets
    /// per (term key, segment id), and in-flight lookups keyed by term key that later searches join.
    /// </summary>
    internal abstract Search.DocSetCache Cache { get; }

    /// <summary>A lookup that ran past its budget finished: raise ContentChanged at once (coalesced), not on the commit debounce.</summary>
    internal abstract void PrefetchCompleted();

    /// <summary>
    /// A full reconcile is pending or running (ContentStatus.Linked false): documents of rescanned or rebuilt roots may be
    /// unmapped for a while. SearchEngine then sets SearchResults.ContentPending, so the app searches again on ContentChanged.
    /// </summary>
    internal abstract bool CatchingUp { get; }

    /// <summary>
    /// Takes a reference on the view published in <paramref name="slot"/>: reads it, tries to add a reference, and reads
    /// again when the writer released that view meanwhile. Null when nothing is published.
    /// </summary>
    internal static ContentView? Acquire(ref ContentView? slot)
    {
        for (var spin = new SpinWait(); ; spin.SpinOnce())
        {
            var view = Volatile.Read(ref slot);
            if (view == null) return null;
            if (view.TryAddRef()) return view;
        }
    }
}

/// <summary>
/// What one search reads: a store view and the id map published with it; immutable, ref-counted. Whoever creates it holds
/// one reference; every successful <see cref="AddRef"/> or <see cref="TryAddRef"/> is paired with one <see cref="Dispose"/>.
/// </summary>
internal sealed class ContentView : IDisposable
{
    int _refs = 1;

    /// <summary>Wraps a store view (takes over its reference) with its map.</summary>
    public ContentView(Store.IStoreView store, DocIdMap map, ContentMode mode)
    {
        Store = store;
        Map = map;
        Mode = mode;
    }

    /// <summary>The store at one commit.</summary>
    public Store.IStoreView Store { get; }
    /// <summary>Ids for the main index's generation at publish time.</summary>
    public DocIdMap Map { get; }
    /// <summary>The mode it was published for.</summary>
    public ContentMode Mode { get; }

    /// <summary>Whether every reference was released (the store view is released too).</summary>
    public bool IsReleased => Volatile.Read(ref _refs) <= 0;

    /// <summary>Another reference to the same view; the holder disposes it. Throws when the view was already released.</summary>
    public ContentView AddRef() => TryAddRef() ? this : throw new ObjectDisposedException(nameof(ContentView));

    /// <summary>Adds a reference unless the last one is gone (then a newer view must be read instead).</summary>
    public bool TryAddRef()
    {
        for (int refs = Volatile.Read(ref _refs); refs > 0; refs = Volatile.Read(ref _refs))
            if (Interlocked.CompareExchange(ref _refs, refs + 1, refs) == refs) return true;
        return false;
    }

    /// <summary>Releases this reference; segments outlive merges until the last one goes.</summary>
    public void Dispose()
    {
        int refs = Interlocked.Decrement(ref _refs);
        if (refs == 0) Store.Dispose();
        else if (refs < 0) Interlocked.Exchange(ref _refs, 0); // released once too often (a holder's bug): never release the store twice
    }
}

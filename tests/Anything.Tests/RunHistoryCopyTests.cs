using Anything.Core;

namespace Anything.Tests;

/// <summary><see cref="RunHistory.CopyFrom"/>: a copy kept for another index (a folder listing's) that follows the app's history.</summary>
public sealed class RunHistoryCopyTests
{
    static RunLookup Lookup(FileIndex index)
    {
        index.Lock.EnterReadLock();
        try { return index.RunHistory.Lookup(index); }
        finally { index.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Copies_every_entry_and_follows_changes_and_removals()
    {
        var history = new RunHistory();
        history.Increment(@"C:\a.txt", 100);
        history.Set(@"C:\b.txt", 3, 200);
        var copy = new RunHistory();
        copy.Set(@"C:\stale.txt", 9, 1);
        int raised = 0;
        copy.Changed += () => raised++;

        copy.CopyFrom(history);
        Assert.Equal(1, raised);
        Assert.Equal(2, copy.Count);
        Assert.Equal(new RunHistory.Entry(1, 100), copy.Get(@"c:\A.TXT"));
        Assert.Equal(new RunHistory.Entry(3, 200), copy.Get(@"C:\b.txt"));
        Assert.Equal(0, copy.Get(@"C:\stale.txt").Count);

        // The same again: nothing changed, nothing raised.
        copy.CopyFrom(history);
        Assert.Equal(1, raised);

        history.Increment(@"C:\a.txt", 300);
        history.Set(@"C:\b.txt", 0, 0);
        copy.CopyFrom(history);
        Assert.Equal(2, raised);
        Assert.Equal(new RunHistory.Entry(2, 300), copy.Get(@"C:\a.txt"));
        Assert.Equal(1, copy.Count);

        history.Clear();
        copy.CopyFrom(history);
        Assert.Equal(0, copy.Count);
        copy.CopyFrom(copy); // itself: nothing
        Assert.Equal(3, raised);
    }

    [Fact]
    public void A_copy_matches_only_the_paths_that_changed()
    {
        var history = new RunHistory();
        history.Increment(@"C:\x\a.txt", 1);
        history.Increment(@"C:\x\b.txt", 1);
        var copy = new RunHistory();
        copy.CopyFrom(history);
        var index = new FileIndex { RunHistory = copy };
        int root = index.AddRoot(@"C:\x");
        int a = index.Add("a.txt", root, FileAttributes.Archive, 1, 0, 0);
        int b = index.Add("b.txt", root, FileAttributes.Archive, 1, 0, 0);
        var first = Lookup(index);
        Assert.Equal(1, first.Get(a).Count);

        // One run more of b.txt: only b's entry changes, so orders built on the first lookup take just that change.
        history.Increment(@"C:\x\b.txt", 2);
        copy.CopyFrom(history);
        var second = Lookup(index);
        Assert.Equal(2, second.Get(b).Count);
        var changed = new List<int>();
        Assert.True(second.TryGetChangesSince(first.Sequence, changed));
        Assert.Equal([b], changed);
    }
}

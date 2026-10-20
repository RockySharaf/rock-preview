using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests;

/// <summary>
/// Removing a folder uses the index's child counts to skip the whole-index pass when the folder is empty: whatever
/// path entries take (added, moved, compacted, loaded), removing a folder must still remove exactly its subtree.
/// </summary>
public sealed class FileIndexChildCountTests
{
    static string[] Live(FileIndex index) =>
        Enumerable.Range(0, index.Count).Where(index.IsAlive).Select(index.GetFullPath).Order(StringComparer.Ordinal).ToArray();

    static FileIndex Tree(out int root, out int a, out int b, out int a1)
    {
        var index = new FileIndex();
        root = index.Add("X:", FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
        a = index.Add("a", root, FileAttributes.Directory, -1, 0, 0);
        b = index.Add("b", root, FileAttributes.Directory, -1, 0, 0);
        a1 = index.Add("a1", a, FileAttributes.Directory, -1, 0, 0);
        index.Add("f.txt", a1, FileAttributes.Normal, 1, 0, 0);
        return index;
    }

    [Fact]
    public void Removing_a_folder_removes_its_subtree_and_only_it()
    {
        var index = Tree(out _, out int a, out _, out _);
        index.Remove(a);
        Assert.Equal([@"X:\", @"X:\b"], Live(index));
    }

    [Fact]
    public void Folder_emptied_child_by_child_is_removed_alone()
    {
        var index = Tree(out _, out int a, out _, out int a1);
        var file = Enumerable.Range(0, index.Count).Single(i => index.Name(i) == "f.txt");
        // The change journal reports a deleted tree children first.
        index.Remove(file);
        index.Remove(a1);
        index.Remove(a);
        Assert.Equal([@"X:\", @"X:\b"], Live(index));
    }

    [Fact]
    public void A_folder_that_got_a_child_by_a_move_is_not_treated_as_empty()
    {
        var index = Tree(out _, out int a, out int b, out int a1);
        index.Rename(a1, "moved", b); // b now holds a1 (and its file); a is empty
        index.Remove(b);
        Assert.Equal([@"X:\", @"X:\a"], Live(index));
        index.Remove(a);
        Assert.Equal([@"X:\"], Live(index));
    }

    [Fact]
    public void Counts_survive_compaction()
    {
        var index = Tree(out _, out int a, out int b, out _);
        index.Remove(b);
        var remap = index.Compact();
        index.Add("late.txt", remap[a], FileAttributes.Normal, 1, 0, 0);
        index.Remove(remap[a]);
        Assert.Equal([@"X:\"], Live(index));
    }

    [Fact]
    public void Counts_are_right_after_loading_children_saved_before_their_parents()
    {
        var dir = Directory.CreateTempSubdirectory("anything-kids-").FullName;
        try
        {
            // Saved order follows ids, and a folder moved under a later folder comes before its new parent.
            var index = Tree(out int root, out int a, out _, out int a1);
            int late = index.Add("late", root, FileAttributes.Directory, -1, 0, 0);
            index.Rename(a1, "a1", late);
            string db = Path.Combine(dir, "x.db");
            IndexDatabase.Save(db, index, [new DbRoot { Path = "X:", Kind = RootKind.Folder, RootId = root }]);

            var loaded = new FileIndex();
            var roots = IndexDatabase.Load(db, loaded)!;
            int lateId = Enumerable.Range(0, loaded.Count).Single(i => loaded.Name(i) == "late");
            loaded.Remove(lateId);
            Assert.Equal([@"X:\", @"X:\a", @"X:\b"], Live(loaded));
            Assert.Single(roots);
        }
        finally { Directory.Delete(dir, true); }
    }
}

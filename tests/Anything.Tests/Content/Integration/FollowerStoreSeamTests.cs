using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Store;
using Anything.Core.Search;

namespace Anything.Tests.Content.Integration;

/// <summary>
/// I2, where the follower (U5) meets the real store (U1) on paths only fakes covered before: re-keys rewritten in a commit
/// and in place, a damaged segment found by the scrub and dropped, a store opened while a file of it is held, and a
/// Rebuild's delete while a search still holds the old segments.
/// </summary>
[Collection(RealFilesSerial.Name)]
public sealed class FollowerStoreSeamTests : IDisposable
{
    readonly RealFilesRig _rig = new(ContentMode.Documents);

    public void Dispose() => _rig.Dispose();

    static readonly SearchEngine Engine = new() { ContentBudget = TimeSpan.FromSeconds(30) };

    string[] Find(string query)
    {
        var r = Engine.Search(_rig.Index, query, new SearchOptions(), SortKey.Name, false, default);
        Assert.Null(r.Error);
        return [.. r.Ids.Select(id => Path.GetRelativePath(_rig.Root, _rig.Index.GetFullPath(id))).Order(StringComparer.OrdinalIgnoreCase)];
    }

    void Notes(string folder, int count)
    {
        for (int i = 0; i < count; i++) _rig.Write($@"{folder}\note{i:D2}.txt", $"memo {i} about subject{i % 5} and word{i}");
    }

    void AssertNoDuplicates(int expected)
    {
        var rows = _rig.Rows();
        Assert.Equal(expected, rows.Count);
        Assert.Equal(expected, rows.Select(r => r.Row.Key).Distinct().Count());
    }

    [Theory]
    [InlineData(false)] // a small segment: the commit carrying the copies rewrites it (its change has remaps)
    [InlineData(true)]  // a large one (over the in-place limit): IContentStore.Rekey rewrites it once, between batches
    public void A_folder_rename_moves_most_of_a_segment_without_reading_anything(bool inPlace)
    {
        Notes("Jobs", 30);
        Notes("Other", 5);
        _rig.Start(tweak: o => o with { RekeyInPlaceAbove = inPlace ? 10 : 10_000 });
        _rig.Settle();
        int reads = _rig.Files.Reads;
        Assert.Equal(35, reads);
        uint[] before;
        using (var v = _rig.Content!.AcquireView()!) before = [.. v.Store.Segments.Select(s => s.Id)];

        _rig.Rename(_rig.Full("Jobs"), _rig.Full("Jobs 2024"));
        _rig.Settle();
        Assert.Equal(reads, _rig.Files.Reads);
        if (inPlace) Assert.True(_rig.Follower.RekeysInPlace > 0, "the segment wasn't rewritten in place");
        using (var v = _rig.Content!.AcquireView()!)
            Assert.DoesNotContain(v.Store.Segments, s => before.Contains(s.Id)); // the moved segment was rewritten, not copied into a new one
        Assert.Equal([@"Jobs 2024\note07.txt"], Find("content:word7"));
        Assert.Equal(7, Find("content:subject2").Length);
        Assert.Equal([@"Other\note03.txt"], Find("content:word3 Other\\"));
        Assert.Null(_rig.Row(_rig.Full(@"Jobs\note07.txt")));
        Assert.NotNull(_rig.Row(_rig.Full(@"Jobs 2024\note07.txt")));
        AssertNoDuplicates(35);

        // And back again, then a restart: still bound, nothing read.
        _rig.Rename(_rig.Full("Jobs 2024"), _rig.Full("Jobs"));
        _rig.Settle();
        _rig.Stop();
        _rig.NewIndex(fill: true);
        _rig.Start();
        _rig.Settle();
        Assert.Equal(reads, _rig.Files.Reads);
        Assert.Equal([@"Jobs\note07.txt"], Find("content:word7"));
        AssertNoDuplicates(35);
    }

    [Fact]
    public void A_damaged_segment_is_found_by_the_scrub_dropped_and_its_files_read_again()
    {
        Notes("A", 12);
        _rig.Start();
        _rig.Settle();
        Notes("B", 8); // a second commit, so one segment stays sound
        _rig.Settle();
        Assert.Equal(20, _rig.Files.Reads);
        uint victim;
        using (var v = _rig.Content!.AcquireView()!)
        {
            Assert.Equal(2, v.Store.Segments.Count);
            victim = v.Store.Segments[0].Id;
            Assert.Equal(12, v.Store.Segments[0].DocCount);
        }
        _rig.Stop();

        // A bit flips inside the first segment's stored text (only a full check finds it; opening doesn't).
        string file = Path.Combine(_rig.ContentFolder.Path, SegmentFormat.FileName(victim));
        var bytes = File.ReadAllBytes(file);
        bytes[SegmentFormat.HeaderSize + 10] ^= 1;
        File.WriteAllBytes(file, bytes);

        _rig.NewIndex(fill: true);
        _rig.Start();
        _rig.Pass(60_000, step: 2000); // the idle scrub (one segment every 10 s once open) finds it; an empty commit drops it
        Assert.Equal(32, _rig.Files.Reads); // its 12 files read again, the other segment's never
        foreach (var f in Directory.GetFiles(_rig.Full("B"))) Assert.Equal(1, _rig.Files.ReadsOf(f));
        using (var v = _rig.Content!.AcquireView()!) Assert.DoesNotContain(v.Store.Segments, s => s.Id == victim);
        Assert.Equal([@"A\note04.txt", @"B\note04.txt"], Find("content:word4"));
        AssertNoDuplicates(20);
        Assert.Equal(ContentPhase.UpToDate, _rig.Content!.Status.Phase);
    }

    [Fact]
    public void A_store_whose_file_is_held_opens_read_only_and_is_opened_again_once_it_is_free()
    {
        Notes("A", 5);
        _rig.Start();
        _rig.Settle();
        _rig.Stop();
        int reads = _rig.Files.Reads;

        // Another program holds the manifest without sharing it (a backup tool, an indexer).
        var held = new FileStream(Path.Combine(_rig.ContentFolder.Path, "manifest"), FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            Notes("B", 3);
            _rig.NewIndex(fill: true);
            _rig.Start();
            _rig.Settle();
            var status = _rig.Content!.Status;
            Assert.Equal(reads, _rig.Files.Reads); // nothing is read for a store that can't be written
            Assert.True(status.Phase == ContentPhase.Paused, status.ToString());
            Assert.Contains("in use", status.Reason);
        }
        finally { held.Dispose(); }

        // Free again: opened at the next try (30 s), and the new files are read.
        _rig.Pass(40_000);
        Assert.Equal(reads + 3, _rig.Files.Reads);
        Assert.Equal(ContentPhase.UpToDate, _rig.Content!.Status.Phase);
        Assert.Equal([@"B\note01.txt"], Find("content:word1 B\\"));
        AssertNoDuplicates(8);
    }

    [Fact]
    public void A_rebuild_while_a_search_holds_the_old_segments_finishes_deleting_once_they_are_released()
    {
        Notes("A", 10);
        _rig.Start();
        _rig.Settle();
        uint[] old;
        using (var v = _rig.Content!.AcquireView()!) old = [.. v.Store.Segments.Select(s => s.Id)];

        // A search's lookup still holds the view (and its mapped segments) when Rebuild text index is clicked.
        var lease = _rig.Content.AcquireView()!;
        _rig.Content.Rebuild();
        _rig.Pass(5000);
        Assert.Equal("memo 3 about subject3 and word3", lease.Store.Segments[0].ReadStored(
            lease.Store.Find(_rig.KeyOf(_rig.Full(@"A\note03.txt")))!.Value.Doc)!.Body); // the old view still reads
        lease.Dispose();

        _rig.Pass(30_000);
        foreach (uint id in old) Assert.False(File.Exists(Path.Combine(_rig.ContentFolder.Path, SegmentFormat.FileName(id))), $"segment {id} is still there");
        Assert.Equal(20, _rig.Files.Reads); // everything read again, once
        foreach (var f in Directory.GetFiles(_rig.Full("A"))) Assert.Equal(2, _rig.Files.ReadsOf(f));
        Assert.Equal([@"A\note03.txt"], Find("content:word3"));
        AssertNoDuplicates(10);
        Assert.Equal(ContentPhase.UpToDate, _rig.Content!.Status.Phase);
    }
}

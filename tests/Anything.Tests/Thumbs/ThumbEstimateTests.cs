using Anything.Core;
using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

public sealed class ThumbEstimateTests
{
    [Fact]
    public void Each_kind_costs_its_measured_average_at_the_size_class_in_use()
    {
        Assert.Equal(0, ThumbEstimator.BytesFor(0, 0, 0, 256));
        // Decimal, like every size shown: 13 KB is 13,000 bytes.
        Assert.Equal(3 * 13_000 + 2 * 7_000 + 5 * 17_000, ThumbEstimator.BytesFor(3, 2, 5, 256));
        Assert.Equal(3 * 40_000 + 2 * 19_000 + 5 * 50_000, ThumbEstimator.BytesFor(3, 2, 5, 512));
        // A display size counts as its class: up to 256 the small figures, above it the large ones.
        Assert.Equal(ThumbEstimator.BytesFor(3, 2, 5, 256), ThumbEstimator.BytesFor(3, 2, 5, 160));
        Assert.Equal(ThumbEstimator.BytesFor(3, 2, 5, 512), ThumbEstimator.BytesFor(3, 2, 5, 320));
        // 100,000 photos come to about 1.3 GB at 160 px, and about three times as much at 400 px.
        Assert.Equal(100_000L * 13_000, ThumbEstimator.BytesFor(100_000, 0, 0, 160));
        Assert.Equal(100_000L * 40_000, ThumbEstimator.BytesFor(100_000, 0, 0, 400));
        Assert.Equal(0, ThumbEstimator.BytesFor(-5, 0, 0, 256));
    }

    [Fact]
    public void The_large_class_takes_far_more_than_half_again_so_it_has_figures_of_its_own()
    {
        Assert.True(ThumbEstimator.PhotoBytesLarge > ThumbEstimator.PhotoBytesSmall * 3 / 2);
        Assert.True(ThumbEstimator.VideoBytesLarge > ThumbEstimator.VideoBytesSmall * 3 / 2);
        Assert.True(ThumbEstimator.FlatBytesLarge > ThumbEstimator.FlatBytesSmall * 3 / 2);
    }

    [Fact]
    public void A_census_counts_each_name_by_its_kind_and_kinds_without_a_picture_count_nothing()
    {
        var census = ThumbEstimator.Count(
        [
            "holiday.jpg", "scan.HEIC", "raw.cr3", "art.psd", "render.tga",   // photos
            "clip.mp4", "film.MKV",                                         // videos
            "report.pdf", "slides.pptx", "logo.png", "font.ttf", "plan.dwg",  // documents
            "notes.txt", "Program.cs", "sheet.xlsx",                        // text pages, a spreadsheet: never stored
            "setup.exe", "archive.zip", "song.mp3", "README", "data.bin",   // nothing
        ]);
        Assert.Equal((5L, 2L, 5L), (census.Photos, census.Videos, census.Documents));
        Assert.Equal(12, census.Files);
        Assert.Equal(20, census.IndexedFiles);
        Assert.True(census.Complete);
        Assert.Equal(ThumbEstimator.BytesFor(5, 2, 5, 256), census.BytesAt(256));
        Assert.Equal(ThumbEstimator.BytesFor(5, 2, 5, 512), census.BytesAt(512));
    }

    [Fact]
    public void With_everyday_files_only_what_the_views_never_show_counts_nothing()
    {
        string[] names = ["holiday.jpg", "clip.mp4", "report.pdf", "Program.cs", "package.json", "notes.txt", "build.psd"];
        var all = ThumbEstimator.Count(names);
        var everyday = ThumbEstimator.Count(names, Anything.Core.Search.EverydayFiles.Default);
        Assert.Equal((all.Videos, all.Documents), (everyday.Videos, everyday.Documents));
        Assert.Equal(all.IndexedFiles, everyday.IndexedFiles);
        Assert.True(everyday.Photos <= all.Photos);

        var index = new FileIndex();
        int root = index.AddRoot("C:");
        for (int i = 0; i < 5; i++) index.Add($"p{i}.jpg", root, FileAttributes.Archive, 1000, 1, 0);
        for (int i = 0; i < 50; i++) index.Add($"s{i}.cs", root, FileAttributes.Archive, 1000, 1, 0);
        var counted = ThumbEstimator.CountIndex(index, CancellationToken.None, Anything.Core.Search.EverydayFiles.Default);
        Assert.Equal((5L, 5L), (counted.Photos, counted.Files));
        Assert.Equal(55, counted.IndexedFiles);
    }

    [Fact]
    public void The_live_index_is_counted_by_kind_without_folders_or_removed_files()
    {
        var index = new FileIndex();
        int root = index.AddRoot("C:");
        int photos = index.Add("Photos.jpg", root, FileAttributes.Directory, -1, 0, 0);
        for (int i = 0; i < 40; i++) index.Add($"p{i}.JPG", photos, FileAttributes.Archive, 1000, 1, 0);
        for (int i = 0; i < 3; i++) index.Add($"v{i}.mov", photos, FileAttributes.Archive, 1000, 1, 0);
        index.Add("doc.pdf", root, FileAttributes.Archive, 10, 1, 0);
        index.Add("page.md", root, FileAttributes.Archive, 10, 1, 0);
        index.Add("tool.exe", root, FileAttributes.Archive, 10, 1, 0);
        // Names FileIndex.Extension sees no extension in.
        index.Add(".jpg", root, FileAttributes.Archive, 10, 1, 0);
        index.Add("photo.", root, FileAttributes.Archive, 10, 1, 0);
        int gone = index.Add("gone.jpg", root, FileAttributes.Archive, 10, 1, 0);
        index.Remove(gone);
        var census = ThumbEstimator.CountIndex(index, CancellationToken.None);
        Assert.Equal((40L, 3L, 1L, 44L), (census.Photos, census.Videos, census.Documents, census.Files));
        Assert.Equal(48, census.IndexedFiles);
        Assert.True(census.Complete);
        Assert.Equal(index.LiveCount, census.LiveAtCount);
    }

    [Fact]
    public void A_census_of_an_index_with_many_folders_is_not_changed_much_right_after_counting()
    {
        // 40,000 folders and 200,000 files: the files counted differ from the index's live count (folders included) by
        // far more than 10 %, so comparing those two would count again and again with nothing changed.
        var index = new FileIndex();
        int root = index.AddRoot("C:");
        var folders = new int[40_000];
        for (int i = 0; i < folders.Length; i++) folders[i] = index.Add($"f{i}", root, FileAttributes.Directory, -1, 0, 0);
        for (int i = 0; i < 200_000; i++) index.Add($"p{i}.jpg", folders[i % folders.Length], FileAttributes.Archive, 1000, 1, 0);
        var census = ThumbEstimator.CountIndex(index, CancellationToken.None);
        Assert.Equal(200_000, census.IndexedFiles);
        Assert.False(ThumbEstimator.ChangedMuch(census.LiveAtCount, index.LiveCount));
        // A real change still counts: 25,000 more files.
        for (int i = 0; i < 25_000; i++) index.Add($"q{i}.jpg", root, FileAttributes.Archive, 1000, 1, 0);
        Assert.True(ThumbEstimator.ChangedMuch(census.LiveAtCount, index.LiveCount));
    }

    [Fact]
    public async Task Counting_while_the_index_changes_and_is_compacted_is_safe()
    {
        var index = new FileIndex();
        int root = index.AddRoot("D:");
        for (int i = 0; i < 20_000; i++) index.Add($"a{i}.jpg", root, FileAttributes.Archive, 1, 1, 0);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            int n = 0;
            while (!stop.IsCancellationRequested)
            {
                int id = index.Add($"b{n++}.pdf", root, FileAttributes.Archive, 1, 1, 0);
                if (n % 2 == 0) index.Remove(id);
                if (n % 500 == 0) index.Compact();
            }
        });
        try
        {
            for (int round = 0; round < 20; round++)
            {
                var census = ThumbEstimator.CountIndex(index, CancellationToken.None);
                // The pictures never change; a walk that was restarted too often says it isn't complete.
                if (census.Complete) Assert.Equal(20_000, census.Photos);
                Assert.True(census.Photos <= 20_000);
                Assert.Equal(0, census.Videos);
            }
        }
        finally
        {
            stop.Cancel();
            await writer;
        }
        var quiet = ThumbEstimator.CountIndex(index, CancellationToken.None);
        Assert.True(quiet.Complete);
        Assert.Equal(20_000, quiet.Photos);
        Assert.Equal(index.LiveCount - 1, quiet.IndexedFiles);
    }

    [Fact]
    public void Counting_stops_when_cancelled()
    {
        var index = new FileIndex();
        int root = index.AddRoot("E:");
        index.Add("a.jpg", root, FileAttributes.Archive, 1, 1, 0);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ThumbEstimator.CountIndex(index, cancelled.Token));
    }

    [Fact]
    public void The_estimate_is_worked_out_again_when_the_index_changed_much()
    {
        Assert.False(ThumbEstimator.ChangedMuch(100_000, 100_000));
        Assert.False(ThumbEstimator.ChangedMuch(100_000, 109_999));
        Assert.True(ThumbEstimator.ChangedMuch(100_000, 110_000));
        Assert.True(ThumbEstimator.ChangedMuch(100_000, 90_000));
        // On a big index 20,000 files are enough.
        Assert.True(ThumbEstimator.ChangedMuch(1_000_000, 1_020_000));
        Assert.False(ThumbEstimator.ChangedMuch(1_000_000, 1_019_999));
        // From nothing, any file is a change.
        Assert.True(ThumbEstimator.ChangedMuch(0, 1));
        Assert.False(ThumbEstimator.ChangedMuch(0, 0));
    }

    [Fact]
    public void The_extension_is_read_as_the_index_reads_it()
    {
        Assert.Equal("JPG", ThumbEstimator.ExtensionOf("a.b.JPG").ToString());
        Assert.Equal("", ThumbEstimator.ExtensionOf(".jpg").ToString());
        Assert.Equal("", ThumbEstimator.ExtensionOf("photo.").ToString());
        Assert.Equal("", ThumbEstimator.ExtensionOf("README").ToString());
    }
}

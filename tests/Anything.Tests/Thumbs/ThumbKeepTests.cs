using Anything.Core;
using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

/// <summary>The user's choice of what "Instant thumbnails" keeps, the limit box, and the "store is full" rules.</summary>
public sealed class ThumbKeepTests
{
    static ThumbKeep Keep(bool pictures = true, bool videos = true, bool documents = true, params string[] skip)
        => new(pictures, videos, documents, skip);

    [Theory]
    [InlineData("a.jpg", ThumbKeepGroup.Pictures)]
    [InlineData("a.CR2", ThumbKeepGroup.Pictures)]
    [InlineData("a.psd", ThumbKeepGroup.Pictures)]
    [InlineData("a.png", ThumbKeepGroup.Pictures)] // priced as a document by the estimate, a picture to the user
    [InlineData("a.gif", ThumbKeepGroup.Pictures)]
    [InlineData("a.mp4", ThumbKeepGroup.Videos)]
    [InlineData("a.pdf", ThumbKeepGroup.Documents)]
    [InlineData("a.docx", ThumbKeepGroup.Documents)]
    [InlineData("a.ai", ThumbKeepGroup.Documents)]
    [InlineData("a.ttf", ThumbKeepGroup.Documents)]
    [InlineData("a.txt", ThumbKeepGroup.None)]
    [InlineData("a.mp3", ThumbKeepGroup.None)]
    [InlineData("a.xlsx", ThumbKeepGroup.None)]
    [InlineData("README", ThumbKeepGroup.None)]
    public void Each_kind_has_its_box(string name, ThumbKeepGroup group) => Assert.Equal(group, ThumbKeep.GroupOf(name));

    [Fact]
    public void Folders_have_no_box() => Assert.Equal(ThumbKeepGroup.None, ThumbKeep.GroupOf(@"C:\Photos.jpg", isFolder: true));

    [Fact]
    public void Everything_ticked_keeps_every_kind_kept_on_disk_and_nothing_else()
    {
        foreach (var name in new[] { "a.jpg", "a.png", "a.heic", "a.mov", "a.pdf", "a.pptx", "a.dwg", "a.txt", "a.mp3", "a.zip", "a.doc", "a" })
            Assert.Equal(ThumbKinds.KeepOnDisk(name), ThumbKeep.All.Keeps(name));
    }

    [Fact]
    public void Each_box_keeps_only_its_own_group()
    {
        var noPictures = Keep(pictures: false);
        Assert.False(noPictures.Keeps("a.jpg"));
        Assert.False(noPictures.Keeps("a.png"));
        Assert.False(noPictures.Keeps("a.psd"));
        Assert.True(noPictures.Keeps("a.mp4"));
        Assert.True(noPictures.Keeps("a.pdf"));

        var noVideos = Keep(videos: false);
        Assert.False(noVideos.Keeps("a.mkv"));
        Assert.True(noVideos.Keeps("a.jpg"));

        var noDocuments = Keep(documents: false);
        Assert.False(noDocuments.Keeps("a.pdf"));
        Assert.False(noDocuments.Keeps("a.docx"));
        Assert.False(noDocuments.Keeps("a.ai"));
        Assert.True(noDocuments.Keeps("a.png"));
        Assert.True(noDocuments.Keeps("a.jpg"));
    }

    [Fact]
    public void Nothing_ticked_keeps_nothing()
    {
        var none = Keep(false, false, false);
        Assert.True(none.KeepsNothing);
        Assert.False(none.Keeps("a.jpg"));
        Assert.False(none.Keeps("a.mp4"));
        Assert.False(none.Keeps("a.pdf"));
        Assert.Equal("Nothing is being saved.", ThumbOption.KeepNote(none));
        // The store keeps the kinds apart now: switching one off deletes its thumbnails; types left out still stay.
        Assert.Equal("When the store is full, thumbnails of documents are dropped first, then videos, then the oldest pictures. "
            + "Switching a kind off deletes its saved thumbnails; those of the file types listed here stay until the store needs their room, or until you clear it.",
            ThumbOption.KeepNote(ThumbKeep.All));
        // A store carried over from an earlier version: its thumbnails carry no kind, so neither rule holds for them.
        Assert.EndsWith(" Thumbnails saved by an earlier version go first, and stay when you switch a kind off.", ThumbOption.KeepNote(ThumbKeep.All, earlierSaved: true));
        Assert.Equal("Nothing is being saved.", ThumbOption.KeepNote(none, earlierSaved: true));
    }

    [Fact]
    public void Ticking_never_keeps_what_the_store_never_keeps()
    {
        // Asked in addition to KeepOnDisk: text pages, music covers and folders stay off disk whatever is ticked.
        Assert.False(ThumbKeep.All.Keeps("notes.txt"));
        Assert.False(ThumbKeep.All.Keeps("song.mp3"));
        Assert.False(ThumbKeep.All.Keeps(@"C:\Photos", isFolder: true));
    }

    [Fact]
    public void Types_typed_oddly_are_read_once_each_without_dots_in_lower_case()
    {
        Assert.Equal(["pdf", "psd"], ThumbKeep.ParseExtensions(".PDF,pdf ;psd"));
        Assert.Equal(["pdf", "psd", "ai"], ThumbKeep.ParseExtensions("pdf, .psd ai"));
        Assert.Equal(["pdf"], ThumbKeep.ParseExtensions("  *.pdf  "));
        Assert.Empty(ThumbKeep.ParseExtensions(""));
        Assert.Empty(ThumbKeep.ParseExtensions(null));
        Assert.Empty(ThumbKeep.ParseExtensions(" , ; . "));
    }

    [Fact]
    public void A_type_left_out_is_never_kept_whatever_its_case_or_path()
    {
        var keep = new ThumbKeep(true, true, true, ThumbKeep.ParseExtensions(".PDF,pdf ;psd"));
        Assert.Equal(["pdf", "psd"], keep.Skipped);
        Assert.False(keep.Keeps("report.pdf"));
        Assert.False(keep.Keeps(@"D:\Work\REPORT.PDF"));
        Assert.False(keep.Keeps("art.PSD"));
        Assert.True(keep.Keeps("photo.jpg"));
        Assert.True(keep.Keeps(@"D:\old.pdf\photo.jpg")); // the folder's name is not the file's type
        Assert.True(keep.Keeps("deck.pptx"));
    }

    [Fact]
    public void The_choice_reads_from_the_ini()
    {
        Assert.Equal(ThumbKeep.All, ThumbKeep.FromIni(null, null)); // a new install, or an ini of 1.7.30
        Assert.True(ThumbKeep.FromIni("", null).KeepsNothing);
        var two = ThumbKeep.FromIni(" Pictures , VIDEOS;nonsense", "PDF .ai");
        Assert.True(two.Pictures);
        Assert.True(two.Videos);
        Assert.False(two.Documents);
        Assert.Equal(["pdf", "ai"], two.Skipped);
        Assert.Empty(ThumbKeep.FromIni(null, "").Skipped);
    }

    [Theory]
    [InlineData(true, true, true, "", "pictures,videos,documents", "")]
    [InlineData(true, false, true, "pdf psd", "pictures,documents", "pdf psd")]
    [InlineData(false, false, false, "ai", "", "ai")]
    [InlineData(false, true, false, ".PDF,pdf ;psd", "videos", "pdf psd")]
    public void The_choice_goes_to_the_ini_and_back(bool pictures, bool videos, bool documents, string typed, string kindsIni, string skipIni)
    {
        var keep = new ThumbKeep(pictures, videos, documents, ThumbKeep.ParseExtensions(typed));
        Assert.Equal(kindsIni, keep.KindsIni);
        Assert.Equal(skipIni, keep.SkipIni);
        Assert.Equal(keep, ThumbKeep.FromIni(keep.KindsIni, keep.SkipIni));
    }

    [Fact]
    public void A_free_limit_goes_to_the_ini_and_back()
    {
        foreach (int mb in new[] { 120_000, 100, 1_500, 4_000_000, 777 })
            Assert.Equal(mb, ThumbOption.CapMegabytesFrom(mb.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void The_estimate_counts_only_what_would_be_kept()
    {
        string[] names = ["a.jpg", "b.png", "c.mp4", "d.pdf", "e.docx", "f.txt", "g.PDF"];
        var all = ThumbEstimator.Count(names, keep: ThumbKeep.All);
        Assert.Equal((1L, 1L, 4L), (all.Photos, all.Videos, all.Documents));
        Assert.Equal(ThumbEstimator.Count(names), all);

        // png is a picture to the user (priced as a document): with Pictures off it goes from the documents' count.
        var noPictures = ThumbEstimator.Count(names, keep: Keep(pictures: false));
        Assert.Equal((0L, 1L, 3L), (noPictures.Photos, noPictures.Videos, noPictures.Documents));

        var noPdf = ThumbEstimator.Count(names, keep: Keep(skip: "pdf"));
        Assert.Equal((1L, 1L, 2L), (noPdf.Photos, noPdf.Videos, noPdf.Documents));

        var nothing = ThumbEstimator.Count(names, keep: Keep(false, false, false));
        Assert.Equal(0, nothing.Files);
        Assert.Equal(names.Length, nothing.IndexedFiles);
    }

    [Fact]
    public void The_live_index_is_counted_as_the_choice_says()
    {
        var index = new FileIndex();
        int root = index.AddRoot("C:");
        for (int i = 0; i < 10; i++) index.Add($"p{i}.JPG", root, FileAttributes.Archive, 1000, 1, 0);
        for (int i = 0; i < 4; i++) index.Add($"s{i}.png", root, FileAttributes.Archive, 1000, 1, 0);
        for (int i = 0; i < 3; i++) index.Add($"v{i}.mov", root, FileAttributes.Archive, 1000, 1, 0);
        for (int i = 0; i < 5; i++) index.Add($"d{i}.Pdf", root, FileAttributes.Archive, 1000, 1, 0);
        index.Add("x.docx", root, FileAttributes.Archive, 1000, 1, 0);

        var all = ThumbEstimator.CountIndex(index, CancellationToken.None, keep: ThumbKeep.All);
        Assert.Equal((10L, 3L, 10L), (all.Photos, all.Videos, all.Documents));
        var noPdf = ThumbEstimator.CountIndex(index, CancellationToken.None, keep: Keep(videos: false, skip: "PDF"));
        Assert.Equal((10L, 0L, 5L), (noPdf.Photos, noPdf.Videos, noPdf.Documents));
        var noPictures = ThumbEstimator.CountIndex(index, CancellationToken.None, keep: Keep(pictures: false));
        Assert.Equal((0L, 3L, 6L), (noPictures.Photos, noPictures.Videos, noPictures.Documents));
        Assert.Equal(23, noPictures.IndexedFiles);
    }

    [Fact]
    public void With_some_types_off_the_card_counts_the_types_chosen()
    {
        var census = ThumbEstimator.Count(["a.jpg", "b.jpg"], keep: Keep(videos: false));
        string text = ThumbOption.EstimateText(census, true, 500, 256, Keep(videos: false));
        Assert.Contains("For the 2 files of the types you chose on this PC they could take less than 10 MB;", text);
        string none = ThumbOption.EstimateText(new ThumbCensus(0, 0, 0, 3, true), true, 500, 256, Keep(false, false, false));
        Assert.Contains("No files of the types you chose were found on this PC, so this takes almost no space; Rock Preview keeps at most 500 MB.", none);
        // Everything ticked reads as before.
        Assert.Equal(ThumbOption.EstimateText(census, true, 500, 256), ThumbOption.EstimateText(census, true, 500, 256, ThumbKeep.All));
    }

    [Theory]
    [InlineData(500, "0.5")]
    [InlineData(8000, "8")]
    [InlineData(120_000, "120")]
    [InlineData(1_250, "1.25")]
    [InlineData(100, "0.1")]
    [InlineData(1_234, "1.234")]
    [InlineData(4_000_000, "4000")]
    public void The_box_shows_the_limit_as_a_plain_number_of_GB(int mb, string shown)
    {
        Assert.Equal(shown, ThumbOption.LimitBoxText(mb));
        Assert.Equal(mb, ThumbOption.LimitTyped(shown, 999)); // what the box shows reads back as the same limit
    }

    [Theory]
    [InlineData("12", 8000, 12_000)]
    [InlineData("1.5 TB", 8000, 1_500_000)]
    [InlineData("500 MB", 8000, 500)]
    [InlineData("0", 8000, 100)]
    [InlineData("", 8000, 8000)]
    [InlineData("lots", 8000, 8000)]
    [InlineData("-3", 2000, 2000)]
    [InlineData("8 KB", 2000, 2000)]
    public void A_typed_size_sets_the_limit_and_anything_else_puts_the_old_one_back(string typed, int current, int limit)
        => Assert.Equal(limit, ThumbOption.LimitTyped(typed, current));

    [Theory]
    [InlineData(0L, 1000)]
    [InlineData(1L, 1000)]
    [InlineData(1_000_000_000L, 1000)]
    [InlineData(1_000_000_001L, 2000)]
    [InlineData(7_300_000_000L, 8000)]
    [InlineData(119_000_000_000L, 119_000)]
    public void Enough_for_everything_is_the_estimate_rounded_up_to_whole_GB(long bytes, int mb)
    {
        Assert.Equal(mb, ThumbOption.EnoughMegabytes(bytes));
        Assert.StartsWith("Enough for everything: ", ThumbOption.EnoughText(mb));
    }

    [Fact]
    public void The_line_under_the_limit_says_the_free_space_and_when_the_limit_is_more()
    {
        Assert.Equal("120 GB free on drive C:.", ThumbOption.LimitSpaceText(120_000_000_000, @"c:\Data\Thumbs", 8000, 0));
        Assert.Equal("120 GB free on drive D:. The limit is more than that: saving stops when the drive is nearly full.",
            ThumbOption.LimitSpaceText(120_000_000_000, @"D:\Thumbs", 200_000, 0));
        // What the store holds already is room it may use again.
        Assert.Equal("120 GB free on drive D:.", ThumbOption.LimitSpaceText(120_000_000_000, @"D:\Thumbs", 200_000, 90_000_000_000));
        Assert.Equal("120 GB free on this drive.", ThumbOption.LimitSpaceText(120_000_000_000, @"\\server\share\Thumbs", 8000, 0));
        Assert.Equal("", ThumbOption.LimitSpaceText(-1, @"C:\Thumbs", 8000, 0));
    }

    [Fact]
    public void Full_and_low_disk_are_said_in_plain_words()
    {
        // Pictures are kept longest now: the words say which go first.
        Assert.Equal("Instant thumbnails is full (8 GB). To make room, documents and videos go first, then the oldest pictures.", ThumbOption.FullBarText(8000));
        Assert.Equal("Instant thumbnails is full (120 GB). To make room, documents and videos go first, then the oldest pictures.", ThumbOption.FullBarText(120_000));
        Assert.Equal("Not saving new thumbnails now: drive E: is nearly full.", ThumbOption.DiskLowText(@"E:\Rock Preview Thumbnails"));
        Assert.Equal("Full: to keep your pictures, thumbnails of documents and videos go first, and new ones aren't saved while it is full. Raise the limit to keep more.", ThumbOption.FullText);
    }

    [Theory]
    [InlineData(8000, 0, false, true)]     // never told
    [InlineData(8000, 8000, false, false)] // answered at this limit
    [InlineData(8000, 4000, false, true)]  // answered at another limit: told again
    [InlineData(8000, 0, true, false)]     // once per run
    [InlineData(8000, 4000, true, false)]
    public void The_full_bar_is_told_once_per_run_and_once_per_limit(int limit, int toldAt, bool toldThisRun, bool show)
        => Assert.Equal(show, ThumbOption.ShouldTellFull(limit, toldAt, toldThisRun));
}

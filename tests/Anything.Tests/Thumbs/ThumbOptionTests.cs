using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

public sealed class ThumbOptionTests
{
    static ThumbCensus Census(long photos, long videos = 0, long documents = 0)
        => new(photos, videos, documents, photos + videos + documents + 10, Complete: true);

    [Fact]
    public void The_store_lies_beside_the_database_unless_the_user_chose_a_place()
    {
        string db = Path.Combine(Path.GetTempPath(), "rp", "RockPreview.db");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "rp", "Thumbs"), ThumbOption.StoreFolder(db, null));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "rp", "Thumbs"), ThumbOption.StoreFolder(db, ""));
        string chosen = Path.Combine(Path.GetTempPath(), "elsewhere");
        Assert.Equal(Path.Combine(chosen, "Rock Preview Thumbnails"), ThumbOption.StoreFolder(db, chosen));
        // Quoted or padded, as an edited ini may have it.
        Assert.Equal(Path.Combine(chosen, "Rock Preview Thumbnails"), ThumbOption.StoreFolder(db, $" \"{chosen}\" "));
        // A path that can't be made full falls back to the default rather than throwing.
        Assert.Equal(Path.Combine(Path.GetTempPath(), "rp", "Thumbs"), ThumbOption.StoreFolder(db, "C:\\bad\0name"));
    }

    [Theory]
    [InlineData(null, 500)]
    [InlineData("", 500)]
    [InlineData("lots", 500)]
    [InlineData("250", 250)]
    [InlineData("500", 500)]
    [InlineData(" 1000 ", 1000)]
    [InlineData("1024", 1000)]  // a limit from before the units were decimal: the choice just below
    [InlineData("8000", 8000)]
    [InlineData("8192", 8000)]
    [InlineData("100000", 8000)]
    [InlineData("1500", 1000)]  // between two choices: the one below, never more than asked
    [InlineData("100", 250)]    // below the smallest: the smallest
    [InlineData("-5", 250)]
    public void The_limit_from_the_ini_is_one_of_the_choices(string? value, int expected)
        => Assert.Equal(expected, ThumbOption.CapMegabytesFrom(value));

    [Fact]
    public void A_new_place_inside_the_old_stores_folder_is_known()
    {
        Assert.True(ThumbOption.IsWithin(@"X:\data\Thumbs\Rock Preview Thumbnails", @"X:\data\Thumbs"));
        Assert.True(ThumbOption.IsWithin(@"X:\data\thumbs", @"X:\Data\Thumbs\"));
        Assert.False(ThumbOption.IsWithin(@"X:\data\Thumbs2\Rock Preview Thumbnails", @"X:\data\Thumbs"));
        Assert.False(ThumbOption.IsWithin(@"Y:\Rock Preview Thumbnails", @"X:\data\Thumbs"));
    }

    [Fact]
    public void A_folder_that_could_not_be_removed_is_named()
    {
        Assert.Equal("", ThumbOption.RemovalNote(StoreRemoval.Removed, @"X:\Thumbs"));
        Assert.Equal("", ThumbOption.RemovalNote(StoreRemoval.NotThere, @"X:\Thumbs"));
        Assert.Contains(@"X:\Thumbs", ThumbOption.RemovalNote(StoreRemoval.LeftAlone, @"X:\Thumbs"));
        Assert.Contains(@"X:\Thumbs", ThumbOption.RemovalNote(StoreRemoval.Failed, @"X:\Thumbs"));
    }

    [Fact]
    public void Limits_read_as_Settings_lists_them()
    {
        Assert.Equal(["250 MB", "500 MB", "1 GB", "2 GB", "4 GB", "8 GB"], ThumbStore.CapChoicesMegabytes.Select(ThumbOption.CapText));
        Assert.Equal("1.5 GB", ThumbOption.CapText(1500));
    }

    [Fact]
    public void The_size_now_is_counted_as_the_limit_is_so_a_full_store_reads_as_its_limit()
    {
        Assert.Equal("0 MB of 500 MB", ThumbOption.SizeNowText(0, 500));
        Assert.Equal("less than 1 MB of 500 MB", ThumbOption.SizeNowText(300_000, 500));
        Assert.Equal("123 MB of 500 MB", ThumbOption.SizeNowText(123_456_789, 500));
        // One unit for the limit and the size: a store filled to its limit reads as its limit, and never more.
        Assert.Equal("500 MB of 500 MB", ThumbOption.SizeNowText(ThumbStore.CapBytesFor(500), 500));
        Assert.Equal("499 MB of 500 MB", ThumbOption.SizeNowText(ThumbStore.CapBytesFor(500) - 1, 500));
        Assert.Equal("1.2 GB of 2 GB", ThumbOption.SizeNowText(1_234_000_000, 2000));
        Assert.Equal("1.9 GB of 2 GB", ThumbOption.SizeNowText(ThumbStore.CapBytesFor(2000) - 1, 2000));
        Assert.Equal("2.0 GB of 2 GB", ThumbOption.SizeNowText(ThumbStore.CapBytesFor(2000), 2000));
        Assert.Equal("", ThumbOption.SizeNowText(-1, 500));
    }

    const string Gain = "Thumbnails you have seen before appear at once, also after a restart, because small copies are kept on disk. ";

    [Fact]
    public void Before_indexing_is_complete_the_card_says_the_gain_and_does_not_guess_the_cost()
    {
        const string waiting = Gain + "The space this needs is worked out when indexing has finished.";
        Assert.Equal(waiting, ThumbOption.EstimateText(null, indexingComplete: false, 500, 256));
        Assert.Equal(waiting, ThumbOption.EstimateText(Census(1000), indexingComplete: false, 500, 256));
        Assert.Equal(waiting, ThumbOption.EstimateText(null, indexingComplete: true, 500, 256));
    }

    [Fact]
    public void Once_indexing_is_complete_the_card_says_the_gain_then_the_cost_on_this_PC_then_the_limit()
    {
        // 100,000 photos at 13 KB (class 256): 1,300,000,000 bytes, "about 1.3 GB". From 10,000 up the count is rounded.
        Assert.Equal(Gain + "For the roughly 100,000 pictures, videos and documents on this PC they could take about 1.3 GB; " +
                     "Rock Preview keeps at most 500 MB and drops the oldest when that is full.",
            ThumbOption.EstimateText(Census(100_000), indexingComplete: true, 500, 256));
        // At a thumbnail size of the large class the same files take about three times as much (40 KB each).
        Assert.Contains("could take about 4.0 GB;", ThumbOption.EstimateText(Census(100_000), true, 500, 400));
        Assert.Contains("keeps at most 2 GB and", ThumbOption.EstimateText(Census(100_000), true, 2000, 256));
        // One file: the grammar of one. Too little to put a figure on: never "about under 10 MB".
        Assert.Equal(Gain + "For the 1 picture, video or document on this PC it could take less than 10 MB; " +
                     "Rock Preview keeps at most 500 MB and drops the oldest when that is full.",
            ThumbOption.EstimateText(Census(1), true, 500, 256));
        Assert.Equal(Gain + "No pictures, videos or documents were found on this PC, so this takes almost no space; " +
                     "Rock Preview keeps at most 500 MB.",
            ThumbOption.EstimateText(Census(0), true, 500, 256));
        // Every kind counts toward the number and the size; below 10,000 the number is exact.
        string mixed = ThumbOption.EstimateText(Census(1000, videos: 200, documents: 800), true, 500, 256);
        Assert.Contains("For the 2,000 pictures, videos and documents on this PC they could take about 28 MB;", mixed); // 1,000 × 13 KB + 200 × 7 KB + 800 × 17 KB
    }

    [Theory]
    [InlineData(9_999, "the 9,999 pictures")]
    [InlineData(10_000, "the roughly 10,000 pictures")]
    [InlineData(10_499, "the roughly 10,000 pictures")]
    [InlineData(372_083, "the roughly 372,000 pictures")]
    [InlineData(372_500, "the roughly 373,000 pictures")]
    public void A_large_count_is_rounded_to_the_thousand_so_it_does_not_change_at_every_refresh(long files, string expected)
        => Assert.Contains(expected, ThumbOption.EstimateText(Census(files), true, 500, 256));

    [Fact]
    public void The_bar_says_the_limit_when_this_PCs_files_would_need_that_much_or_more()
    {
        // 100,000 photos at class 160: about 1.3 GB, above a 500 MB limit. The store never grows past the limit, so
        // that is the figure, and the estimate is not said.
        Assert.Equal("Thumbnails can appear at once if Rock Preview keeps small copies of them on disk, using at most 500 MB. " +
                     "You choose the limit and the folder.", ThumbOption.RecommendationText(Census(100_000), 160, 500));
        // Just above the limit: the limit (38,462 photos at 13 KB are 500,006,000 bytes).
        Assert.Contains("using at most 500 MB.", ThumbOption.RecommendationText(Census(38_462), 160, 500));
        // Exactly at the limit: the limit too. 1,000 photos at 13 KB are 13,000,000 bytes, so a 13 MB limit (not one
        // Settings offers, but the arithmetic is the same) is met to the byte.
        Assert.Equal(13_000_000, Census(1000).BytesAt(160));
        Assert.Contains("using at most 13 MB.", ThumbOption.RecommendationText(Census(1000), 160, 13));
        // One photo fewer is under it, so the estimate is said.
        Assert.Contains("using about 13 MB.", ThumbOption.RecommendationText(Census(999), 160, 13));
    }

    [Fact]
    public void The_bar_says_about_how_much_when_this_PCs_files_need_less_than_the_limit()
    {
        Assert.Equal("Thumbnails can appear at once if Rock Preview keeps small copies of them on disk, using about 1.3 GB. " +
                     "You choose the limit and the folder.", ThumbOption.RecommendationText(Census(100_000), 160, 2000));
        Assert.Equal("Thumbnails can appear at once if Rock Preview keeps small copies of them on disk, using about 28 MB. " +
                     "You choose the limit and the folder.", ThumbOption.RecommendationText(Census(1000, videos: 200, documents: 800), 160, 500));
    }

    [Fact]
    public void The_bar_says_less_than_10_MB_for_very_few_files()
    {
        Assert.Equal("Thumbnails can appear at once if Rock Preview keeps small copies of them on disk, using less than 10 MB. " +
                     "You choose the limit and the folder.", ThumbOption.RecommendationText(Census(500), 160, 500));
    }

    [Fact]
    public void Turning_off_says_how_much_space_it_frees_when_that_is_known()
    {
        Assert.Contains("which frees 123 MB.", ThumbOption.TurnOffQuestion(123_456_789));
        Assert.Contains("normal way", ThumbOption.TurnOffQuestion(123_456_789));
        Assert.DoesNotContain("frees", ThumbOption.TurnOffQuestion(-1));
        Assert.DoesNotContain("frees", ThumbOption.TurnOffQuestion(1000));
    }

    [Fact]
    public void A_location_that_cannot_be_used_says_so_and_a_working_one_says_nothing()
    {
        Assert.Equal("", ThumbOption.ProblemText(ThumbStoreProblem.None));
        Assert.Equal("", ThumbOption.ProblemText(ThumbStoreProblem.Off));
        Assert.Contains("can't be used", ThumbOption.ProblemText(ThumbStoreProblem.FolderUnusable));
        Assert.Contains("Another copy of Rock Preview", ThumbOption.ProblemText(ThumbStoreProblem.InUse));
        Assert.Equal("", ThumbOption.FreeSpaceText(-1));
        Assert.Equal("120 GB free on this drive", ThumbOption.FreeSpaceText(120_000_000_000));
    }

    [Fact]
    public void A_location_kept_by_another_account_explains_why_to_choose_another()
        => Assert.Equal("Another Windows account keeps its thumbnails in this location, so Rock Preview doesn't keep any there. Choose another location.",
            ThumbOption.ProblemText(ThumbStoreProblem.OtherAccount));

    [Fact]
    public void Free_space_is_read_for_an_existing_folder_and_unknown_for_none()
    {
        Assert.True(ThumbOption.FreeBytes(Path.GetTempPath()) > 0);
        Assert.Equal(-1, ThumbOption.FreeBytes(""));
    }

    [Fact]
    public void The_bar_shows_only_when_every_condition_holds()
    {
        Assert.True(ThumbOption.ShouldRecommend(optionOn: false, shownBefore: false, demo: false, indexingComplete: true, estimateExists: true, waits: 3));
        Assert.True(ThumbOption.ShouldRecommend(false, false, false, true, true, 7));
        Assert.False(ThumbOption.ShouldRecommend(true, false, false, true, true, 3));   // already on
        Assert.False(ThumbOption.ShouldRecommend(false, true, false, true, true, 3));   // shown once already
        Assert.False(ThumbOption.ShouldRecommend(false, false, true, true, true, 3));   // demo
        Assert.False(ThumbOption.ShouldRecommend(false, false, false, false, true, 3)); // still indexing
        Assert.False(ThumbOption.ShouldRecommend(false, false, false, true, false, 3)); // nothing counted yet
        Assert.False(ThumbOption.ShouldRecommend(false, false, false, true, true, 2));  // waited only twice
    }
}

public sealed class ThumbFillWatchTests
{
    [Fact]
    public void A_screen_that_fills_within_a_second_is_no_wait()
    {
        var watch = new ThumbFillWatch();
        Assert.False(watch.Observe(0, 60));
        Assert.True(watch.Running);
        Assert.False(watch.Observe(500, 12));
        Assert.False(watch.Observe(1000, 0)); // exactly a second: not more than one
        Assert.False(watch.Running);
    }

    [Fact]
    public void A_screen_slower_than_a_second_is_one_wait_reported_once()
    {
        var watch = new ThumbFillWatch();
        Assert.False(watch.Observe(10_000, 60));
        Assert.False(watch.Observe(10_900, 30));
        Assert.True(watch.Observe(11_001, 20));  // passed the second while still filling
        Assert.False(watch.Observe(11_500, 5));
        Assert.False(watch.Observe(12_000, 0));  // filled at last: already reported
        // The next stretch is timed afresh.
        Assert.False(watch.Observe(20_000, 3));
        Assert.True(watch.Observe(21_500, 0));   // filled late, first seen at the end: reported then
    }

    [Fact]
    public void A_screen_that_never_fills_is_a_wait_and_hiding_it_ends_the_stretch_without_one()
    {
        var watch = new ThumbFillWatch();
        Assert.False(watch.Observe(0, 1));
        Assert.True(watch.Observe(5_000, 1));
        Assert.False(watch.Observe(9_000, 1));
        watch.Reset();
        Assert.False(watch.Running);
        Assert.False(watch.Observe(9_100, 4));
        watch.Reset(); // hidden after 0.4 s
        Assert.False(watch.Observe(9_600, 0));
    }
}

public sealed class ThumbWaitOccasionsTests
{
    [Fact]
    public void Waits_close_together_are_one_occasion()
    {
        var occasions = new ThumbWaitOccasions();
        Assert.True(occasions.TryCount(1_000));
        Assert.False(occasions.TryCount(1_000 + 60_000));                          // a minute later: the same occasion
        Assert.False(occasions.TryCount(1_000 + ThumbWaitOccasions.MinGapMs - 1));
        Assert.True(occasions.TryCount(1_000 + ThumbWaitOccasions.MinGapMs));      // five minutes after the first counted
        Assert.False(occasions.TryCount(1_000 + ThumbWaitOccasions.MinGapMs + 10));
    }

    [Fact]
    public void The_first_wait_always_counts_whatever_the_clock_says()
    {
        Assert.True(new ThumbWaitOccasions().TryCount(0));
        Assert.True(new ThumbWaitOccasions().TryCount(-5));
        Assert.True(new ThumbWaitOccasions().TryCount(long.MaxValue));
    }
}

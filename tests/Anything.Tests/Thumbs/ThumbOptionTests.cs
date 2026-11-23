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
    [InlineData("1.5", 500)]     // not a whole number of megabytes
    // What 1.7.30 and earlier wrote reads exactly as it was.
    [InlineData("250", 250)]
    [InlineData("500", 500)]
    [InlineData(" 1000 ", 1000)]
    [InlineData("2000", 2000)]
    [InlineData("4000", 4000)]
    [InlineData("8000", 8000)]
    // Any other whole number is kept, within the bounds.
    [InlineData("1024", 1024)]
    [InlineData("1500", 1500)]
    [InlineData("8192", 8192)]
    [InlineData("100000", 100000)]
    [InlineData("100", 100)]
    [InlineData("99", 100)]
    [InlineData("-5", 100)]
    [InlineData("4000000", 4_000_000)]
    [InlineData("9999999", 4_000_000)]
    [InlineData("99999999999999", 4_000_000)]
    public void The_limit_from_the_ini_is_kept_within_its_bounds(string? value, int expected)
        => Assert.Equal(expected, ThumbOption.CapMegabytesFrom(value));

    [Theory]
    [InlineData("8", 8000)]
    [InlineData("8 GB", 8000)]
    [InlineData("8gb", 8000)]
    [InlineData(" 8 Gb ", 8000)]
    [InlineData("0.5", 500)]
    [InlineData("0,5", 500)]
    [InlineData(".5", 500)]
    [InlineData("1.25", 1250)]
    [InlineData("1,25", 1250)]
    [InlineData("1,2345", 1235)]      // a comma, but not three digits after it: decimals
    [InlineData("1000,5", 1_000_500)] // four digits before the comma: decimals
    [InlineData("1.000", 1000)]       // a point is always a decimal point
    [InlineData("500 MB", 500)]
    [InlineData("500mb", 500)]
    [InlineData("1.5 TB", 1_500_000)]
    [InlineData("1,5tb", 1_500_000)]
    [InlineData("120", 120_000)]
    [InlineData("0.0004", 100)]       // a fraction of a megabyte: the smallest limit
    [InlineData("0", 100)]
    [InlineData("50 MB", 100)]
    [InlineData("5 TB", 4_000_000)]   // above the bound: the bound
    [InlineData("99999999", 4_000_000)]
    public void A_typed_size_is_read_in_GB_unless_it_says_otherwise(string text, int expected)
    {
        Assert.True(ThumbOption.TryParseSize(text, out int mb));
        Assert.Equal(expected, mb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GB")]
    [InlineData("-8")]
    [InlineData("8 KB")]
    [InlineData("8 GiB")]
    [InlineData("8 G")]
    [InlineData("1.5.2")]
    [InlineData("1,000.5")]
    // A comma with exactly three digits after it may group thousands: not guessed as a decimal comma.
    [InlineData("1,000")]
    [InlineData("1,000 GB")]
    [InlineData("12,500")]
    [InlineData("250,000 MB")]
    [InlineData(" 1,500tb ")]
    [InlineData("8 8")]
    [InlineData("eight")]
    [InlineData("1e3")]
    public void Anything_else_typed_is_not_a_size(string? text)
        => Assert.False(ThumbOption.TryParseSize(text, out _));

    [Fact]
    public void A_thousands_comma_puts_the_old_limit_back()
        => Assert.Equal(8000, ThumbOption.LimitTyped("1,000", 8000));

    [Theory]
    // Lowered below what the store holds: asked.
    [InlineData(8000, 2000, 3_000_000_000L, true)]
    [InlineData(8000, 2000, 2_000_000_001L, true)]
    // Lowered to exactly what it holds, or to more: it still fits, nothing is deleted.
    [InlineData(8000, 2000, 2_000_000_000L, false)]
    [InlineData(8000, 2000, 1_500_000_000L, false)]
    [InlineData(8000, 2000, 0L, false)]
    // Raised, or the same: never asked, even if the store holds more than the new limit.
    [InlineData(2000, 8000, 3_000_000_000L, false)]
    [InlineData(2000, 2000, 3_000_000_000L, false)]
    [InlineData(500, 1000, 9_000_000_000L, false)]
    // The store's size not known: not asked.
    [InlineData(8000, 2000, -1L, false)]
    public void A_lower_limit_is_asked_about_only_when_the_store_holds_more(int oldMb, int newMb, long storeBytes, bool ask)
        => Assert.Equal(ask, ThumbOption.AskBeforeLowering(oldMb, newMb, storeBytes));

    [Fact]
    public void The_question_before_lowering_says_what_the_store_holds_and_the_new_limit()
        => Assert.Equal("The store holds 3.0 GB now. With a limit of 2 GB, the oldest thumbnails are deleted until it fits. Lower the limit?",
            ThumbOption.LowerLimitQuestion(3_000_000_000, 2000));

    // The size is rounded up and the limit said exactly, so a store just over the limit never reads the same as it.
    [Theory]
    [InlineData(2_050_000_000L, 2000, "2.1 GB", "2 GB")]
    [InlineData(2_001_000_000L, 2000, "2.1 GB", "2 GB")]
    [InlineData(2_051_000_000L, 2050, "2.1 GB", "2.05 GB")]
    [InlineData(501_000_000L, 500, "501 MB", "500 MB")]
    [InlineData(100_001_000_000L, 100_000, "101 GB", "100 GB")]
    [InlineData(1_001_000_000_000L, 1_000_000, "1.1 TB", "1 TB")]
    public void The_question_before_lowering_shows_a_store_just_over_the_limit_as_more(long storeBytes, int newMb, string held, string limit)
        => Assert.Equal($"The store holds {held} now. With a limit of {limit}, the oldest thumbnails are deleted until it fits. Lower the limit?",
            ThumbOption.LowerLimitQuestion(storeBytes, newMb));

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
    public void Limits_read_well_at_any_size()
    {
        Assert.Equal("100 MB", ThumbOption.CapText(100));
        Assert.Equal("250 MB", ThumbOption.CapText(250));
        Assert.Equal("1 GB", ThumbOption.CapText(1000));
        Assert.Equal("8 GB", ThumbOption.CapText(8000));
        Assert.Equal("999 MB", ThumbOption.CapText(999));
        Assert.Equal("1.5 GB", ThumbOption.CapText(1500));
        Assert.Equal("120 GB", ThumbOption.CapText(120_000));
        Assert.Equal("999.9 GB", ThumbOption.CapText(999_900));
        Assert.Equal("1 TB", ThumbOption.CapText(999_960));
        Assert.Equal("2 TB", ThumbOption.CapText(2_000_000));
        Assert.Equal("1.5 TB", ThumbOption.CapText(1_500_000));
        Assert.Equal("4 TB", ThumbOption.CapText(ThumbStore.MaxCapMegabytes));
    }

    [Fact]
    public void A_stores_size_reads_well_at_any_size_and_never_more_than_it_is()
    {
        Assert.Equal("999 MB", ThumbOption.StoreSizeText(999_999_999));
        Assert.Equal("1.0 GB", ThumbOption.StoreSizeText(1_000_000_000));
        Assert.Equal("99.9 GB", ThumbOption.StoreSizeText(99_999_999_999));
        Assert.Equal("120 GB", ThumbOption.StoreSizeText(120_900_000_000));
        Assert.Equal("999 GB", ThumbOption.StoreSizeText(999_999_999_999));
        Assert.Equal("1.0 TB", ThumbOption.StoreSizeText(1_000_000_000_000));
        Assert.Equal("1.9 TB", ThumbOption.StoreSizeText(1_999_999_999_999));
        Assert.Equal("120 GB of 120 GB", ThumbOption.SizeNowText(ThumbStore.CapBytesFor(120_000), 120_000));
        Assert.Equal("2.0 TB of 2 TB", ThumbOption.SizeNowText(ThumbStore.CapBytesFor(2_000_000), 2_000_000));
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

    [Fact]
    public void The_size_now_says_what_each_kind_takes_when_more_than_one_holds_something()
    {
        Assert.Equal("1.2 GB of 2 GB: pictures 900 MB, videos 100 MB, documents 200 MB",
            ThumbOption.SizeNowText(1_200_000_000, 2000, new ThumbStoreSplit(900_000_000, 100_000_000, 200_000_000, 0)));
        // Thumbnails from before this version carry no kind: they are said apart, and kinds holding nothing are not named.
        Assert.Equal("400 MB of 500 MB: pictures 100 MB, saved by an earlier version 300 MB",
            ThumbOption.SizeNowText(400_000_000, 500, new ThumbStoreSplit(100_000_000, 0, 0, 300_000_000)));
        // One kind alone: nothing to split.
        Assert.Equal("123 MB of 500 MB", ThumbOption.SizeNowText(123_456_789, 500, new ThumbStoreSplit(123_456_789, 0, 0, 0)));
        Assert.Equal("0 MB of 500 MB", ThumbOption.SizeNowText(0, 500, default));
        Assert.Equal("", ThumbOption.SizeNowText(-1, 500, new ThumbStoreSplit(1, 1, 0, 0)));
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
        Assert.Contains("keeps at most 120 GB and", ThumbOption.EstimateText(Census(100_000), true, 120_000, 256));
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

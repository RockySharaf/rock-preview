using Anything.Core.Search;

namespace Anything.Tests.Search;

/// <summary><see cref="SearchHistory"/>: the searches the search box's list remembers.</summary>
public sealed class SearchHistoryTests
{
    [Fact]
    public void Newest_search_is_first()
    {
        var h = new SearchHistory();
        Assert.True(h.Add("invoice"));
        Assert.True(h.Add("photos 2024"));
        Assert.Equal(["photos 2024", "invoice"], h.Items);
    }

    [Fact]
    public void Searches_are_trimmed_and_blank_ones_are_not_kept()
    {
        var h = new SearchHistory();
        Assert.False(h.Add(""));
        Assert.False(h.Add("   "));
        Assert.False(h.Add(null));
        Assert.True(h.Add("  report  "));
        Assert.Equal(["report"], h.Items);
    }

    [Fact]
    public void Text_on_more_than_one_line_is_not_kept()
    {
        var h = new SearchHistory();
        Assert.False(h.Add("one\r\ntwo"));
        Assert.False(h.Add("one\ntwo"));
        Assert.Empty(h.Items);
    }

    [Fact]
    public void A_repeat_in_another_case_moves_to_the_top_as_typed_this_time()
    {
        var h = new SearchHistory();
        h.Add("Report");
        h.Add("invoice");
        h.Add("budget");
        Assert.True(h.Add("REPORT"));
        Assert.Equal(["REPORT", "budget", "invoice"], h.Items);
    }

    [Fact]
    public void The_same_search_again_at_the_top_changes_nothing()
    {
        var h = new SearchHistory();
        h.Add("report");
        Assert.False(h.Add("report"));
        Assert.False(h.Add(" report "));
        Assert.Equal(["report"], h.Items);
    }

    [Fact]
    public void Only_the_newest_are_kept()
    {
        var h = new SearchHistory();
        for (int i = 1; i <= 25; i++) h.Add("search " + i);
        Assert.Equal(SearchHistory.DefaultCapacity, h.Count);
        Assert.Equal("search 25", h.Items[0]);
        Assert.Equal("search 6", h.Items[^1]);
    }

    [Fact]
    public void A_smaller_capacity_is_kept_to()
    {
        var h = new SearchHistory(2);
        h.Add("a");
        h.Add("b");
        h.Add("c");
        Assert.Equal(["c", "b"], h.Items);
    }

    [Fact]
    public void Capacity_must_be_at_least_one() => Assert.Throws<ArgumentOutOfRangeException>(() => new SearchHistory(0));

    [Fact]
    public void Remove_takes_out_a_search_in_any_case()
    {
        var h = new SearchHistory();
        h.Add("invoice");
        h.Add("Report");
        Assert.True(h.Remove("report"));
        Assert.Equal(["invoice"], h.Items);
        Assert.False(h.Remove("report"));
        Assert.False(h.Remove(""));
        Assert.False(h.Remove(null));
    }

    [Fact]
    public void Clear_forgets_every_search()
    {
        var h = new SearchHistory();
        Assert.False(h.Clear());
        h.Add("a");
        h.Add("b");
        Assert.True(h.Clear());
        Assert.Empty(h.Items);
    }

    [Fact]
    public void Load_keeps_the_saved_order_and_drops_blanks_and_repeats()
    {
        var h = new SearchHistory();
        h.Add("left over");
        h.Load(["newest", "", "Middle", "  ", "middle", "oldest"]);
        Assert.Equal(["newest", "Middle", "oldest"], h.Items);
    }

    [Fact]
    public void Load_keeps_only_the_newest_past_capacity()
    {
        var h = new SearchHistory(3);
        h.Load(["1", "2", "3", "4", "5"]);
        Assert.Equal(["1", "2", "3"], h.Items);
    }

    [Fact]
    public void Searches_with_bars_and_equals_signs_are_kept_whole_in_the_list()
    {
        var h = new SearchHistory();
        h.Add("jpg | png size:>1mb");
        h.Add("a=b");
        Assert.Equal(["a=b", "jpg | png size:>1mb"], h.Items);
    }

    [Fact]
    public void Saved_searches_have_a_key_each_and_come_back_whole()
    {
        var h = new SearchHistory();
        h.Load(["jpg | png size:>1mb", "a=b", "report"]);
        var saved = h.ToSettings(remember: true).ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("3", saved[SearchHistory.CountKey]);
        Assert.Equal("jpg | png size:>1mb", saved[SearchHistory.SettingsKey(0)]);
        Assert.Equal("a=b", saved[SearchHistory.SettingsKey(1)]);

        var back = new SearchHistory();
        back.LoadSettings(k => saved.TryGetValue(k, out var v) ? v : null);
        Assert.Equal(h.Items, back.Items);
    }

    [Fact]
    public void With_remember_searches_off_nothing_is_saved()
    {
        var h = new SearchHistory();
        h.Add("invoice");
        Assert.Equal([new KeyValuePair<string, string>(SearchHistory.CountKey, "0")], h.ToSettings(remember: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("-4")]
    public void A_missing_or_broken_count_loads_nothing(string? count)
    {
        var h = new SearchHistory();
        h.Add("old");
        h.LoadSettings(k => k == SearchHistory.CountKey ? count : "report");
        Assert.Empty(h.Items);
    }

    [Fact]
    public void A_count_past_the_limit_reads_no_more_than_the_limit()
    {
        var asked = new List<string>();
        var h = new SearchHistory();
        h.LoadSettings(k => { asked.Add(k); return k == SearchHistory.CountKey ? " 999999 " : null; });
        Assert.Equal(SearchHistory.MaxSaved + 1, asked.Count);
        Assert.Empty(h.Items); // the keys aren't there
    }
}

using Anything.Core;
using Box = Anything.Core.HelpWindowPlacement.Box;

namespace Anything.Tests;

/// <summary>
/// <see cref="HelpWindowPlacement"/>: where the Search Syntax help opens. It must stay on the screen and leave the
/// search box free whenever the screen has any other room.
/// </summary>
public sealed class HelpWindowPlacementTests
{
    static int S(int v, double scale) => (int)Math.Round(v * scale);

    /// <summary>The help's wanted and minimum outer sizes and its gap at a scaling (720 x 760 client, 420 x 240 minimum).</summary>
    static ((int, int) Wanted, (int, int) Minimum, int Gap) Sizes(double scale) =>
        ((S(736, scale), S(800, scale)), (S(436, scale), S(280, scale)), S(8, scale));

    /// <summary>The search box at the top right of a window, as MainForm lays it out.</summary>
    static Box SearchBoxOf(Box owner, double scale) =>
        new(owner.Right - S(16, scale) - S(300, scale), owner.Y + S(48, scale), S(300, scale), S(32, scale));

    public static TheoryData<double> Scales() => [1.0, 1.25, 1.5, 2.0];

    [Theory, MemberData(nameof(Scales))]
    public void On_a_full_hd_screen_it_sits_under_the_search_box_right_aligned_inside_the_window(double scale)
    {
        var area = new Box(0, 0, 1920, 1040);
        var owner = new Box(60, 40, 1700, 960);
        var box = SearchBoxOf(owner, scale);
        var (wanted, minimum, gap) = Sizes(scale);
        var r = HelpWindowPlacement.Place(owner, box, area, wanted, minimum, gap);
        Assert.True(r.Inside(area));
        Assert.False(r.Intersects(box));
        Assert.Equal(box.Bottom + gap, r.Y);
        Assert.Equal(box.Right, r.Right);
        Assert.True(r.X >= owner.X && r.Bottom <= owner.Bottom - gap);
        Assert.True(r.Width >= minimum.Item1 && r.Height >= minimum.Item2);
    }

    [Theory, MemberData(nameof(Scales))]
    public void On_a_small_screen_it_fits_and_still_leaves_the_box_free(double scale)
    {
        var area = new Box(0, 0, 1366, 728);
        var owner = area; // maximised
        var box = SearchBoxOf(owner, scale);
        var (wanted, minimum, gap) = Sizes(scale);
        var r = HelpWindowPlacement.Place(owner, box, area, wanted, minimum, gap);
        Assert.True(r.Inside(area));
        Assert.False(r.Intersects(box));
        Assert.True(r.Width >= 1 && r.Height >= 1);
    }

    [Fact]
    public void A_short_window_lets_it_reach_below_rather_than_cover_the_box()
    {
        var area = new Box(0, 0, 1920, 1040);
        var owner = new Box(400, 100, 1000, 200);
        var box = SearchBoxOf(owner, 1);
        var (wanted, minimum, gap) = Sizes(1);
        var r = HelpWindowPlacement.Place(owner, box, area, wanted, minimum, gap);
        Assert.Equal(box.Bottom + gap, r.Y);
        Assert.True(r.Bottom > owner.Bottom && r.Inside(area));
        Assert.False(r.Intersects(box));
    }

    [Fact]
    public void A_window_at_the_bottom_of_the_screen_gets_it_beside_it()
    {
        var area = new Box(0, 0, 1920, 1040);
        var owner = new Box(0, 880, 1000, 160);
        var box = SearchBoxOf(owner, 1);
        var (wanted, minimum, gap) = Sizes(1);
        var r = HelpWindowPlacement.Place(owner, box, area, wanted, minimum, gap);
        Assert.Equal(owner.Right + gap, r.X);
        Assert.True(r.Inside(area));
        Assert.False(r.Intersects(box));
        Assert.True(r.Width >= minimum.Item1 && r.Height >= minimum.Item2);
    }

    [Fact]
    public void A_window_at_the_right_edge_gets_it_on_its_left_when_there_is_no_room_below()
    {
        var area = new Box(0, 0, 1920, 1040);
        var owner = new Box(1000, 880, 920, 160);
        var box = SearchBoxOf(owner, 1);
        var (wanted, minimum, gap) = Sizes(1);
        var r = HelpWindowPlacement.Place(owner, box, area, wanted, minimum, gap);
        Assert.Equal(owner.X - gap, r.Right);
        Assert.True(r.Inside(area));
        Assert.False(r.Intersects(box));
    }

    [Fact]
    public void A_second_screen_to_the_left_with_negative_coordinates_works_the_same()
    {
        var area = new Box(-2560, 0, 2560, 1400);
        var owner = new Box(-2400, 100, 1800, 1100);
        var box = SearchBoxOf(owner, 1.5);
        var (wanted, minimum, gap) = Sizes(1.5);
        var r = HelpWindowPlacement.Place(owner, box, area, wanted, minimum, gap);
        Assert.True(r.Inside(area));
        Assert.Equal(box.Right, r.Right);
        Assert.Equal(box.Bottom + gap, r.Y);
    }

    [Fact]
    public void A_screen_smaller_than_the_minimum_still_gets_a_window_on_it()
    {
        var area = new Box(0, 0, 300, 200);
        var owner = area;
        var box = new Box(10, 10, 280, 30);
        var r = HelpWindowPlacement.Place(owner, box, area, (736, 800), (436, 280), 8);
        Assert.True(r.Inside(area));
        Assert.True(r.Width >= 1 && r.Height >= 1);
        Assert.False(r.Intersects(box)); // there is room below the box, though less than the minimum
    }

    [Fact]
    public void Clamp_keeps_a_box_on_the_screen_and_never_smaller_than_one_pixel()
    {
        var area = new Box(0, 0, 100, 100);
        Assert.Equal(new Box(0, 0, 100, 100), HelpWindowPlacement.Clamp(new Box(-50, -50, 500, 500), area));
        Assert.Equal(new Box(90, 90, 10, 10), HelpWindowPlacement.Clamp(new Box(200, 200, 10, 10), area));
        Assert.Equal(new Box(0, 0, 1, 1), HelpWindowPlacement.Clamp(new Box(0, 0, 0, -5), area));
    }
}

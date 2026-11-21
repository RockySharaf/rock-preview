using Anything.Core;

namespace Anything.Tests;

/// <summary>Which folders the status bar stops adding up selection sizes in, because doing so ran out of time there.</summary>
public sealed class SelectionSizeMemoryTests
{
    sealed class Place;

    [Fact]
    public void Nothing_is_slow_at_first()
    {
        var m = new SelectionSizeMemory<Place>();
        Assert.False(m.IsSlow(new Place(), 1));
        Assert.False(m.IsSlow(null, 1));
    }

    [Fact]
    public void Running_out_after_many_items_makes_the_folder_slow_for_selections_as_big()
    {
        var m = new SelectionSizeMemory<Place>();
        var here = new Place();
        m.RanOut(here, selected: 500, reached: SelectionSizeMemory<Place>.SlowAfterItems);
        Assert.True(m.IsSlow(here, 500));
        Assert.True(m.IsSlow(here, 900));
        Assert.False(m.IsSlow(here, 499));
        Assert.False(m.IsSlow(new Place(), 500)); // another folder, or this one opened again
    }

    [Fact]
    public void One_slow_item_on_its_own_hides_nothing_later()
    {
        var m = new SelectionSizeMemory<Place>();
        var here = new Place();
        m.RanOut(here, selected: 300, reached: 3);
        Assert.False(m.IsSlow(here, 300));
        Assert.False(m.IsSlow(here, 1));
    }

    [Fact]
    public void Running_out_twice_in_a_folder_where_every_item_is_slow_makes_it_slow()
    {
        var m = new SelectionSizeMemory<Place>(); // a phone: the time runs out after a few items every time
        var phone = new Place();
        m.RanOut(phone, selected: 40, reached: 5, item: "IMG_0005.JPG");
        m.RanOut(phone, selected: 20, reached: 3, item: "IMG_0103.JPG");
        Assert.True(m.IsSlow(phone, 3));
        Assert.True(m.IsSlow(phone, 40));
        Assert.False(m.IsSlow(phone, 2)); // fewer than were ever added up in the time
    }

    [Fact]
    public void Running_out_again_and_again_at_the_same_slow_item_is_once()
    {
        var m = new SelectionSizeMemory<Place>(); // B is slow every time it is read; A and C are not
        var here = new Place();
        m.RanOut(here, selected: 2, reached: 2, item: "B"); // A, then Ctrl+click B
        m.RanOut(here, selected: 3, reached: 2, item: "B"); // Ctrl+click C
        m.RanOut(here, selected: 4, reached: 3, item: "B");
        Assert.False(m.IsSlow(here, 1));
        Assert.False(m.IsSlow(here, 2));
        Assert.False(m.IsSlow(here, 4));
        m.RanOut(here, selected: 5, reached: 4, item: "D"); // another item slow too: the folder is
        Assert.True(m.IsSlow(here, 2));
    }

    [Fact]
    public void Running_out_with_the_item_not_known_counts_each_time()
    {
        var m = new SelectionSizeMemory<Place>();
        var here = new Place();
        m.RanOut(here, selected: 6, reached: 3, item: "B");
        m.RanOut(here, selected: 6, reached: 4);
        Assert.True(m.IsSlow(here, 3));
    }

    [Fact]
    public void Running_out_before_the_first_item_twice_hides_every_selection_there()
    {
        var m = new SelectionSizeMemory<Place>();
        var ftp = new Place();
        m.RanOut(ftp, selected: 4, reached: 0);
        m.RanOut(ftp, selected: 2, reached: 0);
        Assert.True(m.IsSlow(ftp, 1));
    }

    [Fact]
    public void Running_out_once_in_each_of_two_folders_is_not_twice()
    {
        var m = new SelectionSizeMemory<Place>();
        Place a = new(), b = new();
        m.RanOut(a, selected: 30, reached: 4);
        m.RanOut(b, selected: 30, reached: 4);
        Assert.False(m.IsSlow(a, 30));
        Assert.False(m.IsSlow(b, 30));
    }

    [Fact]
    public void A_smaller_slow_selection_lowers_the_threshold_and_a_bigger_one_keeps_it()
    {
        var m = new SelectionSizeMemory<Place>();
        var here = new Place();
        m.RanOut(here, selected: 800, reached: 60);
        m.RanOut(here, selected: 200, reached: 70);
        Assert.True(m.IsSlow(here, 200));
        m.RanOut(here, selected: 1000, reached: 55);
        Assert.True(m.IsSlow(here, 200));
    }

    [Fact]
    public void A_folder_with_no_place_is_never_remembered()
    {
        var m = new SelectionSizeMemory<Place>();
        m.RanOut(null, selected: 500, reached: 100);
        m.RanOut(null, selected: 500, reached: 100);
        Assert.False(m.IsSlow(null, 500));
    }
}

using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

public sealed class ThumbMemoryTests
{
    [Fact]
    public void The_least_recently_used_goes_first_when_over_budget()
    {
        var dropped = new List<string>();
        var memory = new ThumbMemory<int, string>(300, dropped.Add);
        memory.Set(1, "one", 100);
        memory.Set(2, "two", 100);
        memory.Set(3, "three", 100);
        Assert.True(memory.TryGet(1, out _)); // 1 used: 2 is now the oldest
        memory.Set(4, "four", 100);
        Assert.Equal(["two"], dropped);
        Assert.Equal([3, 1, 4], memory.Keys);
        Assert.Equal(300, memory.Bytes);
        Assert.Equal(3, memory.Count);
    }

    [Fact]
    public void Only_values_dropped_for_room_are_reported_as_evicted()
    {
        var evicted = new List<int>();
        var memory = new ThumbMemory<int, string>(200);
        memory.Evicted += evicted.Add;
        memory.Set(1, "one", 100);
        memory.Set(2, "two", 100);
        memory.Set(2, "two again", 100); // replaced: not evicted
        memory.Remove(2);                 // removed: not evicted
        memory.Set(3, "three", 100);
        memory.Set(4, "four", 100);       // no room: 1 goes
        Assert.Equal([1], evicted);
        memory.Budget = 100;              // 3 goes
        memory.Clear();                   // cleared: not evicted
        Assert.Equal([1, 3], evicted);
    }

    [Fact]
    public void Peek_and_contains_leave_the_order_alone()
    {
        var dropped = new List<string>();
        var memory = new ThumbMemory<int, string>(200, dropped.Add);
        memory.Set(1, "one", 100);
        memory.Set(2, "two", 100);
        Assert.True(memory.Peek(1, out var one));
        Assert.Equal("one", one);
        Assert.True(memory.Contains(1));
        memory.Set(3, "three", 100);
        Assert.Equal(["one"], dropped);
        Assert.False(memory.Peek(1, out _));
    }

    [Fact]
    public void Replacing_a_value_lets_the_old_one_go_and_counts_the_new_size()
    {
        var dropped = new List<string>();
        var memory = new ThumbMemory<int, string>(1000, dropped.Add);
        memory.Set(1, "low", 10);
        memory.Set(1, "final", 400);
        Assert.Equal(["low"], dropped);
        Assert.Equal(400, memory.Bytes);
        Assert.True(memory.TryGet(1, out var v));
        Assert.Equal("final", v);
        // The same value set again is not dropped (it is still held).
        memory.Set(1, "final", 400);
        Assert.Equal(["low"], dropped);
    }

    [Fact]
    public void A_value_bigger_than_the_budget_stays_until_the_next_one()
    {
        var dropped = new List<string>();
        var memory = new ThumbMemory<int, string>(100, dropped.Add);
        memory.Set(1, "small", 50);
        memory.Set(2, "huge", 500);
        Assert.Equal(["small"], dropped);
        Assert.True(memory.Contains(2));
        memory.Set(3, "next", 50);
        Assert.Equal(["small", "huge"], dropped);
        Assert.Equal([3], memory.Keys);
    }

    [Fact]
    public void Remove_clear_and_a_smaller_budget_pass_every_value_to_the_callback()
    {
        var dropped = new List<string>();
        var memory = new ThumbMemory<int, string>(1000, dropped.Add);
        for (int i = 0; i < 5; i++) memory.Set(i, "v" + i, 100);
        Assert.True(memory.Remove(2));
        Assert.False(memory.Remove(2));
        memory.Budget = 200; // the oldest go until it fits
        Assert.Equal(["v2", "v0", "v1"], dropped);
        memory.Clear();
        Assert.Equal(["v2", "v0", "v1", "v3", "v4"], dropped);
        Assert.Equal(0, memory.Count);
        Assert.Equal(0, memory.Bytes);
    }
}

public sealed class ThumbPaintTests
{
    [Fact]
    public void The_real_picture_at_this_size_wins()
    {
        Assert.Equal(ThumbDrawing.Exact, ThumbPaint.Choose(ThumbQuality.Final, null));
        Assert.Equal(ThumbDrawing.Exact, ThumbPaint.Choose(ThumbQuality.Final, ThumbQuality.Final));
        Assert.Equal(ThumbDrawing.Exact, ThumbPaint.Choose(ThumbQuality.None, ThumbQuality.Final)); // an icon is the answer
    }

    [Fact]
    public void A_stand_in_gives_way_to_the_real_picture_of_another_size()
    {
        Assert.Equal(ThumbDrawing.StandIn, ThumbPaint.Choose(ThumbQuality.Low, null));
        Assert.Equal(ThumbDrawing.Other, ThumbPaint.Choose(ThumbQuality.Low, ThumbQuality.Final));
        Assert.Equal(ThumbDrawing.StandIn, ThumbPaint.Choose(ThumbQuality.Low, ThumbQuality.Low));
        Assert.Equal(ThumbDrawing.StandIn, ThumbPaint.Choose(ThumbQuality.Low, ThumbQuality.None));
    }

    [Fact]
    public void With_nothing_at_this_size_another_size_stands_in_and_else_the_frame()
    {
        // The thumbnail size changed, or the file changed since: the old picture stays until the new one comes.
        Assert.Equal(ThumbDrawing.Other, ThumbPaint.Choose(null, ThumbQuality.Final));
        Assert.Equal(ThumbDrawing.Other, ThumbPaint.Choose(null, ThumbQuality.Low));
        Assert.Equal(ThumbDrawing.Other, ThumbPaint.Choose(null, ThumbQuality.None));
        Assert.Equal(ThumbDrawing.Nothing, ThumbPaint.Choose(null, null));
    }

    [Fact]
    public void Exact_pictures_are_never_enlarged_stand_ins_fill_the_cell_and_other_sizes_keep_their_proportion()
    {
        Assert.Equal(1f, ThumbPaint.Scale(ThumbDrawing.Exact, 160, 120, 160, 160));
        Assert.Equal(1f, ThumbPaint.Scale(ThumbDrawing.Exact, 32, 32, 160, 160));       // an icon stays its size
        Assert.Equal(0.5f, ThumbPaint.Scale(ThumbDrawing.Exact, 320, 200, 160, 160));   // only shrunk to fit
        Assert.Equal(2f, ThumbPaint.Scale(ThumbDrawing.StandIn, 80, 60, 160, 160));     // an EXIF preview fills the cell
        Assert.Equal(1.6f, ThumbPaint.Scale(ThumbDrawing.Other, 160, 100, 256, 160), 3); // 160 → 256: as it would have been made
        Assert.Equal(0.5f, ThumbPaint.Scale(ThumbDrawing.Other, 256, 200, 128, 256), 3); // 256 → 128
        Assert.Equal(1f, ThumbPaint.Scale(ThumbDrawing.Exact, 0, 0, 160, 160));
    }
}

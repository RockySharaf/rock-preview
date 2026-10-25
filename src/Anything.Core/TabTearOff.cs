using System.Drawing;

namespace Anything.Core;

/// <summary>
/// The geometry of dragging tabs out of a window and into another, as in Chrome, kept apart from the windows so it can
/// be tested: when a dragged tab leaves its strip, which slot it takes in a strip, where the pointer holds it, where the
/// window carrying it goes, and which window's strip is under the pointer.
/// </summary>
public static class TabTearOff
{
    /// <summary>How far past its strip (logical pixels; Chrome's 15, rounded) the pointer takes a dragged tab out of it.</summary>
    public const int DetachMargin = 16;

    /// <summary>
    /// Whether the pointer has taken a dragged tab out of its strip: more than <paramref name="margin"/> past one of its
    /// edges. Back on a strip, the tab joins it as soon as the pointer is on it, so a tab doesn't flicker in and out.
    /// </summary>
    public static bool LeavesStrip(Rectangle strip, Point pointer, int margin)
    {
        var area = strip;
        area.Inflate(margin, margin);
        return !area.Contains(pointer);
    }

    /// <summary>
    /// The slot (0 to <paramref name="slots"/> - 1) that <paramref name="x"/> falls in, for tabs <paramref name="tabWidth"/>
    /// wide from <paramref name="left"/>; the nearest end when it is past them.
    /// </summary>
    public static int SlotAt(int x, int left, int tabWidth, int slots)
        => (int)Math.Clamp(Math.Floor((x - left) / (double)Math.Max(1, tabWidth)), 0, Math.Max(0, slots - 1));

    /// <summary>
    /// Where in a tab the pointer holds it, as fractions of its width and height (0 to 1), so that a tab drawn wider in
    /// another window is still held at the same place.
    /// </summary>
    public static PointF HoldAt(Rectangle tab, Point pointer) => new(
        tab.Width > 0 ? Math.Clamp((pointer.X - tab.Left) / (float)tab.Width, 0, 1) : 0,
        tab.Height > 0 ? Math.Clamp((pointer.Y - tab.Top) / (float)tab.Height, 0, 1) : 0);

    /// <summary>The point of <paramref name="tab"/> that <paramref name="hold"/> (from <see cref="HoldAt"/>) is.</summary>
    public static Point HeldPoint(Rectangle tab, PointF hold)
        => new(tab.Left + (int)Math.Round(hold.X * tab.Width), tab.Top + (int)Math.Round(hold.Y * tab.Height));

    /// <summary>Where a window at <paramref name="window"/> goes so that its point <paramref name="held"/> (both on screen) comes under the pointer.</summary>
    public static Point FollowPointer(Point window, Point held, Point pointer)
        => new(window.X + pointer.X - held.X, window.Y + pointer.Y - held.Y);

    /// <summary>
    /// A top-level window as the search for the strip under the pointer sees it: its bounds on screen, its tab strip's
    /// when it is a Rock Preview window that can take a tab (else null), and whether it is passed over (the window being
    /// dragged, hidden, minimized, cloaked or click-through windows).
    /// </summary>
    public readonly record struct Layer(Rectangle Bounds, Rectangle? Strip, bool Ignored);

    /// <summary>
    /// Which window, in <paramref name="zOrder"/> (topmost first), takes a dragged tab at <paramref name="pointer"/>: the
    /// topmost window there that isn't passed over, when the pointer is on its tab strip. -1 when the window there is
    /// another program's (covering the strip), or the pointer is elsewhere on a Rock Preview window, or on no window.
    /// </summary>
    public static int StripUnder(IReadOnlyList<Layer> zOrder, Point pointer)
    {
        for (int i = 0; i < zOrder.Count; i++)
        {
            var w = zOrder[i];
            if (w.Ignored || !w.Bounds.Contains(pointer)) continue;
            return w.Strip is { } strip && strip.Contains(pointer) ? i : -1;
        }
        return -1;
    }
}

using System.Drawing;

namespace Anything.Core;

/// <summary>
/// Where a new window opens, kept apart from the windows so it can be tested: a step down and right of the window it
/// was opened from, or moved onto the monitor it was opened on, and always inside that monitor's working area.
/// </summary>
public static class NewWindowBounds
{
    /// <summary>How far (pixels) a window opened from another one sits down and right of it, as Explorer's cascade does.</summary>
    public const int CascadeStep = 24;

    /// <summary>
    /// A window opened from one at <paramref name="source"/>: <paramref name="step"/> down and right of it, inside
    /// <paramref name="workingArea"/>. Where the step would run off the edge it starts again from the top or left, as
    /// Explorer's cascade does, rather than being pushed back onto the window it came from. A window as large as the
    /// area, which can't move, is made a step smaller, so the new one never hides the old one exactly.
    /// </summary>
    public static Rectangle Cascade(Rectangle source, Rectangle workingArea, int step)
    {
        var from = Clamp(source, workingArea);
        int x = from.X + step, y = from.Y + step;
        if (x + from.Width > workingArea.Right) x = workingArea.Left;
        if (y + from.Height > workingArea.Bottom) y = workingArea.Top;
        var b = Clamp(new Rectangle(x, y, from.Width, from.Height), workingArea);
        if (b != from) return b;
        return Clamp(new Rectangle(from.X + step, from.Y + step, Math.Max(from.Width - step, 1), Math.Max(from.Height - step, 1)), workingArea);
    }

    /// <summary>
    /// <paramref name="bounds"/>, saved on the monitor whose working area is <paramref name="from"/>, moved onto the one
    /// whose working area is <paramref name="to"/>: as far from its corner, the same size (smaller when it doesn't fit).
    /// </summary>
    public static Rectangle MoveOnto(Rectangle bounds, Rectangle from, Rectangle to)
        => Clamp(new Rectangle(bounds.X - from.X + to.X, bounds.Y - from.Y + to.Y, bounds.Width, bounds.Height), to);

    /// <summary><paramref name="bounds"/> pushed inside <paramref name="area"/>, keeping its size where it fits.</summary>
    public static Rectangle Clamp(Rectangle bounds, Rectangle area)
    {
        int width = Math.Min(bounds.Width, area.Width), height = Math.Min(bounds.Height, area.Height);
        int x = Math.Clamp(bounds.X, area.Left, area.Right - width);
        int y = Math.Clamp(bounds.Y, area.Top, area.Bottom - height);
        return new Rectangle(x, y, width, height);
    }
}

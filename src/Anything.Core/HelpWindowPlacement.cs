namespace Anything.Core;

/// <summary>
/// Where the Help window (F1) opens: under the search box, lined up with its right edge, inside the window
/// that asked, so the box stays free to type in while reading. Never off the screen; never over the search box while
/// the screen has any other room. Pure arithmetic on screen rectangles (device pixels), so it is tested.
/// </summary>
public static class HelpWindowPlacement
{
    public readonly record struct Box(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public bool Intersects(Box o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
        public bool Inside(Box o) => X >= o.X && Y >= o.Y && Right <= o.Right && Bottom <= o.Bottom;
    }

    /// <param name="owner">The window that asked, on the screen.</param>
    /// <param name="searchBox">Its search box, on the screen.</param>
    /// <param name="area">The working area of the screen the owner is on.</param>
    /// <param name="wanted">The help's wanted outer size.</param>
    /// <param name="minimum">The smallest it should be (its minimum size); the screen may still force it smaller.</param>
    /// <param name="gap">The space left between the help and the box or the owner's edges.</param>
    public static Box Place(Box owner, Box searchBox, Box area, (int Width, int Height) wanted, (int Width, int Height) minimum, int gap)
    {
        int minW = Math.Min(minimum.Width, area.Width), minH = Math.Min(minimum.Height, area.Height);
        int wantW = Math.Clamp(wanted.Width, minW, Math.Max(minW, area.Width));
        int wantH = Math.Clamp(wanted.Height, minH, Math.Max(minH, area.Height));

        // 1. Under the search box, its right edge lined up with the box's, inside the owner; it may reach below a short
        //    owner rather than go anywhere else, as long as it stays on the screen.
        int top = searchBox.Bottom + gap;
        int right = Math.Min(searchBox.Right, area.Right);
        foreach (int bottomLimit in new[] { Math.Min(owner.Bottom - gap, area.Bottom), area.Bottom })
        {
            int room = bottomLimit - top;
            if (room < minH) continue;
            int leftLimit = Math.Max(owner.X + gap, area.X);
            int w = Math.Clamp(Math.Min(wantW, right - leftLimit), minW, wantW);
            int x = Math.Max(area.X, right - w);
            return Clamp(new Box(x, top, w, Math.Min(wantH, room)), area);
        }

        // 2. Beside the owner, right then left, on the same screen.
        int besideTop = Math.Clamp(owner.Y, area.Y, Math.Max(area.Y, area.Bottom - wantH));
        int roomRight = area.Right - (owner.Right + gap);
        if (roomRight >= minW) return Clamp(new Box(owner.Right + gap, besideTop, Math.Min(wantW, roomRight), wantH), area);
        int roomLeft = owner.X - gap - area.X;
        if (roomLeft >= minW)
        {
            int w = Math.Min(wantW, roomLeft);
            return Clamp(new Box(owner.X - gap - w, besideTop, w, wantH), area);
        }

        // 3. Last resort, a small screen: as much as there is below the box, even under the minimum, or above it; over the
        //    box only when neither has any room.
        int below = area.Bottom - top, above = searchBox.Y - gap - area.Y;
        int width = wantW, xx = Math.Max(area.X, right - width);
        if (below >= above && below > 0) return Clamp(new Box(xx, top, width, Math.Min(wantH, below)), area);
        if (above > 0)
        {
            int h = Math.Min(wantH, above);
            return Clamp(new Box(xx, searchBox.Y - gap - h, width, h), area);
        }
        return Clamp(new Box(xx, area.Y, width, wantH), area);
    }

    /// <summary>Inside the area, at least 1 x 1.</summary>
    public static Box Clamp(Box b, Box area)
    {
        int w = Math.Max(1, Math.Min(b.Width, area.Width)), h = Math.Max(1, Math.Min(b.Height, area.Height));
        int x = Math.Clamp(b.X, area.X, Math.Max(area.X, area.Right - w));
        int y = Math.Clamp(b.Y, area.Y, Math.Max(area.Y, area.Bottom - h));
        return new Box(x, y, w, h);
    }
}

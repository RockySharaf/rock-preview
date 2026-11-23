namespace Anything.Core;

/// <summary>
/// The pointer at the scope chip for people who haven't used the chip yet: what it says, whether it shows, and where it
/// goes. Pure rules on the chip's state and screen rectangles (device pixels), so they are tested.
/// </summary>
public static class ScopePointerRules
{
    /// <summary>The starts of the program on which the pointer shows, once each, unless Got it or the chip ends it first.</summary>
    public const int Starts = 2;

    public const string Title = "This is where Rock Preview searches";

    /// <summary>The second line, for what the chip shows now and what clicking it does.</summary>
    public static string Line(SearchScope.Button scope) =>
        !scope.Everything ? $"Now only in {scope.Where ?? scope.Text}. Click it to search everything."
        : scope.Click == SearchScope.Click.ToFolder ? $"Now everywhere. Click it to search only in {scope.Where}."
        : "Everything on this PC. In a folder it shows the folder's name and searches only there.";

    /// <summary>
    /// The pointer shows: on fewer than <see cref="Starts"/> starts so far (<paramref name="shownOnStarts"/>, as the
    /// settings keep it; Got it sets it to the limit), not in the demo, not already on this start, only while the chip
    /// can be seen, and never to someone who has clicked the chip (<paramref name="chipUses"/>): they know it already.
    /// </summary>
    public static bool Wanted(int shownOnStarts, bool demo, bool shownThisStart, bool chipVisible, int chipUses) =>
        !demo && !shownThisStart && chipVisible && shownOnStarts < Starts && chipUses == 0;

    /// <summary>What the pointer does when its moment comes: shows, waits the same delay again, or gives up for this start.</summary>
    public enum Moment { Show, Wait, GiveUp }

    /// <summary>The moments it tries on one start before it gives up (a menu, a dialog or a drag in the way each time).</summary>
    public const int Tries = 10;

    /// <summary>
    /// It never shows under a menu, a drop-down, a dialog or during a drag: then it waits, and after <see cref="Tries"/>
    /// moments in all gives up. <paramref name="triesSoFar"/> counts the moments that already waited.
    /// </summary>
    public static Moment WhenDue(bool menuOpen, bool dialogOpen, bool captured, int triesSoFar) =>
        !menuOpen && !dialogOpen && !captured ? Moment.Show
        : triesSoFar + 1 >= Tries ? Moment.GiveUp
        : Moment.Wait;

    /// <summary>Where the keyboard is when Esc is pressed while the pointer shows.</summary>
    public enum EscTarget { SearchBox, OtherTextBox, NativeEdit, Other }

    /// <summary>
    /// Esc closes the pointer and does nothing else (true), except in an edit box that is not the search box (a rename
    /// box, the address bar, the folder view's own rename edit): that Esc belongs to the box, which it cancels. In the
    /// results, the folder view or the tree it would clear the search or hide the window, which the user didn't mean.
    /// </summary>
    public static bool EscOnlyClosesPointer(EscTarget target) => target is not (EscTarget.OtherTextBox or EscTarget.NativeEdit);

    public readonly record struct Box(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public bool Intersects(Box o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
        public bool Inside(Box o) => X >= o.X && Y >= o.Y && Right <= o.Right && Bottom <= o.Bottom;
    }

    /// <summary>Where the callout goes: its bounds, whether it is under the search box (arrow up) or over it (arrow down), and the arrow's tip from its left edge.</summary>
    public readonly record struct Placement(Box Bounds, bool Below, int ArrowX);

    /// <summary>
    /// Under the search box, its arrow at the middle of the chip; over the box when there is no room under it; never
    /// over the box itself and never off the working area. Null when it can't be placed so: then it doesn't show.
    /// </summary>
    /// <param name="chip">The scope chip, on the screen.</param>
    /// <param name="field">The search box around it, on the screen.</param>
    /// <param name="area">The working area of the screen the window is on.</param>
    /// <param name="width">The callout's width, arrow included.</param>
    /// <param name="height">The callout's height, arrow included.</param>
    /// <param name="gap">Room left between the callout and the search box.</param>
    /// <param name="arrowInset">The least distance from the callout's side to its arrow's tip (its rounded corner and half the arrow).</param>
    public static Placement? Place(Box chip, Box field, Box area, int width, int height, int gap, int arrowInset)
    {
        if (width <= 0 || height <= 0 || width > area.Width || 2 * arrowInset > width) return null;
        int tip = chip.X + chip.Width / 2;
        if (tip < area.X || tip > area.Right) return null; // the chip is off the screen
        bool below = field.Bottom + gap + height <= area.Bottom;
        int top = below ? field.Bottom + gap : field.Y - gap - height;
        if (!below && top < area.Y) return null;
        int left = Math.Clamp(tip - arrowInset, area.X, area.Right - width);
        int arrow = Math.Clamp(tip - left, arrowInset, width - arrowInset);
        return new Placement(new Box(left, top, width, height), below, arrow);
    }
}

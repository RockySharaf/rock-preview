namespace Anything.Core;

/// <summary>
/// The size of a window that shows lines of text without wrapping them (Help › Search syntax, F1). Kept apart from the
/// window so it can be tested with numbers: every input is in pixels at the window's own scaling.
/// </summary>
public static class TextWindowLayout
{
    /// <summary>
    /// The client size: wide enough for the longest line (<paramref name="textWidth"/>, measured in the box's font) plus
    /// what the box itself takes beside the text (<paramref name="boxChrome"/>: its margins and vertical scroll bar), and
    /// <paramref name="wantedHeight"/> tall, but never so large that the window (client plus its frame) is larger than the
    /// working area of its screen; then the text scrolls instead.
    /// </summary>
    public static (int Width, int Height) ClientSize(int textWidth, int boxChrome, int wantedHeight,
                                                     int frameWidth, int frameHeight, int workingWidth, int workingHeight)
    {
        int width = Math.Min(textWidth + boxChrome, workingWidth - frameWidth);
        int height = Math.Min(wantedHeight, workingHeight - frameHeight);
        return (Math.Max(width, 1), Math.Max(height, 1));
    }

    /// <summary>
    /// Whether the box needs a horizontal scroll bar: only when the client width (from <see cref="ClientSize"/>) is less
    /// than the longest line plus what the box takes beside the text, which happens on a screen narrower than the text.
    /// When the whole line fits, a horizontal scroll bar would be a dead grey strip.
    /// </summary>
    public static bool NeedsHorizontalScroll(int textWidth, int boxChrome, int clientWidth) => clientWidth < textWidth + boxChrome;
}

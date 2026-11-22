using System.Runtime.CompilerServices;

namespace Anything;

/// <summary>
/// A ToolStrip's item tooltips come from a ToolTip of its own that WinForms keeps private: it shows when the pointer
/// rests on an item and closes when the pointer leaves the item, and nothing else closes or updates it. These close it
/// from outside the way the pointer does, by letting the strip handle a mouse leave.
/// </summary>
internal static class StripToolTips
{
    const int WM_MOUSEMOVE = 0x200, WM_MOUSELEAVE = 0x2A3;

    // Whether the pointer has hovered on the item (the strip shows its tooltip then) since it last came onto it.
    static readonly ConditionalWeakTable<ToolStripItem, StrongBox<bool>> s_hovered = new();

    /// <summary>
    /// Sets an item's tooltip text. A tooltip that is showing keeps the text it was shown with (Back, just clicked, would
    /// still name the folder it went to), so when the pointer is on the item its tooltip is closed. With
    /// <paramref name="hoverAgain"/> the pointer then hovers again where it rests: the tooltip comes back with the new
    /// text after the usual delay. Without, it comes back when the pointer moves, and only a tooltip that has shown is
    /// closed: closing also stops a hover under way, so a text that changes faster than the hover time (the status bar
    /// while indexing) would never let its tooltip show, and a hover under way reads the new text by itself.
    /// </summary>
    public static void Set(ToolStripItem item, string text, bool hoverAgain = true)
    {
        var hovered = s_hovered.GetValue(item, Track);
        if (item.ToolTipText == text) return;
        item.ToolTipText = text;
        if (!hoverAgain && !hovered.Value) return;
        // Where the item shows: its own bar, or the overflow menu it has moved to. Done once the message being handled
        // (often the click on this very item) is over, so the strip isn't told the pointer left in the middle of it.
        if (item.GetCurrentParent() is not { IsHandleCreated: true } strip) return;
        strip.BeginInvoke(() =>
        {
            if (strip.IsDisposed || !strip.ShowItemToolTips || item.GetCurrentParent() != strip) return;
            var p = strip.PointToClient(Control.MousePosition);
            if (!item.Bounds.Contains(p)) return;
            if (hoverAgain) Rehover(strip, p);
            else Close(strip);
        });
    }

    static StrongBox<bool> Track(ToolStripItem item)
    {
        var hovered = new StrongBox<bool>();
        item.MouseHover += (_, _) => hovered.Value = true;
        item.MouseLeave += (_, _) => hovered.Value = false;
        return hovered;
    }

    /// <summary>
    /// Closes the strip's tooltip and lets the pointer, which rests at <paramref name="client"/>, hover again (without a
    /// move the strip would wait for one). With a mouse button down only the tooltip is closed.
    /// </summary>
    internal static void Rehover(ToolStrip strip, Point client)
    {
        Close(strip);
        if (Control.MouseButtons == MouseButtons.None)
            Interop.Native.SendMessage(strip.Handle, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((client.Y << 16) | (client.X & 0xFFFF)));
    }

    /// <summary>
    /// Closes the strip's item tooltip and cancels one waiting to show (the strip stops its hover timer when the pointer
    /// leaves). The item under the pointer loses its hover look until the pointer moves, which also starts a new hover.
    /// The strip's ShowItemToolTips must still be on: with it off, WinForms leaves a showing tooltip alone.
    /// </summary>
    public static void Close(ToolStrip strip)
    {
        if (strip.IsHandleCreated) Interop.Native.SendMessage(strip.Handle, WM_MOUSELEAVE, IntPtr.Zero, IntPtr.Zero);
    }
}

using Anything.Core.Search;

namespace Anything;

// What the Search Syntax help (F1) needs from the window that asked for it: where its search box is, and a way to try
// a name or an example in it.
internal sealed partial class MainForm
{
    /// <summary>The search box on the screen, or null while it isn't showing.</summary>
    internal Rectangle? SearchBoxScreenBounds() =>
        _searchField is { IsHandleCreated: true, Visible: true } f ? f.RectangleToScreen(f.ClientRectangle) : null;

    /// <summary>
    /// A double-click in the help: an example takes the place of the search, a name goes in at the caret. Either way the
    /// search box gets the focus and searches as typing does, so the result shows at once while the help stays open.
    /// </summary>
    internal void TryInSearchBox(string text, HelpInsert how)
    {
        if (IsDisposed || !Visible || how == HelpInsert.None) return;
        var (box, caret) = SearchHelp.Insert(_search.Text, _search.SelectionStart, _search.SelectionLength, text, how);
        Activate();
        FocusSearch(false);
        _search.Text = box;
        _search.Select(caret, 0);
    }
}

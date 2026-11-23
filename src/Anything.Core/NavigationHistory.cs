namespace Anything.Core;

/// <summary>
/// Back and Forward for one folder view, as File Explorer keeps them: each place in the history remembers its name (for
/// the buttons' tooltips, "Back to Downloads") and the item that was selected when it was left, so going back selects
/// that item again. Kept apart from the window so it can be tested; in the app <typeparamref name="T"/> is the shell's
/// ID list of the place and of the item.
/// </summary>
public sealed class NavigationHistory<T> where T : class
{
    /// <summary>
    /// A place in the history: where, what it's called, the item selected there when it was left (or null), and the
    /// search that showed over it then (or null), which shows again when Back or Forward comes to it.
    /// </summary>
    public sealed record Entry(T Place, string? Name, T? Item, SearchPage? Page = null)
    {
        /// <summary>What Back and Forward's tooltips call it: the search when it has one, else the place's name.</summary>
        public string? Label => Page?.Label ?? Name;
    }

    /// <summary>The oldest places are forgotten past this many.</summary>
    public const int Limit = 200;

    readonly List<Entry> _back = [], _forward = [];

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public int BackCount => _back.Count;
    public int ForwardCount => _forward.Count;

    /// <summary>Where Back goes, or null.</summary>
    public Entry? BackEntry => _back.Count > 0 ? _back[^1] : null;
    /// <summary>Where Forward goes, or null.</summary>
    public Entry? ForwardEntry => _forward.Count > 0 ? _forward[^1] : null;

    /// <summary>A new place was opened (not by Back or Forward): the one left goes on the Back list and Forward is dropped.</summary>
    public void Visit(Entry left)
    {
        _back.Add(left);
        if (_back.Count > Limit) _back.RemoveAt(0);
        _forward.Clear();
    }

    /// <summary>
    /// Goes back from <paramref name="here"/>: <paramref name="open"/> is asked to show the place, and when it can't,
    /// the history stays as it was. Returns the place opened, or null.
    /// </summary>
    public Entry? GoBack(Entry here, Func<Entry, bool> open) => Step(_back, _forward, here, open);

    /// <summary>Goes forward from <paramref name="here"/>, as <see cref="GoBack"/> does the other way.</summary>
    public Entry? GoForward(Entry here, Func<Entry, bool> open) => Step(_forward, _back, here, open);

    static Entry? Step(List<Entry> from, List<Entry> to, Entry here, Func<Entry, bool> open)
    {
        if (from.Count == 0) return null;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(here);
        if (open(target)) return target;
        to.RemoveAt(to.Count - 1);
        from.Add(target);
        return null;
    }
}

namespace Anything.Core;

/// <summary>
/// Remembers the folders where adding up a selection's sizes for the status bar ran out of time, so each later
/// selection change there doesn't wait on the UI thread again. A folder counts as slow when the time ran out after many
/// items had been added up, or when it ran out there at two different items (a phone, FTP or a cloud folder, where every
/// item is slow and the time runs out before many). Running out after a few items at the same one each time (one slow
/// item, read again by every selection that holds it) hides those selections' sizes only. Places are told apart by
/// reference: each navigation gives its folder a new one, so opening the folder again starts afresh.
/// </summary>
public sealed class SelectionSizeMemory<TPlace> where TPlace : class
{
    /// <summary>How many items must have been added up before running out of time counts as the folder being slow at once.</summary>
    public const int SlowAfterItems = 50;

    TPlace? _slowAt;        // the place found slow...
    int _slowFrom;          // ...for selections of at least this many items
    TPlace? _ranOutAt;      // the place where the time last ran out...
    string? _ranOutItem;    // ...the item being read when it first did there (null: none known)...
    int _ranOutTimes;       // ...how often it has there at different items...
    int _fewestReached;     // ...and the fewest items added up when it did

    /// <summary>Whether a selection of <paramref name="count"/> items at <paramref name="place"/> is not to be added up.</summary>
    public bool IsSlow(TPlace? place, int count) => place != null && ReferenceEquals(place, _slowAt) && count >= _slowFrom;

    /// <summary>
    /// The time ran out at <paramref name="place"/> with <paramref name="reached"/> of the <paramref name="selected"/>
    /// items added up, while <paramref name="item"/> was being read (null when not known, or before the first item: then
    /// it always counts as another one). Running out again at the same item counts once: one item slow every time it is
    /// read makes every selection holding it run out, yet the folder's other items are not slow.
    /// </summary>
    public void RanOut(TPlace? place, int selected, int reached, string? item = null)
    {
        if (place == null) return;
        if (!ReferenceEquals(place, _ranOutAt)) (_ranOutAt, _ranOutItem, _ranOutTimes, _fewestReached) = (place, null, 0, int.MaxValue);
        _fewestReached = Math.Min(_fewestReached, Math.Max(0, reached));
        if (_ranOutTimes == 0) { _ranOutItem = item; _ranOutTimes = 1; }
        else if (item == null || !string.Equals(item, _ranOutItem, StringComparison.Ordinal)) _ranOutTimes++;
        if (reached >= SlowAfterItems) Remember(place, selected);
        else if (_ranOutTimes >= 2) Remember(place, Math.Max(1, _fewestReached)); // every item is slow here
    }

    void Remember(TPlace place, int from)
    {
        from = Math.Max(1, from);
        _slowFrom = ReferenceEquals(place, _slowAt) ? Math.Min(_slowFrom, from) : from;
        _slowAt = place;
    }
}

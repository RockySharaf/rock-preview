namespace Anything.Core.Search;

/// <summary>
/// The searches the search box remembers, newest first, for the list under its arrow (Alt+Down). A search is added
/// when it is committed (Enter, or a result opened), not on every keystroke. The same words in another case are one
/// search: adding it again moves it to the top, as typed this time. At most <see cref="Capacity"/> are kept.
/// </summary>
public sealed class SearchHistory
{
    /// <summary>How many searches are kept unless another number is given.</summary>
    public const int DefaultCapacity = 20;

    readonly List<string> _items = [];

    public SearchHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>The most searches kept: adding one more drops the oldest.</summary>
    public int Capacity { get; }

    /// <summary>The searches, newest first.</summary>
    public IReadOnlyList<string> Items => _items;

    public int Count => _items.Count;

    /// <summary>
    /// Puts <paramref name="search"/> (trimmed) at the top, taking out the same search in any case and the oldest past
    /// <see cref="Capacity"/>. Blank text, or text on more than one line, is not a search to remember. True if the list changed.
    /// </summary>
    public bool Add(string? search)
    {
        string text = search?.Trim() ?? "";
        if (text.Length == 0 || text.AsSpan().IndexOfAny('\r', '\n') >= 0) return false;
        if (_items.Count > 0 && _items[0] == text) return false;
        int at = IndexOf(text);
        if (at >= 0) _items.RemoveAt(at);
        _items.Insert(0, text);
        if (_items.Count > Capacity) _items.RemoveRange(Capacity, _items.Count - Capacity);
        return true;
    }

    /// <summary>Takes out <paramref name="search"/> (in any case). True if it was there.</summary>
    public bool Remove(string? search)
    {
        int at = IndexOf(search?.Trim() ?? "");
        if (at < 0) return false;
        _items.RemoveAt(at);
        return true;
    }

    /// <summary>Forgets every search. True if there were any.</summary>
    public bool Clear()
    {
        if (_items.Count == 0) return false;
        _items.Clear();
        return true;
    }

    /// <summary>
    /// Replaces the list with saved searches, newest first (as <see cref="Items"/> gives them): blank ones and repeats
    /// are dropped, and only the newest <see cref="Capacity"/> kept.
    /// </summary>
    public void Load(IEnumerable<string> newestFirst)
    {
        _items.Clear();
        foreach (var s in newestFirst.Reverse()) Add(s);
    }

    /// <summary>The settings key holding how many searches are saved; each is under <see cref="SettingsKey"/>.</summary>
    public const string CountKey = "search_history_count";

    /// <summary>The most saved searches read back, whatever the count in the settings says.</summary>
    public const int MaxSaved = 1000;

    /// <summary>The settings key of saved search <paramref name="index"/> (0 is the newest).</summary>
    public static string SettingsKey(int index) => $"search_history{index}";

    /// <summary>
    /// The searches as settings: the count, then one key per search, newest first. Each search has a key of its own,
    /// so one with '|' or '=' in it ("jpg | png") comes back whole. With <paramref name="remember"/> off, a count of 0
    /// and nothing else: the searches aren't saved.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> ToSettings(bool remember)
    {
        int count = remember ? _items.Count : 0;
        yield return new(CountKey, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (int i = 0; i < count; i++) yield return new(SettingsKey(i), _items[i]);
    }

    /// <summary>
    /// Replaces the list with the searches saved by <see cref="ToSettings"/>; <paramref name="read"/> gives a key's value,
    /// or null when it isn't there. A missing or broken count reads as none, and at most <see cref="MaxSaved"/> are read.
    /// </summary>
    public void LoadSettings(Func<string, string?> read)
    {
        int count = int.TryParse(read(CountKey)?.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n)
            ? Math.Clamp(n, 0, MaxSaved) : 0;
        Load(Enumerable.Range(0, count).Select(i => read(SettingsKey(i)) ?? ""));
    }

    int IndexOf(string text) => text.Length == 0 ? -1 : _items.FindIndex(s => string.Equals(s, text, StringComparison.OrdinalIgnoreCase));
}

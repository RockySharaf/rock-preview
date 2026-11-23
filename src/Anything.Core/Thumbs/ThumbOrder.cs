namespace Anything.Core.Thumbs;

// How to use the request order (the grid on the UI thread, the service's workers on theirs):
//
//   1. Whenever the scroll position, the size, the list or the thumbnail size changes, the grid asks
//      ThumbOrder.Viewport(count, columns, firstRow, rows, direction) for the items to ask for, in order: the visible
//      cells in reading order, then two screens ahead in the scroll direction (nearest row first), then one screen behind.
//   2. For each item it still needs (not already in its memory cache at Final quality) it resolves the path and the
//      stamp (from the index, ThumbStamp.Unknown when it has none) and builds a ThumbRequest with the priority the plan
//      gave. It hands the list, in the same order, to ThumbQueue.SubmitViewport with the list number (a number the grid
//      changes whenever the list itself changes: a new search, another folder, a refresh).
//   3. When the queue is idle (ThumbQueue.IsIdle) and there was no scrolling or key for 1.5 s, the grid asks its
//      IdlePlanner for the next chunk (IdlePlanner.Next, starting at ThumbOrder.IdleStart of the plan) and submits those
//      with ThumbQueue.SubmitIdle. IdlePlanner.Reset when the list changes. With "Instant thumbnails" off (the default)
//      there is no idle step: the queue is made with new ThumbQueue(allowIdle: false) (or AllowIdle set false when the
//      setting is turned off) and SubmitIdle then takes nothing; the grid need not ask the planner at all.
//   4. The service's workers call ThumbQueue.TryTake, make the picture, then ThumbQueue.Complete, which says which
//      display sizes are wanted from it now (more may have been asked for while it ran).

/// <summary>Which way the user last scrolled; the look-ahead goes that way.</summary>
public enum ScrollDirection : sbyte
{
    /// <summary>Toward the start of the list.</summary>
    Up = -1,
    /// <summary>Not scrolled yet: treated as down (people read on).</summary>
    None = 0,
    /// <summary>Toward the end of the list.</summary>
    Down = 1,
}

/// <summary>Who wants a picture, best first: the queue hands out every Visible job before any Ahead one, and so on.</summary>
public enum ThumbPriority : byte
{
    /// <summary>On screen now.</summary>
    Visible = 0,
    /// <summary>Within two screens in the scroll direction.</summary>
    Ahead = 1,
    /// <summary>Within one screen against the scroll direction.</summary>
    Behind = 2,
    /// <summary>Warming the rest of the current list while the user does nothing.</summary>
    Idle = 3,
}

/// <summary>One item of a viewport plan: its index in the list and why it is wanted.</summary>
public readonly record struct PlannedItem(int Index, ThumbPriority Priority);

/// <summary>The order in which a grid asks for the pictures around its viewport. Pure and thread-safe.</summary>
public static class ThumbOrder
{
    /// <summary>Screens of look-ahead in the scroll direction.</summary>
    public const int AheadScreens = 2;
    /// <summary>Screens kept warm behind.</summary>
    public const int BehindScreens = 1;

    /// <summary>
    /// The items of a grid of <paramref name="count"/> items in <paramref name="columns"/> columns whose rows
    /// <paramref name="firstRow"/> to <paramref name="firstRow"/> + <paramref name="rows"/> - 1 are on screen (partly
    /// visible rows included): the visible ones in reading order, then <see cref="AheadScreens"/> screens of rows in the
    /// scroll direction, nearest row first and each row left to right, then <see cref="BehindScreens"/> screen of rows
    /// the other way, nearest first. Indices outside the list are left out; nothing appears twice.
    /// </summary>
    public static List<PlannedItem> Viewport(int count, int columns, int firstRow, int rows, ScrollDirection direction)
    {
        var plan = new List<PlannedItem>();
        if (count <= 0) return plan;
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        int totalRows = (count + columns - 1) / columns;
        firstRow = Math.Clamp(firstRow, 0, Math.Max(0, totalRows - 1));
        int lastRow = Math.Min(totalRows - 1, firstRow + rows - 1);
        int step = direction == ScrollDirection.Up ? -1 : 1;

        for (int r = firstRow; r <= lastRow; r++) AddRow(plan, r, columns, count, ThumbPriority.Visible);
        int aheadFrom = step > 0 ? lastRow + 1 : firstRow - 1;
        for (int i = 0, r = aheadFrom; i < rows * AheadScreens && r >= 0 && r < totalRows; i++, r += step)
            AddRow(plan, r, columns, count, ThumbPriority.Ahead);
        int behindFrom = step > 0 ? firstRow - 1 : lastRow + 1;
        for (int i = 0, r = behindFrom; i < rows * BehindScreens && r >= 0 && r < totalRows; i++, r -= step)
            AddRow(plan, r, columns, count, ThumbPriority.Behind);
        return plan;
    }

    /// <summary>Where idle warming starts: one past the last item the plan covers (0 for an empty plan), wrapping to the start.</summary>
    public static int IdleStart(IReadOnlyList<PlannedItem> plan, int count)
    {
        if (count <= 0) return 0;
        int max = -1;
        foreach (var p in plan) if (p.Index > max) max = p.Index;
        return (max + 1) % count;
    }

    static void AddRow(List<PlannedItem> plan, int row, int columns, int count, ThumbPriority priority)
    {
        int start = row * columns;
        int end = Math.Min(count, start + columns);
        for (int i = start; i < end; i++) plan.Add(new PlannedItem(i, priority));
    }
}

/// <summary>
/// Picks the items of one list to warm while the user is idle: <see cref="ChunkSize"/> at a time, beyond the
/// look-ahead and wrapping to the start, never the same item twice, at most <see cref="CapPerList"/> for the list.
/// Belongs to one grid and is used on its thread only (not thread-safe). <see cref="Reset"/> when the list changes.
/// </summary>
public sealed class IdlePlanner
{
    /// <summary>Items per idle chunk.</summary>
    public const int DefaultChunk = 200;
    /// <summary>Items warmed per list at most.</summary>
    public const int DefaultCap = 3000;

    readonly HashSet<int> _given = [];

    /// <summary>A planner with the design's chunk (200) and cap (3,000) unless others are given (tests).</summary>
    public IdlePlanner(int chunkSize = DefaultChunk, int capPerList = DefaultCap)
    {
        ChunkSize = Math.Max(1, chunkSize);
        CapPerList = Math.Max(0, capPerList);
    }

    /// <summary>Items handed out per call at most.</summary>
    public int ChunkSize { get; }
    /// <summary>Items handed out for one list at most.</summary>
    public int CapPerList { get; }
    /// <summary>Items handed out since the last <see cref="Reset"/>.</summary>
    public int Given => _given.Count;
    /// <summary>Whether the cap is reached: no more idle work for this list.</summary>
    public bool Exhausted => _given.Count >= CapPerList;

    /// <summary>
    /// Whether the last <see cref="Next"/> handed out nothing: everything it looked at is handed out or not wanted, though
    /// the cap may be far off (a short list), or the cap is reached. The grid's idle timer stops on this until something
    /// can make new candidates (a scroll, a new list, idle work given back), since asking again would walk the same items
    /// to the same answer twice a second. On a huge list the walk is cut short, so items past it may still be wanted; they
    /// get their turn when a viewport moves the walk's start. <see cref="Reset"/> and <see cref="Return"/> clear it, and so
    /// does a <see cref="Next"/> that hands something out.
    /// </summary>
    public bool Done { get; private set; }

    /// <summary>Forgets what was handed out (a new list).</summary>
    public void Reset()
    {
        _given.Clear();
        Done = false;
    }

    /// <summary>
    /// Takes back items handed out that were never made (a new viewport dropped them before a worker started on them):
    /// they may be handed out again, and stop counting toward the cap. Items made stay handed out, so a list that keeps
    /// changing doesn't get the same chunk again and again.
    /// </summary>
    public void Return(IEnumerable<int> notMade)
    {
        foreach (int i in notMade) _given.Remove(i);
        // What was given back may be handed out again, so the list is no longer known to be used up.
        Done = false;
    }

    /// <summary>
    /// The next chunk of indices of a list of <paramref name="count"/> items, walking forward from
    /// <paramref name="start"/> and wrapping, skipping items handed out before and items <paramref name="wanted"/> says
    /// no to (in the viewport plan, or already pictured). Skipped items don't count toward the cap. The walk looks at
    /// most at one lap of the list or 2 × the cap items, whichever is fewer, so a call stays cheap on a huge list.
    /// </summary>
    public List<int> Next(int count, int start, Func<int, bool> wanted)
    {
        var chunk = new List<int>();
        if (count <= 0 || Exhausted) { Done = true; return chunk; }
        int room = Math.Min(ChunkSize, CapPerList - _given.Count);
        long probes = Math.Min((long)count, Math.Max(2L * CapPerList, ChunkSize));
        int i = ((start % count) + count) % count;
        for (long n = 0; n < probes && chunk.Count < room; n++, i = i + 1 == count ? 0 : i + 1)
        {
            if (_given.Contains(i) || !wanted(i)) continue;
            _given.Add(i);
            chunk.Add(i);
        }
        Done = chunk.Count == 0;
        return chunk;
    }
}

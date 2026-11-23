namespace Anything.Core.Thumbs;

/// <summary>
/// The one turn, for the whole process, that a suspect takes before the marked work that makes its picture. A suspect is
/// a file whose picture was being made when the program ended once (<see cref="ThumbStore.IsSuspect"/>); if the program
/// ends again while it is made, the store turns it into Crashed for good. Several files are made side by side, so a crash
/// caused by one of them would otherwise mark its neighbours too, and a good neighbour retried beside other suspects could
/// be blamed a second time. With one suspect at a time, a second end finds at most one suspect in flight, and only that
/// one is blamed. Thread-safe; only suspects ever ask for it, so other files never take its lock.
/// </summary>
public sealed class SuspectTurn
{
    /// <summary>
    /// A holder that never gives the turn back (a decode that hangs, on a lane no watchdog watches) keeps it this long at
    /// most: after that another suspect may take it, so one stuck file can't keep the others waiting for ever.
    /// </summary>
    public const long LimitMs = 30_000;

    private readonly object _lock = new();
    private readonly long _limitMs;
    private long _holder; // 0: free
    private long _takenAt;

    /// <summary>A turn held <paramref name="limitMs"/> at most (the product's is <see cref="LimitMs"/>).</summary>
    public SuspectTurn(long limitMs = LimitMs) => _limitMs = limitMs;

    /// <summary>Who holds the turn now (0: nobody).</summary>
    public long Holder { get { lock (_lock) return _holder; } }

    /// <summary>
    /// Takes the turn for <paramref name="holder"/> (any number but 0, the same for every take and give-back of one job)
    /// at <paramref name="now"/> (milliseconds): true when it is free, already this holder's (its time starts again), or
    /// held by another for <see cref="LimitMs"/> or more. False otherwise: the caller leaves the job queued, no thread waits.
    /// </summary>
    public bool TryTake(long holder, long now)
    {
        if (holder == 0) throw new ArgumentOutOfRangeException(nameof(holder), "0 means nobody");
        lock (_lock)
        {
            if (_holder != 0 && _holder != holder && now - _takenAt < _limitMs) return false;
            _holder = holder;
            _takenAt = now;
            return true;
        }
    }

    /// <summary>
    /// Gives the turn back, only when <paramref name="holder"/> still holds it (a holder whose time ran out and was
    /// overtaken gives back nothing). True when it was given back: whoever waits for it may go.
    /// </summary>
    public bool Release(long holder)
    {
        lock (_lock)
        {
            if (holder == 0 || _holder != holder) return false;
            _holder = 0;
            return true;
        }
    }
}

/// <summary>Small rules of the thumbnail service, kept apart from its threads so they can be tested.</summary>
public static class ThumbGuards
{
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> OfficeExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".doc", ".dot", ".docx", ".docm", ".dotx", ".dotm", ".xls", ".xlt", ".xlsb", ".xlsx", ".xlsm", ".xltx", ".xltm",
            ".ppt", ".pps", ".pot", ".pptx", ".pptm", ".ppsx", ".ppsm", ".potx", ".potm", ".sldx", ".sldm",
            ".odt", ".ott", ".ods", ".ots", ".odp", ".otp", ".odg",
        }.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>Whether Windows can return an empty Office template page for this file extension (including its dot).</summary>
    public static bool IsOfficeDocument(ReadOnlySpan<char> extension) => OfficeExtensions.Contains(extension);

    /// <summary>
    /// Whether a picture made for a file stamped <paramref name="madeFor"/> may be kept on disk, the file read again just
    /// before (<paramref name="readNow"/>: whether that worked; <paramref name="now"/>: what it said). Only when the file is
    /// still exactly the one the picture was made for: a file replaced while it was made (a copy that keeps the date and
    /// size of the old one, then restored) must not leave one version's picture under another's key. It is still shown.
    /// </summary>
    public static bool MayStore(in ThumbStamp madeFor, bool readNow, in ThumbStamp now)
        => readNow && !madeFor.IsUnknown && now == madeFor;

    /// <summary>
    /// Whether a store that couldn't use its folder is tried again later (every 30 s, on the next request): a drive not
    /// there yet or another copy using the folder may pass. A folder that belongs to another Windows account doesn't
    /// stop being that account's by waiting, so it is not tried again (nor is anything else).
    /// </summary>
    public static bool RetriesOpen(ThumbStoreProblem problem)
        => problem is ThumbStoreProblem.FolderUnusable or ThumbStoreProblem.InUse;
}

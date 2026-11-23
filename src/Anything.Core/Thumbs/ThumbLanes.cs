namespace Anything.Core.Thumbs;

/// <summary>What a decode thread does with a job that waits for the large-decode gate (see <see cref="ThumbLanes.ForGateWaiter"/>).</summary>
public enum GateChoice : byte
{
    /// <summary>Take it now: it doesn't wait for the gate, or the gate is free.</summary>
    Take,
    /// <summary>Leave it queued: the gate is held, and taking it would only open the file again to be told so.</summary>
    Wait,
    /// <summary>The gate has been held so long that its holder may be stuck: Windows' extractor is asked instead.</summary>
    ToShell,
}

/// <summary>
/// The thumbnail lanes' small rules, kept apart from the threads that follow them so they can be tested: the large-decode
/// gate's waiting jobs, the watchdog's check that a worker still runs the job it found overdue, and when a lane is dead.
/// </summary>
public static class ThumbLanes
{
    /// <summary>
    /// While the gate stays held, a job waits at least this long before it may go to Windows. A release wakes it at once.
    /// </summary>
    public const long GateRetryMs = 150;

    /// <summary>
    /// A gate held this long (counted from the first job told it is held) may have a stuck holder (a codec that never
    /// returns, a network read that hangs): jobs someone wants then go to Windows' extractor instead of waiting for ever.
    /// </summary>
    public const long GateStuckMs = 30_000;

    /// <summary>
    /// What a decode thread does with a job: one that doesn't wait for the gate, or finds it free, is taken. One that waits
    /// while the gate is held stays queued (no thread waits for the gate), unless the gate has been held for
    /// <see cref="GateStuckMs"/> or more since <paramref name="heldSince"/> (0: not known to be held), and the job is
    /// wanted (idle work only fills the store and may wait) and hasn't asked Windows' extractor yet (it asks once).
    /// </summary>
    public static GateChoice ForGateWaiter(bool needsGate, bool gateHeld, bool idle, bool shellTried, long heldSince, long now, long deferUntil = 0)
    {
        if (!needsGate || !gateHeld) return GateChoice.Take;
        if (now < deferUntil || idle || shellTried || heldSince == 0) return GateChoice.Wait;
        return now - heldSince >= GateStuckMs ? GateChoice.ToShell : GateChoice.Wait;
    }

    /// <summary>After a Windows miss, only a file with its own extractor still to try goes back to decode.
    /// A picture sent to Windows because its gate was stuck finishes with its stand-in or icon.</summary>
    public static bool DecodeAfterShellMiss(ThumbRoute route, bool decodeTried, bool gateToShell)
        => route != ThumbRoute.Shell && !decodeTried && !gateToShell;

    /// <summary>
    /// Whether a job that ends without a picture, for a reason that may pass, is remembered as failed for this run (so
    /// it isn't made again until then). Not a file only Windows' cache was asked for (<paramref name="cacheOnly"/>:
    /// folders, shortcuts, the PDF kinds) when nothing else tried (<paramref name="decodeTried"/>): a miss in Windows'
    /// cache is no fact about the file, and a later look asks the cache again (File Explorer may have filled it).
    /// </summary>
    public static bool RemembersFailure(bool cacheOnly, bool decodeTried) => !cacheOnly || decodeTried;

    /// <summary>
    /// Whether a worker the watchdog found overdue still runs the same job, started at the same moment: when the job it
    /// noted (<paramref name="notedWork"/>, <paramref name="notedStart"/>) has ended and another begun, giving up now would
    /// abandon the next, healthy job.
    /// </summary>
    public static bool StillSameRun(object? notedWork, long notedStart, object? currentWork, long currentStart)
        => notedWork != null && ReferenceEquals(notedWork, currentWork) && notedStart == currentStart;

    /// <summary>
    /// Whether a lane is dead after the watchdog gave up one of its threads: no replacement was started (the budget is
    /// spent) and no thread of that lane is left. Work sent to it then gets the lane's fallback instead.
    /// </summary>
    public static bool LaneDies(bool replaced, int threadsLeftInLane) => !replaced && threadsLeftInLane == 0;
}

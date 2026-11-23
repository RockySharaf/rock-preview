using System.Runtime.CompilerServices;
using Anything.Core.Content.Indexing;

// The types of this folder are public for the grid (RockPreview), which can't see Anything.Core's internals: letting it
// would make Anything.Core.Indexing.Native clash with its own Native. The benchmark (PreviewHarness) also uses the test
// hooks (WaitUntilWritten, Abandon), which stay internal.
[assembly: InternalsVisibleTo("PreviewHarness")]

namespace Anything.Core.Thumbs;

/// <summary>
/// The thumbnail service's fixed rules that don't depend on Windows: how many threads each lane has, when idle work
/// waits, which answers from Windows' thumbnail extractor mean "this file has no thumbnail" for good, and the limits
/// the decoders check before they trust a size read from a file. Pure and thread-safe.
/// </summary>
public static class ThumbRules
{
    /// <summary>STA threads for quick work: cache-only lookups in Windows' thumbnail cache, embedded previews, icons.</summary>
    public const int FastLaneThreads = 2;
    /// <summary>STA threads that ask Windows' thumbnail extractor (it can hang inside a third-party handler).</summary>
    public const int ShellLaneThreads = 3;
    /// <summary>A Windows call on a fast or shell-extract thread that runs longer than this is given up and its thread replaced.</summary>
    public static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(12);
    /// <summary>Threads given up as stuck, at most, in the life of the process; after that a lane shrinks instead.</summary>
    public const int MaxAbandonedThreads = 8;
    /// <summary>Decode threads that may take idle work (the rest wait for work the user is looking at or near).</summary>
    public const int IdleDecodeThreads = 2;
    /// <summary>Shell-extract threads that may take idle work.</summary>
    public const int IdleShellThreads = 1;

    /// <summary>The PSD format's largest picture side (PSB allows far more).</summary>
    public const int PsdMaxSide = 30_000;
    /// <summary>The PSB format's largest picture side.</summary>
    public const int PsbMaxSide = 300_000;
    /// <summary>The most channels a PSD or PSB may have.</summary>
    public const int PsdMaxChannels = 56;
    /// <summary>A PSD row-length table bigger than this is not read (the thumbnail resource is used instead).</summary>
    public const long PsdMaxRowTableBytes = 64L << 20;
    /// <summary>A TGA bigger than this many pixels is not decoded.</summary>
    public const long TgaMaxPixels = 64L * 1000 * 1000;

    /// <summary>Decode threads for a PC with <paramref name="processorCount"/> logical processors: 3/8 of them, 2 to 6.</summary>
    public static int DecodeThreadsFor(int processorCount) => Math.Clamp(processorCount * 3 / 8, 2, 6);

    /// <summary>
    /// Decode threads of a lane of <paramref name="decodeThreads"/> that may take idle work: at most
    /// <see cref="IdleDecodeThreads"/>, and always one fewer than the lane has, because a decode that started is never
    /// stopped: with every thread on idle work, the pictures the user scrolls to would wait for it to finish.
    /// </summary>
    public static int IdleDecodeThreadsFor(int decodeThreads) => Math.Clamp(decodeThreads - 1, 0, IdleDecodeThreads);

    /// <summary>Whether idle work (pictures nobody is looking at yet) waits: Battery Saver, a full-screen program, or a low disk.</summary>
    internal static bool IdleWorkPaused(in PowerState s) => s.BatterySaver || s.FullScreen || PowerRules.LowDisk(s);

    /// <summary>How often the service asks the power policy whether idle work waits (the answer is reused in between).</summary>
    public const long IdleCheckMs = 2000;

    /// <summary>A check of the power policy that hasn't answered after this long (a disk asleep) pauses idle work.</summary>
    public const long PowerCheckGiveUpMs = 4000;

    /// <summary>
    /// The view counts as moving for this long after it was last scrolled (or resized, or given a new list): jobs that
    /// draw text then run one at a time, since the grid's captions share the font engine with them.
    /// </summary>
    public const long TextViewMovedMs = 300;

    /// <summary>Jobs that draw text at once while the view is still.</summary>
    public const int TextJobsWhenStill = 3;

    /// <summary>
    /// Whether a check last made at <paramref name="lastCheckedMs"/> (a millisecond clock; <see cref="long.MinValue"/>
    /// for never) is due again at <paramref name="nowMs"/>: never made, or <paramref name="intervalMs"/> or more ago.
    /// "Never" is tested as such, not by subtracting from it (which would overflow and read as "just now").
    /// </summary>
    public static bool CheckDue(long nowMs, long lastCheckedMs, long intervalMs)
        => lastCheckedMs == long.MinValue || nowMs - lastCheckedMs >= intervalMs || nowMs < lastCheckedMs;

    /// <summary>
    /// The worst priority a thread of a lane may take: the first <paramref name="idleThreads"/> threads of the lane take
    /// idle work too (unless idle work is paused); the others only work the user is looking at or near.
    /// </summary>
    public static ThumbPriority WorstFor(int threadIndex, int idleThreads, bool idlePaused)
        => !idlePaused && threadIndex < idleThreads ? ThumbPriority.Idle : ThumbPriority.Behind;

    /// <summary>
    /// Whether an HRESULT from Windows' thumbnail extractor means the file has no thumbnail for good (no handler, the
    /// handler can't, extraction failed), so "no picture" may be stored. Anything else (a locked file, access denied, a
    /// device not ready, a pending or timed-out extraction, low memory) may go away and is only remembered for this run.
    /// </summary>
    public static bool IsCleanShellNone(int hr) => unchecked((uint)hr) switch
    {
        0x80004005 => true, // E_FAIL: the handler can't make one
        0x80004001 => true, // E_NOTIMPL
        0x80004002 => true, // E_NOINTERFACE: no thumbnail handler
        0x80040154 => true, // REGDB_E_CLASSNOTREG: the handler named for the kind isn't installed
        0x8004B200 => true, // WTS_E_FAILEDEXTRACTION
        0x8004B206 => true, // WTS_E_NOSTORAGEPROVIDERTHUMBNAILHANDLER
        0x80070057 => true, // E_INVALIDARG: what handlers say for a file they can't read as their kind
        0x88982F50 => true, // WINCODEC_ERR_COMPONENTNOTFOUND: no codec
        0x88982F07 => true, // WINCODEC_ERR_UNKNOWNIMAGEFORMAT
        _ => false,
    };

    /// <summary>
    /// Whether a PSD or PSB header describes a picture the format allows: sides 1 to 30,000 (PSB 300,000), 1 to 56
    /// channels, depth 1, 8, 16 or 32. A header outside these is damaged and its sizes are not trusted.
    /// </summary>
    public static bool PsdHeaderOk(bool isPsb, long width, long height, int channels, int depth)
    {
        long max = isPsb ? PsbMaxSide : PsdMaxSide;
        return width >= 1 && height >= 1 && width <= max && height <= max
            && channels >= 1 && channels <= PsdMaxChannels && depth is 1 or 8 or 16 or 32;
    }

    /// <summary>The size of a PSD's row-length table (2 bytes a row, 4 in a PSB), one row per channel and line.</summary>
    public static long PsdRowTableBytes(bool isPsb, int channels, long height) => (long)channels * height * (isPsb ? 4 : 2);

    /// <summary>
    /// The longest a PackBits-compressed row of <paramref name="rowBytes"/> bytes can honestly be: twice the row, with
    /// room to spare. Not the best coding's bound (one header byte per 128): Photoshop's coder breaks literal runs at
    /// short repeats, so a noisy photo's rows come out longer than the row itself (a 1-byte literal costs 2 bytes;
    /// measured on a 24-megapixel PSD: 6096 to 6324 bytes for 6000-byte rows). A longer length read from a file is
    /// damage, not a reason to allocate.
    /// </summary>
    public static long PackBitsMaxRow(long rowBytes) => 2 * rowBytes + 64;
}

/// <summary>
/// How many stuck threads may still be replaced in the life of the process (<see cref="ThumbRules.MaxAbandonedThreads"/>):
/// each replacement takes one; when none are left a lane shrinks instead. Thread-safe.
/// </summary>
public sealed class AbandonBudget(int total)
{
    int _left = total;

    /// <summary>The replacements left.</summary>
    public int Left => Volatile.Read(ref _left);

    /// <summary>Takes one replacement; false when the budget is spent.</summary>
    public bool TryTake()
    {
        while (true)
        {
            int left = Volatile.Read(ref _left);
            if (left <= 0) return false;
            if (Interlocked.CompareExchange(ref _left, left - 1, left) == left) return true;
        }
    }
}

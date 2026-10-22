namespace Anything.Core.Content.Indexing;

/// <summary>
/// The per-share speed limit for network reads: one bucket per share key, allowed one second of burst, then held to its
/// average. The scheduler asks <see cref="Ready"/> before handing a share's next file over and charges what a read cost
/// with <see cref="Charge"/>; readers wait inside a read with <see cref="Wait"/> (the <c>NetworkThrottle</c> hook), so a
/// large file is spread over time instead of read in one rush. Thread-safe.
/// </summary>
internal sealed class ShareThrottle(Func<long>? clockMs = null)
{
    /// <summary>The burst a share may run ahead of its average.</summary>
    public const long BurstMs = 1000;
    /// <summary>The longest one wait lasts (a very low limit spreads a read over several).</summary>
    const int MaxWaitMs = 60_000;

    readonly Func<long> _clock = clockMs ?? (static () => Environment.TickCount64);
    readonly Dictionary<string, long> _debtUntil = new(StringComparer.OrdinalIgnoreCase);
    long _bytesPerSecond = 8L << 20;

    /// <summary>The default for reads outside the indexer's scheduler (calibration, the demo's real files): 8 MB/s per share.</summary>
    public static ShareThrottle Default { get; } = new();

    /// <summary>Bytes per second per share (at least 64 KB/s).</summary>
    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set => Interlocked.Exchange(ref _bytesPerSecond, Math.Max(64 * 1024, value));
    }

    /// <summary>Whether the share's average has come back to within the burst, so its next file may start.</summary>
    public bool Ready(string share, long nowMs)
    {
        lock (_debtUntil) return !_debtUntil.TryGetValue(share, out long debt) || debt - BurstMs <= nowMs;
    }

    /// <summary>Charges bytes read from a share (after the read) without waiting.</summary>
    public void Charge(string share, long bytes, long nowMs)
    {
        if (bytes > 0) Reserve(share, bytes, nowMs);
    }

    /// <summary>Charges bytes about to be read and returns how long to wait first so the share keeps to its average.</summary>
    long Reserve(string share, long bytes, long nowMs)
    {
        long rate = BytesPerSecond;
        lock (_debtUntil)
        {
            long start = _debtUntil.TryGetValue(share, out long debt) ? Math.Max(debt, nowMs) : nowMs;
            _debtUntil[share] = start + bytes * 1000 / rate;
            return start - BurstMs - nowMs;
        }
    }

    /// <summary>
    /// Waits before reading bytes from a share (the readers' <c>NetworkThrottle</c> hook); returns the milliseconds it
    /// waited, which the reader leaves out of its time limit. Cancellation ends the wait at once.
    /// </summary>
    public long Wait(string share, long bytes, CancellationToken ct)
    {
        if (bytes <= 0) return 0;
        long wait = Reserve(share, bytes, _clock());
        if (wait <= 0) return 0;
        long started = Environment.TickCount64;
        ct.WaitHandle.WaitOne((int)Math.Min(wait, MaxWaitMs));
        ct.ThrowIfCancellationRequested();
        return Environment.TickCount64 - started;
    }
}

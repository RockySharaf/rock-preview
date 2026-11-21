namespace Anything.Core;

/// <summary>
/// The references Windows' shell holds on the app's process while it works on its own threads in it: a copy, move or
/// delete started in the hosted Explorer view (paste, drag and drop, its progress window), or a Properties window. The
/// app registers a reference the shell takes and lets go (SetProcessReference); this counts them and decides whether
/// that work is still going, so the process waits for it before it ends and automatic updates wait too.
/// </summary>
/// <remarks>
/// A reference alone isn't trusted for ever: a shell component that never let go would keep an invisible process
/// alive. Work counts as going while a reference is held and either the shell shows a window in the process (the
/// copy's progress window appears within a few seconds and stays until it ends) or, within <see cref="Grace"/>, a
/// reference was taken or let go or such a window was there.
/// </remarks>
public sealed class ShellWork
{
    /// <summary>How long a held reference counts as work without a window to show for it.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    readonly Func<long> _now;
    int _held;
    long _changedAt;

    /// <param name="now">Milliseconds from any fixed start (Environment.TickCount64 unless a test sets it).</param>
    public ShellWork(Func<long>? now = null)
    {
        _now = now ?? (() => Environment.TickCount64);
        _changedAt = long.MinValue / 2;
    }

    /// <summary>References the shell holds now.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>The shell took a reference (any thread); the count after it.</summary>
    public int AddRef()
    {
        Volatile.Write(ref _changedAt, _now());
        return Interlocked.Increment(ref _held);
    }

    /// <summary>The shell let go of one (any thread); the count after it, never below zero.</summary>
    public int Release()
    {
        Volatile.Write(ref _changedAt, _now());
        int left = Interlocked.Decrement(ref _held);
        if (left < 0) { Interlocked.Increment(ref _held); return 0; }
        return left;
    }

    /// <summary>Whether shell work is going, given whether the shell shows a window in the process now.</summary>
    public bool Busy(bool shellWindowShown)
    {
        if (Held <= 0) return false;
        // A window seen counts like a change: the grace runs from when it was last there.
        if (shellWindowShown) { Volatile.Write(ref _changedAt, _now()); return true; }
        return _now() - Volatile.Read(ref _changedAt) < (long)Grace.TotalMilliseconds;
    }
}

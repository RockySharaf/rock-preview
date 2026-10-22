namespace Anything.Core;

/// <summary>
/// Replacing a program file that ran a moment ago: Windows, or a virus scan, can hold it a little after its process
/// exits. That shows as a sharing violation (IOException) or as access denied (UnauthorizedAccessException), so both are
/// tried again.
/// </summary>
public static class FileRetry
{
    /// <summary>How many times a busy file is tried, and how long to wait between tries: about 10 seconds in all.</summary>
    public const int Attempts = 41;
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Runs <paramref name="action"/>, again after <paramref name="sleep"/> (Thread.Sleep of <see cref="Delay"/> by default)
    /// while the file is busy, at most <paramref name="attempts"/> times; the last failure is thrown.
    /// </summary>
    public static void Run(Action action, int attempts = Attempts, Action? sleep = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception e) when (IsBusy(e) && attempt < attempts) { (sleep ?? (() => Thread.Sleep(Delay)))(); }
        }
    }

    /// <summary>A failure worth trying again: the file is held, or access to it is denied for now.</summary>
    public static bool IsBusy(Exception e) => e is IOException or UnauthorizedAccessException;
}

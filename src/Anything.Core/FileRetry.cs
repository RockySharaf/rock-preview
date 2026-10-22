namespace Anything.Core;

/// <summary>
/// Replacing or deleting a program file that ran a moment ago: Windows, or a virus scan, can hold it a little after its
/// process exits. Replacing or deleting a file still open fails with access denied (UnauthorizedAccessException), other
/// holds with a sharing violation (IOException), so both are tried again.
/// </summary>
public static class FileRetry
{
    /// <summary>How many times a busy file is tried, and how long to wait between tries: about 10 seconds of waiting.</summary>
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

    /// <summary>
    /// Puts a copy of <paramref name="from"/> in place of <paramref name="to"/> through "<paramref name="to"/>.new": copied
    /// once (a program is tens of MB), then moved over <paramref name="to"/>, which is tried again while it is held. Read-only
    /// (from setup run off a disc or a share) stops neither, and the .new file is never left behind after a failure.
    /// </summary>
    public static void Replace(string from, string to, int attempts = Attempts, Action? sleep = null)
    {
        string tmp = to + ".new";
        try
        {
            Run(() => { ClearReadOnly(tmp); File.Copy(from, tmp, overwrite: true); ClearReadOnly(tmp); }, attempts, sleep);
            Run(() =>
            {
                if (!File.Exists(tmp)) { File.Copy(from, tmp, overwrite: true); ClearReadOnly(tmp); } // a scan took it away
                ClearReadOnly(to);
                File.Move(tmp, to, overwrite: true);
            }, attempts, sleep);
        }
        catch (Exception)
        {
            ClearReadOnly(tmp);
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Deletes <paramref name="path"/> (read-only too), tried again while it is held; nothing when it isn't there.</summary>
    public static void Delete(string path, int attempts = Attempts, Action? sleep = null)
        => Run(() => { if (!File.Exists(path)) return; ClearReadOnly(path); File.Delete(path); }, attempts, sleep);

    static void ClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path) && File.GetAttributes(path) is var a && (a & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

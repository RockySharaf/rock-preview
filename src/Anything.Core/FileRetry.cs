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
    public static void Run(Action action, int attempts = Attempts, Action? sleep = null) => Run(action, IsBusy, attempts, sleep);

    static void Run(Action action, Func<Exception, bool> busy, int attempts, Action? sleep)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception e) when (busy(e) && attempt < attempts) { (sleep ?? (() => Thread.Sleep(Delay)))(); }
        }
    }

    /// <summary>A failure worth trying again: the file is held, or access to it is denied for now.</summary>
    public static bool IsBusy(Exception e) => e is IOException or UnauthorizedAccessException;

    /// <summary>
    /// Puts a copy of <paramref name="from"/> in place of <paramref name="to"/> through "<paramref name="to"/>.new": copied
    /// once (a program is tens of MB), then moved over <paramref name="to"/>, which is tried again while it is held. Read-only
    /// (from setup run off a disc or a share) stops neither, and the .new file is never left behind after a failure. With
    /// <paramref name="moveAsideWhenHeld"/>, a program file still held after every try (it still runs, or Windows is
    /// writing a crash report for it) is renamed out of the way instead (<see cref="TryMoveAside"/>). Throws
    /// <see cref="FileHeldException"/> when <paramref name="to"/> is still held after all that; a folder that denies
    /// writing fails at once, with access denied.
    /// </summary>
    public static void Replace(string from, string to, int attempts = Attempts, Action? sleep = null, bool moveAsideWhenHeld = false)
    {
        string tmp = to + ".new";
        bool moving = false;
        try
        {
            // Nothing holds a file not made yet: only a scan of a .new left from before (a sharing violation) is waited for.
            Run(() => { ClearReadOnly(tmp); File.Copy(from, tmp, overwrite: true); ClearReadOnly(tmp); }, e => e is IOException, attempts, sleep);
            Run(() =>
            {
                moving = false;
                if (!File.Exists(tmp)) { File.Copy(from, tmp, overwrite: true); ClearReadOnly(tmp); } // a scan took it away
                ClearReadOnly(to);
                moving = true;
                File.Move(tmp, to, overwrite: true);
            }, IsBusy, attempts, sleep);
        }
        catch (Exception e)
        {
            if (moving && IsBusy(e) && moveAsideWhenHeld && TryMoveAside(tmp, to)) return;
            ClearReadOnly(tmp);
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (moving && IsBusy(e)) throw new FileHeldException(to, e);
            throw;
        }
    }

    /// <summary>
    /// A program file Windows still holds can't be replaced or deleted, but it can be renamed (a running program's file
    /// can): <paramref name="to"/> goes by "<paramref name="to"/>.old" (".old2"... when an earlier one is still held too)
    /// and <paramref name="tmp"/> takes its name. The renamed file is deleted at once if it is free, otherwise later
    /// (<see cref="DeleteAside"/>). False, with everything as it was, when even renaming isn't allowed.
    /// </summary>
    static bool TryMoveAside(string tmp, string to)
    {
        DeleteAside(to); // earlier ones that are free by now
        string? aside = null;
        for (int n = 1; aside == null && n < 100; n++)
        {
            string candidate = to + ".old" + (n == 1 ? "" : n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!File.Exists(candidate)) aside = candidate;
        }
        if (aside == null || !File.Exists(tmp)) return false;
        try { File.Move(to, aside); }
        catch (Exception e) when (IsBusy(e)) { return false; }
        try { File.Move(tmp, to); }
        catch (Exception e) when (IsBusy(e))
        {
            try { File.Move(aside, to); } catch (IOException) { } catch (UnauthorizedAccessException) { } // the old one back
            return false;
        }
        try { File.Delete(aside); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return true;
    }

    /// <summary>
    /// Deletes what <see cref="Replace"/> renamed out of the way beside <paramref name="to"/> ("<paramref name="to"/>.old",
    /// ".old2"...) once it is free; one still held stays for next time. Never throws.
    /// </summary>
    public static void DeleteAside(string to)
    {
        try
        {
            string? dir = Path.GetDirectoryName(to);
            string stem = Path.GetFileName(to) + ".old";
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (string file in Directory.EnumerateFiles(dir, stem + "*"))
            {
                string suffix = Path.GetFileName(file)[stem.Length..];
                if (!suffix.All(char.IsAsciiDigit)) continue; // "RockPreview.exe.older" is someone else's
                ClearReadOnly(file);
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

/// <summary>A file that couldn't be replaced because something still held it after every try (the original is inner).</summary>
public sealed class FileHeldException(string path, Exception inner) : IOException($"{path} is still in use, so it couldn't be replaced.", inner)
{
    /// <summary>The file that is still held.</summary>
    public string Path { get; } = path;
}

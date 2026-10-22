using Anything.Core;

namespace Anything.Tests;

/// <summary>Replacing a file that a closing program, or a scan of it, still holds for a moment.</summary>
public sealed class FileRetryTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("fileretry-").FullName;

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void AccessDeniedIsTriedAgainLikeASharingViolation()
    {
        int calls = 0, sleeps = 0;
        FileRetry.Run(() => { if (++calls == 1) throw new UnauthorizedAccessException(); if (calls == 2) throw new IOException(); }, sleep: () => sleeps++);
        Assert.Equal(3, calls);
        Assert.Equal(2, sleeps);
    }

    [Fact]
    public void ABusyFileIsTriedAtMostAttemptsTimesAndTheLastFailureIsThrown()
    {
        int calls = 0, sleeps = 0;
        var e = Assert.Throws<UnauthorizedAccessException>(() =>
            FileRetry.Run(() => { calls++; throw new UnauthorizedAccessException("still denied"); }, attempts: 5, sleep: () => sleeps++));
        Assert.Equal("still denied", e.Message);
        Assert.Equal(5, calls);
        Assert.Equal(4, sleeps);
    }

    [Fact]
    public void OtherFailuresAreNotTriedAgain()
    {
        int calls = 0;
        Assert.Throws<ArgumentException>(() => FileRetry.Run(() => { calls++; throw new ArgumentException(); }, sleep: () => { }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void TheDefaultWaitsAboutTenSecondsInAll()
        => Assert.InRange(FileRetry.Delay * (FileRetry.Attempts - 1), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(11));

    [Fact]
    public void AProgramFileThatDeniesAccessForAMomentIsReplacedOnceItIsFree()
    {
        // A read-only target makes the replace fail with access denied too.
        string exe = Path.Combine(_dir, "RockPreview.exe"), tmp = exe + ".new";
        File.WriteAllText(exe, "old");
        File.SetAttributes(exe, FileAttributes.ReadOnly);
        File.WriteAllText(tmp, "new");
        int sleeps = 0;
        var seen = new List<Type>();
        FileRetry.Run(() => Move(tmp, exe, seen), sleep: () => { if (++sleeps == 2) File.SetAttributes(exe, FileAttributes.Normal); });
        Assert.Equal([typeof(UnauthorizedAccessException), typeof(UnauthorizedAccessException)], seen);
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.False(File.Exists(tmp));
    }

    [Fact]
    public void AProgramFileHeldOpenForAMomentIsReplacedOnceItIsLetGo()
    {
        // Replacing a file that is still open fails with access denied, not a sharing violation: why 1.7.1's update, which
        // tried again only on IOException, gave up at once when the old copy hadn't let go of its file yet.
        string exe = Path.Combine(_dir, "RockPreview.exe"), tmp = exe + ".new";
        File.WriteAllText(exe, "old");
        File.WriteAllText(tmp, "new");
        var held = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None);
        int sleeps = 0;
        var seen = new List<Type>();
        try { FileRetry.Run(() => Move(tmp, exe, seen), sleep: () => { if (++sleeps == 1) held.Dispose(); }); }
        finally { held.Dispose(); }
        Assert.Equal([typeof(UnauthorizedAccessException)], seen);
        Assert.Equal("new", File.ReadAllText(exe));
    }

    /// <summary>Replaces <paramref name="to"/>, noting the type of each failure.</summary>
    static void Move(string from, string to, List<Type> seen)
    {
        try { File.Move(from, to, overwrite: true); }
        catch (Exception e) { seen.Add(e.GetType()); throw; }
    }
}

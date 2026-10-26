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

    // ---- Replace and Delete: what Setup does with the program file ---------------------------------------------

    [Fact]
    public void AnUpdateCopiesTheProgramOnceWhileTheOldFileIsStillHeld()
    {
        string setup = Path.Combine(_dir, "RockPreview-Setup.exe"), exe = Path.Combine(_dir, "RockPreview.exe");
        File.WriteAllText(setup, "1.7.2");
        File.WriteAllText(exe, "1.7.1");
        var held = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None);
        int sleeps = 0;
        // Were it copied again on each try, the change made to the source during the wait would be what lands.
        try { FileRetry.Replace(setup, exe, sleep: () => { if (++sleeps == 1) File.WriteAllText(setup, "copied twice"); else held.Dispose(); }); }
        finally { held.Dispose(); }
        Assert.Equal(2, sleeps);
        Assert.Equal("1.7.2", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public void AnUpdateThatStillCantReplaceTheFileLeavesTheOldOneAndNoNewFile()
    {
        string setup = Path.Combine(_dir, "RockPreview-Setup.exe"), exe = Path.Combine(_dir, "RockPreview.exe");
        File.WriteAllText(setup, "1.7.2");
        File.WriteAllText(exe, "1.7.1");
        int sleeps = 0;
        FileHeldException held;
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None))
            held = Assert.Throws<FileHeldException>(() => FileRetry.Replace(setup, exe, attempts: 3, sleep: () => sleeps++));
        Assert.Equal(exe, held.Path);
        Assert.IsType<UnauthorizedAccessException>(held.InnerException);
        Assert.Equal(2, sleeps);
        Assert.Equal("1.7.1", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public void AccessDeniedMakingTheNewFileFailsAtOnceAndIsntCalledHeld()
    {
        // Nothing can hold a file that isn't made yet, so access denied there is final (here: a source that is a folder).
        string setup = Path.Combine(_dir, "setup"), exe = Path.Combine(_dir, "RockPreview.exe");
        Directory.CreateDirectory(setup);
        File.WriteAllText(exe, "1.7.1");
        var e = Assert.Throws<UnauthorizedAccessException>(() => FileRetry.Replace(setup, exe, sleep: () => throw new InvalidOperationException("waited")));
        Assert.IsNotType<FileHeldException>(e);
        Assert.Equal("1.7.1", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".new"));
        Directory.Delete(setup);
    }

    [Fact]
    public void ReadOnlyFilesDontStopAnUpdateAndTheProgramIsntLeftReadOnly()
    {
        // Setup run off a disc or a share is read-only, and so is a program file an earlier copy of that setup put in place.
        string setup = Path.Combine(_dir, "RockPreview-Setup.exe"), exe = Path.Combine(_dir, "RockPreview.exe");
        File.WriteAllText(setup, "1.7.2");
        File.WriteAllText(exe, "1.7.1");
        File.WriteAllText(exe + ".new", "left by a failed update");
        foreach (var f in new[] { setup, exe, exe + ".new" }) File.SetAttributes(f, FileAttributes.ReadOnly);
        FileRetry.Replace(setup, exe, sleep: () => throw new InvalidOperationException("nothing should be busy"));
        Assert.Equal("1.7.2", File.ReadAllText(exe));
        Assert.False(File.GetAttributes(exe).HasFlag(FileAttributes.ReadOnly));
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public void ANewFileTakenAwayBetweenTriesIsCopiedAgain()
    {
        string setup = Path.Combine(_dir, "RockPreview-Setup.exe"), exe = Path.Combine(_dir, "RockPreview.exe");
        File.WriteAllText(setup, "1.7.2");
        File.WriteAllText(exe, "1.7.1");
        var held = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None);
        try { FileRetry.Replace(setup, exe, sleep: () => { File.Delete(exe + ".new"); held.Dispose(); }); }
        finally { held.Dispose(); }
        Assert.Equal("1.7.2", File.ReadAllText(exe));
    }

    [Fact]
    public void AnUninstallDeletesTheProgramOnceTheClosedCopyLetsGoOfIt()
    {
        string exe = Path.Combine(_dir, "RockPreview.exe");
        File.WriteAllText(exe, "1.7.1");
        File.SetAttributes(exe, FileAttributes.ReadOnly);
        var held = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None);
        int sleeps = 0;
        try { FileRetry.Delete(exe, sleep: () => { sleeps++; held.Dispose(); }); }
        finally { held.Dispose(); }
        Assert.Equal(1, sleeps);
        Assert.False(File.Exists(exe));
        FileRetry.Delete(exe, sleep: () => throw new InvalidOperationException("a missing file isn't busy"));
    }

    [Fact]
    public void AProgramStillRunningIsRenamedOutOfTheWayAndReplaced()
    {
        // Mapped as a program image, the way Windows holds a program that runs (or one a crash report is being written
        // for): it can't be replaced or deleted, only renamed.
        string setup = Path.Combine(_dir, "RockPreview-Setup.dll"), exe = Path.Combine(_dir, "RockPreview.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), exe);
        File.WriteAllText(setup, "1.7.9");
        IntPtr image = LoadLibraryEx(exe, IntPtr.Zero, DONT_RESOLVE_DLL_REFERENCES);
        Assert.NotEqual(IntPtr.Zero, image);
        try
        {
            int sleeps = 0;
            FileRetry.Replace(setup, exe, attempts: 3, sleep: () => sleeps++, moveAsideWhenHeld: true);
            Assert.Equal(2, sleeps); // tried as usual first
            Assert.Equal("1.7.9", File.ReadAllText(exe));
            Assert.True(File.Exists(exe + ".old")); // still mapped: deleted later
            Assert.False(File.Exists(exe + ".new"));
            FileRetry.DeleteAside(exe);
            Assert.True(File.Exists(exe + ".old")); // still held: kept for next time
        }
        finally { FreeLibrary(image); }
        FileRetry.DeleteAside(exe);
        Assert.False(File.Exists(exe + ".old"));
        Assert.Equal("1.7.9", File.ReadAllText(exe));
    }

    [Fact]
    public void WithoutMovingAsideAProgramStillRunningIsntReplaced()
    {
        string setup = Path.Combine(_dir, "RockPreview-Setup.dll"), exe = Path.Combine(_dir, "RockPreview.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), exe);
        File.WriteAllText(setup, "1.7.9");
        IntPtr image = LoadLibraryEx(exe, IntPtr.Zero, DONT_RESOLVE_DLL_REFERENCES);
        try { Assert.Throws<FileHeldException>(() => FileRetry.Replace(setup, exe, attempts: 2, sleep: () => { })); }
        finally { FreeLibrary(image); }
        Assert.NotEqual("1.7.9", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public void AnEarlierRenamedProgramStillHeldMakesTheNextOneOld2()
    {
        string setup = Path.Combine(_dir, "RockPreview-Setup.dll"), exe = Path.Combine(_dir, "RockPreview.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), exe);
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), exe + ".old");
        File.WriteAllText(setup, "1.7.9");
        IntPtr earlier = LoadLibraryEx(exe + ".old", IntPtr.Zero, DONT_RESOLVE_DLL_REFERENCES);
        IntPtr image = LoadLibraryEx(exe, IntPtr.Zero, DONT_RESOLVE_DLL_REFERENCES);
        try
        {
            FileRetry.Replace(setup, exe, attempts: 2, sleep: () => { }, moveAsideWhenHeld: true);
            Assert.Equal("1.7.9", File.ReadAllText(exe));
            Assert.True(File.Exists(exe + ".old"));
            Assert.True(File.Exists(exe + ".old2"));
        }
        finally { FreeLibrary(image); FreeLibrary(earlier); }
        FileRetry.DeleteAside(exe);
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(exe + ".old2"));
    }

    [Fact]
    public void AFileHeldWithoutSharingIsntRenamedEitherAndStaysAsItWas()
    {
        string setup = Path.Combine(_dir, "RockPreview-Setup.exe"), exe = Path.Combine(_dir, "RockPreview.exe");
        File.WriteAllText(setup, "1.7.9");
        File.WriteAllText(exe, "1.7.8");
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<FileHeldException>(() => FileRetry.Replace(setup, exe, attempts: 2, sleep: () => { }, moveAsideWhenHeld: true));
        Assert.Equal("1.7.8", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
    }

    [Fact]
    public void OnlyRenamedProgramsAreDeletedBesideIt()
    {
        string exe = Path.Combine(_dir, "RockPreview.exe");
        foreach (var name in new[] { ".old", ".old2", ".old17", ".older", ".old.txt", "" }) File.WriteAllText(exe + name, name);
        File.SetAttributes(exe + ".old2", FileAttributes.ReadOnly);
        FileRetry.DeleteAside(exe);
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(exe + ".old2"));
        Assert.False(File.Exists(exe + ".old17"));
        Assert.True(File.Exists(exe + ".older"));
        Assert.True(File.Exists(exe + ".old.txt"));
        Assert.True(File.Exists(exe));
        FileRetry.DeleteAside(Path.Combine(_dir, "missing", "RockPreview.exe")); // no folder: nothing, no throw
    }

    const uint DONT_RESOLVE_DLL_REFERENCES = 0x1;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr module);

    /// <summary>Replaces <paramref name="to"/>, noting the type of each failure.</summary>
    static void Move(string from, string to, List<Type> seen)
    {
        try { File.Move(from, to, overwrite: true); }
        catch (Exception e) { seen.Add(e.GetType()); throw; }
    }
}

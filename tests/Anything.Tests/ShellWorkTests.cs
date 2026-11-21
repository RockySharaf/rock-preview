using Anything.Core;

namespace Anything.Tests;

/// <summary>When the shell's work in the process (a copy from the Explorer view) keeps it from ending or updating.</summary>
public sealed class ShellWorkTests
{
    long _now = 1_000_000;
    ShellWork New() => new(() => _now);
    void Wait(TimeSpan t) => _now += (long)t.TotalMilliseconds;

    [Fact]
    public void NothingHeldIsNeverBusy()
    {
        var work = New();
        Assert.False(work.Busy(shellWindowShown: false));
        Assert.False(work.Busy(shellWindowShown: true)); // a window of its own, with no work behind it
    }

    [Fact]
    public void AReferenceJustTakenIsBusy()
    {
        var work = New();
        Assert.Equal(1, work.AddRef());
        Assert.True(work.Busy(false));
    }

    [Fact]
    public void LettingGoOfTheLastEndsIt()
    {
        var work = New();
        work.AddRef();
        work.AddRef();
        Assert.Equal(1, work.Release());
        Assert.True(work.Busy(true));
        Assert.Equal(0, work.Release());
        Assert.False(work.Busy(true));
    }

    [Fact]
    public void ALongCopyWithItsProgressWindowStaysBusy()
    {
        var work = New();
        work.AddRef();
        for (int i = 0; i < 120; i++)
        {
            Wait(TimeSpan.FromMinutes(1));
            Assert.True(work.Busy(shellWindowShown: true));
        }
    }

    [Fact]
    public void AReferenceNeverLetGoWithoutAWindowStopsCountingAfterTheGrace()
    {
        var work = New();
        work.AddRef();
        Wait(ShellWork.Grace - TimeSpan.FromSeconds(1));
        Assert.True(work.Busy(false));
        Wait(TimeSpan.FromSeconds(2));
        Assert.False(work.Busy(false));
    }

    [Fact]
    public void TheGraceRunsFromWhenTheWindowWasLastThere()
    {
        var work = New();
        work.AddRef();
        Wait(TimeSpan.FromMinutes(10));
        Assert.True(work.Busy(true));
        Wait(ShellWork.Grace - TimeSpan.FromSeconds(1));
        Assert.True(work.Busy(false)); // the copy finishing up after its window closed
        Wait(TimeSpan.FromSeconds(2));
        Assert.False(work.Busy(false));
    }

    [Fact]
    public void MoreReleasesThanReferencesStayAtZero()
    {
        var work = New();
        Assert.Equal(0, work.Release());
        Assert.Equal(0, work.Held);
        Assert.Equal(1, work.AddRef());
    }
}

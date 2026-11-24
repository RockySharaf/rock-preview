using System.Diagnostics;
using Anything.Core.Indexing;
using Microsoft.Win32.SafeHandles;

namespace Anything.Tests.Indexing;

/// <summary>
/// The Store copy's elevated helper ends with its Rock Preview (<see cref="VolumeService.CancelWhenAny"/>): when that
/// process ends, closed or crashed, or when it sets the stop event of the helper's start, the token the helper serves
/// with is cancelled. A short-lived process the test starts stands in for Rock Preview, never the real one.
/// </summary>
public sealed class HelperEndTests
{
    [Fact]
    public void The_stop_event_ends_it()
    {
        using var parent = new ManualResetEvent(false);
        using var stop = new ManualResetEvent(false);
        using var cts = VolumeService.CancelWhenAny(parent, stop);
        Assert.False(cts.Token.WaitHandle.WaitOne(200));
        stop.Set();
        Assert.True(cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)), "setting the stop event didn't end it");
    }

    [Fact]
    public void Its_Rock_Preview_ending_ends_it()
    {
        // ping takes about two seconds for three replies from this PC itself, then exits.
        using var parent = Process.Start(new ProcessStartInfo("ping.exe", "-n 3 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        _ = parent.StandardOutput.ReadToEndAsync();
        using var end = new ProcessEnd(parent);
        using var stop = new ManualResetEvent(false);
        using var cts = VolumeService.CancelWhenAny(end, stop);
        Assert.False(cts.Token.WaitHandle.WaitOne(200));
        Assert.True(cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)), "the process's end didn't end it");
        Assert.True(parent.WaitForExit(5000));
    }

    [Fact]
    public void A_handle_that_can_t_be_waited_on_ends_it()
    {
        var gone = new ManualResetEvent(false);
        gone.Dispose();
        using var cts = VolumeService.CancelWhenAny(gone);
        Assert.True(cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
    }

    /// <summary>A process's handle to wait on, as the helper waits on its Rock Preview's (not owned: the Process keeps it).</summary>
    sealed class ProcessEnd : WaitHandle
    {
        public ProcessEnd(Process process) => SafeWaitHandle = new SafeWaitHandle(process.SafeHandle.DangerousGetHandle(), ownsHandle: false);
    }
}

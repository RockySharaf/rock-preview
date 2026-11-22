using System.Runtime.ExceptionServices;
using Anything.Core.Content.Indexing;

namespace Anything.Tests;

/// <summary>
/// Runs hostile input on a thread of its own and measures the CPU time it used, not the wall clock: a busy PC (other
/// tests, another build) can hold a thread off the CPU for seconds, which says nothing about the reader, while a loop
/// that should have been bounded burns CPU every time. The wall clock only catches a real hang, with a long limit.
/// </summary>
internal static class CpuBound
{
    /// <summary>Long enough that only a real hang reaches it, however busy the PC is.</summary>
    public const int HangMilliseconds = 60_000;

    /// <summary>
    /// Runs the work on a new thread (so a busy thread pool can't delay its start); false when it is still running after
    /// <see cref="HangMilliseconds"/>. <paramref name="cpuMs"/> is the CPU time the work used (kernel and user). The work
    /// catches its own exceptions: one escaping a thread of its own would end the whole test run.
    /// </summary>
    public static bool Run(Action work, out double cpuMs)
    {
        double used = 0;
        var thread = new Thread(() =>
        {
            var before = ContentNative.ThreadCpuTime();
            work();
            Volatile.Write(ref used, (ContentNative.ThreadCpuTime() - before).TotalMilliseconds);
        }) { IsBackground = true };
        thread.Start();
        bool finished = thread.Join(HangMilliseconds);
        cpuMs = Volatile.Read(ref used);
        return finished;
    }

    /// <summary>
    /// Runs the work as <see cref="Run"/> does and returns what it returned: an exception it throws is thrown here, and a
    /// hang fails the test. <paramref name="allocated"/> is what the work allocated on its thread.
    /// </summary>
    public static T Measure<T>(Func<T> work, out double cpuMs, out long allocated)
    {
        T? result = default;
        Exception? error = null;
        long bytes = 0;
        bool finished = Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            try { result = work(); }
            catch (Exception e) { error = e; }
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }, out cpuMs);
        if (!finished) throw new Xunit.Sdk.XunitException($"still running after {HangMilliseconds} ms");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        allocated = bytes;
        return result!;
    }
}

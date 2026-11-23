using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace PreviewHarness.Bench;

/// <summary>Medians and percentiles.</summary>
internal static class Stats
{
    public static double Median(IEnumerable<double> v) => Percentile(v, 50);

    public static double Percentile(IEnumerable<double> values, double p)
    {
        var a = values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        if (a.Length == 0) return double.NaN;
        if (a.Length == 1) return a[0];
        double rank = p / 100.0 * (a.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return a[lo] + (a[hi] - a[lo]) * (rank - lo);
    }

    public static double R(double v, int digits = 1) => double.IsNaN(v) ? double.NaN : Math.Round(v, digits);
}

/// <summary>
/// Samples the whole PC's processor load (GetSystemTimes) every 200 ms on a background thread, so that each
/// measurement can say how busy the machine was while it ran.
/// </summary>
internal sealed class LoadMonitor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low, High; public long Value => (long)High << 32 | Low; }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    public static readonly LoadMonitor Shared = new();

    private readonly List<(double T, double Load)> _samples = new();
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private LoadMonitor()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "load monitor", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public double Now => _clock.Elapsed.TotalMilliseconds;

    private void Loop()
    {
        GetSystemTimes(out var i0, out var k0, out var u0);
        while (!_stop)
        {
            Thread.Sleep(200);
            GetSystemTimes(out var i1, out var k1, out var u1);
            long idle = i1.Value - i0.Value, total = (k1.Value - k0.Value) + (u1.Value - u0.Value);
            double load = total > 0 ? Math.Clamp(1.0 - (double)idle / total, 0, 1) * 100 : 0;
            lock (_samples) _samples.Add((Now, load));
            (i0, k0, u0) = (i1, k1, u1);
        }
    }

    /// <summary>Average and highest load over [from, to] (ms on <see cref="Now"/>), using the samples that overlap it.</summary>
    public (double Avg, double Max) Between(double from, double to)
    {
        lock (_samples)
        {
            // A sample at time T covers (T - 200, T].
            var hit = _samples.Where(s => s.T >= from && s.T - 200 <= to).Select(s => s.Load).ToList();
            if (hit.Count == 0)
            {
                var next = _samples.FirstOrDefault(s => s.T >= to);
                if (next.T > 0) hit.Add(next.Load);
                else if (_samples.Count > 0) hit.Add(_samples[^1].Load);
            }
            return hit.Count == 0 ? (double.NaN, double.NaN) : (hit.Average(), hit.Max());
        }
    }

    /// <summary>Waits until the next sample after now exists, so a probe that just ended can be given its load.</summary>
    public (double Avg, double Max) BetweenSettled(double from, double to)
    {
        for (int i = 0; i < 6; i++)
        {
            lock (_samples) if (_samples.Count > 0 && _samples[^1].T >= to) break;
            Thread.Sleep(50);
        }
        return Between(from, to);
    }

    public List<double> AllLoads() { lock (_samples) return _samples.Select(s => s.Load).ToList(); }

    public void Dispose() => _stop = true;
}

/// <summary>This process's processor time (precise cycle count and coarse kernel+user time) and file I/O counters.</summary>
internal static class ProcessMeter
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS c);
    [DllImport("kernel32.dll")] private static extern bool QueryProcessCycleTime(IntPtr h, out ulong cycles);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);

    /// <summary>Nominal processor clock from the registry (MHz), used to turn cycle counts into milliseconds.</summary>
    public static readonly double Mhz = ReadMhz();

    private static double ReadMhz()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (k?.GetValue("~MHz") is int mhz && mhz > 0) return mhz;
        }
        catch { }
        return double.NaN;
    }

    public readonly record struct Snapshot(ulong Cycles, long Times100ns, ulong BytesRead, ulong ReadOps);

    public static Snapshot Take()
    {
        var h = GetCurrentProcess();
        QueryProcessCycleTime(h, out ulong cyc);
        GetProcessTimes(h, out _, out _, out long k, out long u);
        GetProcessIoCounters(h, out var io);
        return new Snapshot(cyc, k + u, io.ReadTransferCount, io.ReadOperationCount);
    }

    /// <summary>Processor milliseconds between two snapshots, from the cycle counter (precise; nominal clock).</summary>
    public static double CpuMs(Snapshot a, Snapshot b) =>
        double.IsNaN(Mhz) ? (b.Times100ns - a.Times100ns) / 10000.0 : (b.Cycles - a.Cycles) / (Mhz * 1000.0);
}

/// <summary>
/// Counts what a decoder actually reads from a file: bytes, read calls, and distinct ranges. Sits between an
/// unbuffered FileStream and the 64 KB buffer the product uses, so the counts are what reaches the file.
/// </summary>
internal sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private readonly List<(long Start, long End)> _ranges = new();
    public long BytesRead;
    public int Reads;

    public CountingStream(Stream inner) { _inner = inner; }

    /// <summary>Opens a file the way the product does (shared read), counted, with a 64 KB buffer on top.</summary>
    public static (Stream Stream, CountingStream Counter) Open(string path, int buffer = 1 << 16)
    {
        var fs = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = 0,
        });
        var c = new CountingStream(fs);
        return (buffer > 0 ? new BufferedStream(c, buffer) : c, c);
    }

    private void Note(long pos, int n)
    {
        if (n <= 0) return;
        BytesRead += n;
        Reads++;
        lock (_ranges) _ranges.Add((pos, pos + n));
    }

    /// <summary>Merged ranges: (distinct bytes, number of separate ranges).</summary>
    public (long Distinct, int Ranges) Coverage()
    {
        List<(long Start, long End)> r;
        lock (_ranges) r = _ranges.OrderBy(x => x.Start).ToList();
        long distinct = 0;
        int count = 0;
        long cs = -1, ce = -1;
        foreach (var (s, e) in r)
        {
            if (s > ce) { if (ce > cs) distinct += ce - cs; cs = s; ce = e; count++; }
            else if (e > ce) ce = e;
        }
        if (ce > cs) distinct += ce - cs;
        return (distinct, count);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long pos = _inner.Position;
        int n = _inner.Read(buffer, offset, count);
        Note(pos, n);
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        long pos = _inner.Position;
        int n = _inner.Read(buffer);
        Note(pos, n);
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
}

/// <summary>A dedicated STA thread that runs work items one at a time (shell COM objects are apartment-threaded).</summary>
internal sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaWorker(string name)
    {
        _thread = new Thread(() => { foreach (var a in _queue.GetConsumingEnumerable()) a(); }) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public T Run<T>(Func<T> f, int timeoutMs = 60000)
    {
        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        _queue.Add(() =>
        {
            try { result = f(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(timeoutMs)) throw new TimeoutException("STA work did not finish in " + timeoutMs + " ms");
        if (error != null) throw new InvalidOperationException("STA work failed", error);
        return result;
    }

    public void Post(Action a) => _queue.Add(a);

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>The commit of the working copy (git, with a time limit).</summary>
internal static class GitInfo
{
    public static string Commit(string repoDir)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                WorkingDirectory = repoDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string s = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return "unknown"; }
            psi.Arguments = "status --porcelain --untracked-files=no";
            using var q = Process.Start(psi)!;
            string dirty = q.StandardOutput.ReadToEnd();
            if (!q.WaitForExit(10000)) { try { q.Kill(); } catch { } }
            return s.Trim() + (dirty.Trim().Length > 0 ? "+changes" : "");
        }
        catch
        {
            return "unknown";
        }
    }
}

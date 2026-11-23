// Rock Preview 1.7.27's StaPool (commit e568bab) and the two Shell calls its ThumbnailProvider made through it, kept
// frozen for the benchmark's "today" pipeline: the product's StaPool has since been repaired (its time-out now counts
// from the start of a job, not from when it was queued), and today's numbers must keep today's behaviour, including a
// queued request that waited 12 s coming back empty. Only the class names and the namespace differ; do not improve it.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Anything.Preview.Interop;

namespace PreviewHarness.Bench;

/// <summary>1.7.27's Shell.GetThumbnailAsync and Shell.GetIconAsync, on 1.7.27's StaPool.</summary>
internal static class TodayShell
{
    private static readonly TodayStaPool Pool = new(4, "Today STA");

    public static Task<Bitmap?> GetThumbnailAsync(string path, int size, CancellationToken ct, bool highPriority = true, bool biggerOk = false)
    {
        size = Math.Clamp(size, 16, 2560);
        var flags = Shell.SIIGBF.ThumbnailOnly | (biggerOk ? Shell.SIIGBF.BiggerSizeOk : 0);
        return Pool.RunAsync(() => Shell.GetImageSta(path, size, flags), ct, highPriority, 12000);
    }

    public static Task<Bitmap?> GetIconAsync(string path, int size, CancellationToken ct, bool highPriority = true)
    {
        size = Math.Clamp(size, 16, 256);
        return Pool.RunAsync(() => Shell.GetImageSta(path, size, Shell.SIIGBF.IconOnly), ct, highPriority, 8000);
    }
}

/// <summary>1.7.27's StaPool, unchanged.</summary>
internal sealed class TodayStaPool
{
    private readonly object _lock = new();
    private readonly Queue<Action> _high = new();
    private readonly Queue<Action> _normal = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly string _name;
    private int _threads;
    private int _replacements;

    public TodayStaPool(int count, string name)
    {
        _name = name;
        for (int i = 0; i < count; i++) StartWorker();
    }

    private void StartWorker()
    {
        int n = Interlocked.Increment(ref _threads);
        var t = new Thread(Loop) { IsBackground = true, Name = $"{_name}-{n}" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private void Loop()
    {
        while (true)
        {
            _signal.Wait();
            Action? work = null;
            lock (_lock)
            {
                if (_high.Count > 0) work = _high.Dequeue();
                else if (_normal.Count > 0) work = _normal.Dequeue();
            }
            try { work?.Invoke(); } catch { /* reported through the task */ }
        }
    }

    /// <summary>Runs <paramref name="func"/> on an STA worker. Returns default on timeout; throws on cancellation.</summary>
    public Task<T?> RunAsync<T>(Func<T?> func, CancellationToken ct, bool highPriority = false, int timeoutMs = 15000)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action job = () =>
        {
            if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
            try { tcs.TrySetResult(func()); }
            catch (OperationCanceledException) { tcs.TrySetCanceled(ct); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        };
        lock (_lock) (highPriority ? _high : _normal).Enqueue(job);
        _signal.Release();
        return WaitAsync(tcs.Task, ct, timeoutMs);
    }

    private async Task<T?> WaitAsync<T>(Task<T?> task, CancellationToken ct, int timeoutMs)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The worker running it may be wedged inside a third-party shell extension: add a replacement.
            if (Interlocked.Increment(ref _replacements) <= 8) StartWorker();
            return default;
        }
    }
}

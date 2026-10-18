using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Anything.Preview.Util;

/// <summary>
/// A small pool of long-lived STA threads for shell COM work (thumbnail providers, property handlers, WPF text
/// rendering). Shell extensions are apartment-threaded; calling them from MTA thread-pool threads is unsafe. Two
/// priorities: preview-pane work jumps ahead of the (possibly long) queue of grid thumbnail requests. Work whose
/// token is cancelled before it starts is skipped for free. A hung shell extension can pin a thread forever, so a
/// timed-out job spawns a replacement worker (bounded).
/// </summary>
internal sealed class StaPool
{
    public static readonly StaPool Shared = new(4, "Rock Preview STA");

    private readonly object _lock = new();
    private readonly Queue<Action> _high = new();
    private readonly Queue<Action> _normal = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly string _name;
    private int _threads;
    private int _replacements;

    public StaPool(int count, string name)
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

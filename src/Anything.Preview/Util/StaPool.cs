using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Anything.Preview.Util;

/// <summary>
/// A small pool of long-lived STA threads for shell COM work (property handlers, WPF text rendering, the preview
/// pane's shell calls). Shell extensions are apartment-threaded; calling them from MTA thread-pool threads is unsafe.
/// Two priorities: urgent work jumps ahead of the normal queue. Work whose token is cancelled before it starts is
/// skipped for free. A hung shell extension can pin a thread forever, so a job that runs longer than its time-out
/// (counted from the moment it starts, not from when it was queued) is given up: its thread is replaced (at most 8
/// times in the life of the pool) and ends by itself when the call finally returns; a job cancelled while it runs keeps
/// that time-out, since the caller moving on doesn't unstick its thread. A result nobody waits for any more
/// (timed out, or cancelled while running) is disposed when it is disposable, so a late bitmap doesn't leak.
/// </summary>
internal sealed class StaPool
{
    public static readonly StaPool Shared = new(4, "Rock Preview STA");

    private const int MaxReplacements = 8;

    private sealed class Worker
    {
        // Set when the job this thread runs was given up and a replacement started: the thread ends after it.
        public volatile bool Replaced;
    }

    private readonly object _lock = new();
    private readonly Queue<Action<Worker>> _high = new();
    private readonly Queue<Action<Worker>> _normal = new();
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
        var worker = new Worker();
        var t = new Thread(() => Loop(worker)) { IsBackground = true, Name = $"{_name}-{n}" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private void Loop(Worker worker)
    {
        while (!worker.Replaced)
        {
            _signal.Wait();
            Action<Worker>? work = null;
            lock (_lock)
            {
                if (_high.Count > 0) work = _high.Dequeue();
                else if (_normal.Count > 0) work = _normal.Dequeue();
            }
            try { work?.Invoke(worker); } catch { /* reported through the task */ }
        }
        // A replacement took this thread's place while it was stuck: it ends here, now that the call came back.
    }

    /// <summary>Runs <paramref name="func"/> on an STA worker. Returns default on timeout; throws on cancellation.</summary>
    public Task<T?> RunAsync<T>(Func<T?> func, CancellationToken ct, bool highPriority = false, int timeoutMs = 15000)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Which thread runs the job and when it began (the time-out counts from then); cancelled when the job is skipped.
        var started = new TaskCompletionSource<(Worker Worker, long At)>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Set when the job's call has really returned, however its wait ended: a job cancelled while it runs is still
        // watched until then (WatchCancelled).
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<Worker> job = worker =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                started.TrySetCanceled();
                return;
            }
            started.TrySetResult((worker, Environment.TickCount64));
            try
            {
                var result = func();
                // Nobody takes it when the wait already ended (time-out or cancellation): don't leak a bitmap.
                if (!tcs.TrySetResult(result) && result is IDisposable d) d.Dispose();
            }
            catch (OperationCanceledException) { tcs.TrySetCanceled(ct); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally { lock (done.Task) done.TrySetResult(); }
        };
        lock (_lock) (highPriority ? _high : _normal).Enqueue(job);
        _signal.Release();
        return WaitAsync(tcs, started.Task, done.Task, ct, timeoutMs);
    }

    private async Task<T?> WaitAsync<T>(TaskCompletionSource<T?> tcs, Task<(Worker Worker, long At)> started, Task done, CancellationToken ct, int timeoutMs)
    {
        var task = tcs.Task;
        try
        {
            // Waiting in the queue doesn't count: only a job that runs too long is treated as hung.
            await Task.WhenAny(task, started).WaitAsync(ct).ConfigureAwait(false);
            return await task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Ends the wait for good, so the late result is disposed rather than delivered. If the job finished in the
            // same instant, its answer stands.
            if (!tcs.TrySetResult(default)) return task.IsCompletedSuccessfully ? task.Result : default;
            // The worker running it may be wedged inside a third-party shell extension: replace it.
            Replace(started.IsCompletedSuccessfully ? started.Result.Worker : null, done);
            return default;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A result that arrived in the same instant has no taker now.
            if (!tcs.TrySetCanceled(ct) && task.IsCompletedSuccessfully && task.Result is IDisposable d) d.Dispose();
            // The caller has moved on (the next selection), but a job already running may be the one that never returns:
            // it keeps its time-out. A job still queued is skipped when its turn comes, and its watch ends then.
            if (!done.IsCompleted) _ = WatchCancelled(started, done, timeoutMs);
            throw;
        }
    }

    /// <summary>
    /// A job cancelled while it runs (or before its turn): if it runs past its time-out, counted from its start, its thread
    /// is replaced exactly as on the time-out path; one that ends in time (or never runs) costs nothing more. Its late
    /// result is disposed by the job itself, since nobody takes it. One pending timer per such job, nothing for others.
    /// </summary>
    private async Task WatchCancelled(Task<(Worker Worker, long At)> started, Task done, int timeoutMs)
    {
        await ((Task)started).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!started.IsCompletedSuccessfully) return; // skipped: it never ran
        var (worker, at) = started.Result;
        long left = timeoutMs - (Environment.TickCount64 - at);
        if (left > 0) await done.WaitAsync(TimeSpan.FromMilliseconds(left)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!done.IsCompleted) Replace(worker, done);
    }

    /// <summary>Replaces a worker stuck in a job given up (while the budget of <see cref="MaxReplacements"/> lasts).</summary>
    private void Replace(Worker? stuck, Task done)
    {
        if (stuck == null) return;
        // The worker finishes this exact job under the same lock before it can take another.
        lock (done)
        {
            if (done.IsCompleted || Interlocked.Increment(ref _replacements) > MaxReplacements) return;
            stuck.Replaced = true;
        }
        StartWorker();
    }
}

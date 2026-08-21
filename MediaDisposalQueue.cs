using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace lifeviz;

/// <summary>
/// Reuses one background worker for media-session teardown. AutoClip and video
/// sequence transitions used to create a dedicated OS thread for every retired
/// decoder, doubling the thread churn caused by clip rotation.
/// </summary>
internal static class MediaDisposalQueue
{
    private readonly record struct DisposalWorkItem(IDisposable Resource, string Label);

    private static readonly BlockingCollection<DisposalWorkItem> Queue = new();
    private static readonly ManualResetEventSlim Idle = new(initialState: true);
    private static readonly object StateLock = new();
    private static readonly Thread Worker = StartWorker();
    private static int _pendingCount;

    public static int PendingCount => Math.Max(0, Volatile.Read(ref _pendingCount));

    public static void Enqueue(IDisposable resource, string label)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _ = Worker;

        lock (StateLock)
        {
            _pendingCount++;
            if (_pendingCount == 1)
            {
                Idle.Reset();
            }
        }

        try
        {
            Queue.Add(new DisposalWorkItem(resource, label));
        }
        catch
        {
            CompleteOne();
            throw;
        }
    }

    public static bool Drain(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            lock (StateLock)
            {
                if (_pendingCount == 0)
                {
                    return true;
                }
            }

            TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                ? Timeout.InfiniteTimeSpan
                : timeout - stopwatch.Elapsed;
            if (remaining != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero)
            {
                return false;
            }

            if (!Idle.Wait(remaining))
            {
                lock (StateLock)
                {
                    return _pendingCount == 0;
                }
            }
        }
    }

    private static Thread StartWorker()
    {
        var thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "LifeViz.MediaDispose",
            Priority = ThreadPriority.BelowNormal
        };
        thread.Start();
        return thread;
    }

    private static void WorkerLoop()
    {
        foreach (DisposalWorkItem item in Queue.GetConsumingEnumerable())
        {
            try
            {
                item.Resource.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Background media disposal failed for {item.Label}: {ex.Message}");
            }
            finally
            {
                CompleteOne();
            }
        }
    }

    private static void CompleteOne()
    {
        lock (StateLock)
        {
            _pendingCount--;
            if (_pendingCount == 0)
            {
                Idle.Set();
            }
        }
    }
}

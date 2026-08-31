using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderDB.Retry;

/// <summary>
/// Manages a coarse-grained time bucket schedule.
/// Triggers a callback sequentially for each due bucket.
/// Ensures that no two callbacks run in parallel (Async Pump pattern).
/// </summary>
public class BucketTimerScheduler : IDisposable
{
    private readonly int _intervalMs;
    private readonly Func<long, Task> _callback;
    private readonly ILogger<BucketTimerScheduler> _logger;

    // State
    private readonly SortedSet<long> _scheduledBuckets = [];
    private readonly Timer _timer;
    private readonly object _lock = new();

    // "Async Pump" guard.
    // Modified ONLY inside _lock to ensure atomicity with timer state.
    private bool _isProcessing;
    private bool _disposed;

    public BucketTimerScheduler(int intervalMs, Func<long, Task> callback, ILogger<BucketTimerScheduler>? logger = null)
    {
        _intervalMs = intervalMs;
        _callback = callback;
        _logger = logger ?? NullLogger<BucketTimerScheduler>.Instance;
        _timer = new Timer(OnTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    public long GetCurrentBucket()
    {
        // This ensures strictly positive ceiling behavior.
        return (Environment.TickCount64 + _intervalMs - 1) / _intervalMs;
    }

    public void Schedule(long bucket)
    {
        lock (_lock)
        {
            if (_disposed) return;

            bool isNewMin = _scheduledBuckets.Add(bucket) && bucket == _scheduledBuckets.Min;

            // Only update the timer if we are NOT currently running the pump.
            // If the pump is running, it will naturally pick up this new bucket 
            // in its loop if it's due.
            if (!_isProcessing && isNewMin)
            {
                UpdateTimer_Locked();
            }
        }
    }

    private void OnTimerCallback(object? state)
    {
        lock (_lock)
        {
            if (_disposed || _isProcessing) return;
            _isProcessing = true;
        }

        // Offload the loop to a ThreadPool thread to avoid blocking the Timer thread.
        Task.Run(ProcessQueueLoopAsync);
    }

    private async Task ProcessQueueLoopAsync()
    {
        while (true)
        {
            long bucketToProcess;

            // 1. Fetch next work item
            lock (_lock)
            {
                if (_disposed) return;

                long now = GetCurrentBucket();

                // If no buckets left, or the next bucket is in the future -> Stop Pump
                if (_scheduledBuckets.Count == 0 || _scheduledBuckets.Min > now)
                {
                    _isProcessing = false;
                    UpdateTimer_Locked(); // Sleep until next bucket
                    return;
                }

                bucketToProcess = _scheduledBuckets.Min;
                _scheduledBuckets.Remove(bucketToProcess);
            }

            // 2. Execute Callback (Processing)
            try
            {
                // Await ensures we don't start the next loop until this one finishes
                await _callback(bucketToProcess);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled scheduler callback exception: bucket={Bucket}", bucketToProcess);
            }
        }
    }

    private void UpdateTimer_Locked()
    {
        if (_scheduledBuckets.Count == 0)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        long nextBucket = _scheduledBuckets.Min;
        long nowTicks = Environment.TickCount64;
        long targetTime = nextBucket * _intervalMs;
        long delay = targetTime - nowTicks;

        if (delay < 0) delay = 0;

        _timer.Change(delay, Timeout.Infinite);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
            _scheduledBuckets.Clear();
        }
    }
}

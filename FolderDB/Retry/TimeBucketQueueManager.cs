using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderDB.Retry;

/// <summary>
/// Provides the default scheduler that re-runs deferred file work, running the items that come due
/// in the same interval as a single batch.
/// </summary>
/// <remarks>
/// Time is divided into intervals as long as the first delay, and an item runs on an interval
/// boundary rather than at an exact moment. The items of one interval are processed one after another,
/// never in parallel. An item that asks for another attempt waits the delay of its next attempt,
/// rounded to the nearest whole number of intervals; after the last delay, every further attempt
/// waits the last one.
/// </remarks>
public class TimeBucketQueueManager : IRetryScheduler<string>
{
    private readonly long[] _delayIntervals;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly CancellationToken _cancellationToken;
    private readonly ConcurrentDictionary<string, QueueItem> _items;
    private readonly BucketTimerScheduler _scheduler;
    private readonly ILogger<TimeBucketQueueManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeBucketQueueManager"/> class.
    /// </summary>
    /// <param name="delays">
    /// The delays before the runs of a scheduled item. The first delay is the base interval, and the
    /// last one repeats until the item completes.
    /// </param>
    /// <param name="loggerFactory">The factory used to create the scheduler's loggers.</param>
    public TimeBucketQueueManager(
        IReadOnlyList<TimeSpan> delays,
        ILoggerFactory? loggerFactory = null)
    {
        var intervalMs = Math.Max(1, (int)delays[0].TotalMilliseconds);
        _delayIntervals = ToIntervals(delays, intervalMs);
        _lifetimeCts = new CancellationTokenSource();
        _cancellationToken = _lifetimeCts.Token;
        var effectiveLoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = effectiveLoggerFactory.CreateLogger<TimeBucketQueueManager>();

        // The scheduled values are file paths, so they are compared the way the rest of the library
        // compares them.
        _items = new ConcurrentDictionary<string, QueueItem>(PathHelper.OSDependedPathComparer);

        // We pass our processing logic as the callback
        _scheduler = new BucketTimerScheduler(
            intervalMs,
            ProcessBatchAsync,
            effectiveLoggerFactory.CreateLogger<BucketTimerScheduler>());
    }

    public void Enqueue(
        string value,
        Func<string, CancellationToken, Task<RetryDecision>> processor,
        bool minBackoff = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(processor);

        long targetBucket = _scheduler.GetCurrentBucket() + 1;

        var current = _items.AddOrUpdate(value,
            _ => new QueueItem
            {
                Value = value,
                Processor = processor,
                TargetBucket = targetBucket,
                Attempt = 0,
                MinBackoffRequested = minBackoff
            },
            (_, existing) => minBackoff
                ? new QueueItem
                {
                    Value = existing.Value,
                    Processor = existing.Processor,
                    TargetBucket = targetBucket,
                    Attempt = 0,
                    MinBackoffRequested = true
                }
                : existing);

        _scheduler.Schedule(current.TargetBucket);
    }

    /// <summary>
    /// This is the callback executed by BucketTimerScheduler.
    /// It is guaranteed to run sequentially (no parallel execution).
    /// </summary>
    private async Task ProcessBatchAsync(long bucket)
    {
        // TODO: think about parallel processing

        // Gather items for this bucket (snapshot)
        var batch = _items.Values
            .Where(x => x.TargetBucket <= bucket)
            .ToList();

        foreach (var item in batch)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _items.TryRemove(item.Value, out _);

            RetryDecision result;
            try
            {
                result = await item.Processor(item.Value, _cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogTrace("Processing canceled: item={Item}", item.Value);
                result = RetryDecision.Complete;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Processing failed: item={Item}", item.Value);
                result = RetryDecision.RetryWithBackoff;
            }

            switch (result)
            {
                case RetryDecision.RetryWithBackoff:
                    ScheduleRetry(item, resetBackoff: false);
                    break;
                case RetryDecision.RetryWithMinBackoff:
                    ScheduleRetry(item, resetBackoff: true);
                    break;
            }
        }
    }

    private void ScheduleRetry(QueueItem item, bool resetBackoff)
    {
        // The last delay repeats: an item that keeps failing stays at it.
        var attempt = resetBackoff
            ? 0
            : Math.Min(item.Attempt + 1, _delayIntervals.Length - 1);

        item.Attempt = attempt;
        item.TargetBucket = _scheduler.GetCurrentBucket() + _delayIntervals[attempt];
        item.MinBackoffRequested = false;

        // Processing took the item out of the dictionary, so a concurrent Enqueue could have put a
        // competing one back. A minimum backoff request wins; a plain one loses to the progression
        // already in flight.
        var winner = _items.AddOrUpdate(
            item.Value,
            item,
            (_, existing) => existing.MinBackoffRequested ? existing : item);

        _logger.LogDebug("Scheduling retry: item={Item} bucket={Bucket}", item.Value, winner.TargetBucket);

        // Re-schedule in the engine
        _scheduler.Schedule(winner.TargetBucket);
    }

    // A delay is rounded to the nearest whole number of intervals, a half going up. One shorter
    // than an interval is raised to it, and one longer than a day is shortened to a day.
    internal static long[] ToIntervals(IReadOnlyList<TimeSpan> delays, int intervalMs)
    {
        var intervals = new long[delays.Count];

        // The first delay is the interval itself, whatever was lost when it was cut to whole milliseconds.
        intervals[0] = 1;

        for (var i = 1; i < intervals.Length; i++)
        {
            var delayMs = Math.Min(delays[i].TotalMilliseconds, RetryDelaysHelper.MaxDelay.TotalMilliseconds);
            intervals[i] = delayMs <= intervalMs
                ? 1
                : (long)Math.Round(delayMs / intervalMs, MidpointRounding.AwayFromZero);
        }

        return intervals;
    }

    public void Dispose()
    {
        try
        {
            _lifetimeCts.Cancel();
        }
        catch
        {
            // Best effort.
        }

        _scheduler.Dispose();
        _items.Clear();
        _lifetimeCts.Dispose();
    }

    // An item is never mutated while it is in the dictionary: Enqueue replaces it and ScheduleRetry
    // only touches an item it has already taken out. That is what makes the compare and swap inside
    // AddOrUpdate meaningful, so this must stay a class with reference equality, not a record.
    private sealed class QueueItem
    {
        public required string Value { get; init; }
        public required Func<string, CancellationToken, Task<RetryDecision>> Processor { get; init; }
        public required long TargetBucket { get; set; }
        public required int Attempt { get; set; }
        public bool MinBackoffRequested { get; set; }
    }
}

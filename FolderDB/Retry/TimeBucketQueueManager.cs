using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderDB.Retry;

public class TimeBucketQueueManager : IRetryScheduler<string>
{
    private readonly int _maxRetryIntervals;
    private readonly double _backoffMultiplier;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly CancellationToken _cancellationToken;
    private readonly ConcurrentDictionary<string, QueueItem> _items;
    private readonly BucketTimerScheduler _scheduler;
    private readonly ILogger<TimeBucketQueueManager> _logger;

    public TimeBucketQueueManager(
        TimeSpan interval,
        int maxRetryIntervals = 10,
        double backoffMultiplier = 2,
        IEqualityComparer<string>? valueComparer = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.FromMilliseconds(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(interval, RetryConsts.MaxDelay);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetryIntervals);

        // ThrowIfLessThan cannot reject NaN: every comparison with it is false.
        if (double.IsNaN(backoffMultiplier))
        {
            throw new ArgumentOutOfRangeException(
                nameof(backoffMultiplier), backoffMultiplier, "The value must be a number.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(backoffMultiplier, 1);

        _maxRetryIntervals = maxRetryIntervals;
        _backoffMultiplier = backoffMultiplier;
        _lifetimeCts = new CancellationTokenSource();
        _cancellationToken = _lifetimeCts.Token;
        var effectiveLoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = effectiveLoggerFactory.CreateLogger<TimeBucketQueueManager>();

        _items = new ConcurrentDictionary<string, QueueItem>(valueComparer);

        // We pass our processing logic as the callback
        _scheduler = new BucketTimerScheduler(
            (int)interval.TotalMilliseconds,
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
                CurrentBackoff = 1,
                MinBackoffRequested = minBackoff
            },
            (_, existing) => minBackoff
                ? new QueueItem
                {
                    Value = existing.Value,
                    Processor = existing.Processor,
                    TargetBucket = targetBucket,
                    CurrentBackoff = 1,
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
        double backoff;
        if (resetBackoff)
        {
            backoff = 1;
        }
        else
        {
            backoff = item.CurrentBackoff * _backoffMultiplier;
            if (backoff > _maxRetryIntervals)
            {
                backoff = _maxRetryIntervals;
            }
        }

        // The progression is kept fractional so that a multiplier below 2 still grows it, while the
        // bucket it lands in is a whole interval, rounded away from the one already passed.
        item.CurrentBackoff = backoff;
        item.TargetBucket = _scheduler.GetCurrentBucket() + (long)Math.Ceiling(backoff);
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
        public required double CurrentBackoff { get; set; }
        public bool MinBackoffRequested { get; set; }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Retry;

namespace FolderDB.Tests;

public class TimeBucketQueueManagerTests
{
    // One bucket is short, but a backed off attempt lands 20 buckets (~1s) away. That gap is what
    // makes the assertions below distinguish "kept its backoff progression" from "moved to the front".
    private static readonly TimeSpan _interval = TimeSpan.FromMilliseconds(50);
    private const int _backoffMultiplier = 20;
    private const int _minBackoffWaitMs = 500;
    private const int _backoffQuietWaitMs = 400;

    private static TimeBucketQueueManager CreateManager() =>
        new(_interval, maxRetryIntervals: 100, _backoffMultiplier);

    [Fact]
    public async Task Enqueue_WhenPlainRequestArrivesDuringProcessing_KeepsBackoffProgression()
    {
        using var manager = CreateManager();
        var probe = new ProcessorProbe(manager, inFlightRequestMinBackoff: false);

        manager.Enqueue("alpha", probe.ProcessAsync);
        Assert.True(await probe.WaitForNextAttemptAsync());

        // The competing item asks for the next bucket, but it must lose to the in flight
        // progression, so no second attempt may happen while the backoff is still running.
        await Task.Delay(_backoffQuietWaitMs);
        Assert.Equal(1, probe.Attempts);
    }

    [Fact]
    public async Task Enqueue_WhenMinBackoffRequestArrivesDuringProcessing_RestartsProgression()
    {
        using var manager = CreateManager();
        var probe = new ProcessorProbe(manager, inFlightRequestMinBackoff: true);

        manager.Enqueue("alpha", probe.ProcessAsync);
        Assert.True(await probe.WaitForNextAttemptAsync());

        // The request landed while the item was out of the dictionary. It still has to win,
        // otherwise it is silently swallowed by the backed off item put back after processing.
        Assert.True(await probe.WaitForNextAttemptAsync(_minBackoffWaitMs));
    }

    [Fact]
    public async Task Enqueue_WhenMinBackoffRequestArrivesBetweenAttempts_RestartsProgression()
    {
        using var manager = CreateManager();
        var probe = new ProcessorProbe(manager, inFlightRequestMinBackoff: null);

        manager.Enqueue("alpha", probe.ProcessAsync);
        Assert.True(await probe.WaitForNextAttemptAsync());

        // Long enough for the backed off item to be back in the dictionary, short enough that
        // its own next attempt is still far away.
        await Task.Delay(200);
        manager.Enqueue("alpha", probe.ProcessAsync, minBackoff: true);

        Assert.True(await probe.WaitForNextAttemptAsync(_minBackoffWaitMs));
    }

    // Fails the first attempt with a backoff retry and completes on the second one. When
    // inFlightRequestMinBackoff is not null, it enqueues the same value from inside the first
    // attempt, which is exactly the window where the item is absent from the dictionary because
    // processing has already taken it.
    private sealed class ProcessorProbe(TimeBucketQueueManager manager, bool? inFlightRequestMinBackoff)
    {
        private readonly SemaphoreSlim _attemptSignal = new(0);
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<RetryDecision> ProcessAsync(string value, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _attempts);

            if (attempt == 1 && inFlightRequestMinBackoff.HasValue)
            {
                manager.Enqueue(value, ProcessAsync, inFlightRequestMinBackoff.Value);
            }

            _attemptSignal.Release();

            return Task.FromResult(attempt == 1
                ? RetryDecision.RetryWithBackoff
                : RetryDecision.Complete);
        }

        public Task<bool> WaitForNextAttemptAsync(int timeoutMs = 5000) =>
            _attemptSignal.WaitAsync(timeoutMs);
    }
}

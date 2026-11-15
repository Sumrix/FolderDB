using System;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Retry;

namespace FolderDB.Tests;

public class TimeBucketQueueManagerTests
{
    // One bucket is short, but a backed off attempt lands 20 buckets (1s) away. That gap is what
    // makes the assertions below distinguish "kept its backoff progression" from "moved to the front".
    private static readonly TimeSpan _interval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan _backoff = TimeSpan.FromSeconds(1);
    private const int _minBackoffWaitMs = 500;
    private const int _backoffQuietWaitMs = 400;

    private static TimeBucketQueueManager CreateManager() =>
        new([_interval, _backoff]);

    // Delays close to the interval stay at it instead of jumping to two, and a half goes up rather
    // than to the even neighbour.
    [Theory]
    [InlineData(110, 1)]
    [InlineData(149, 1)]
    [InlineData(150, 2)]
    [InlineData(250, 3)]
    [InlineData(300, 3)]
    [InlineData(349, 3)]
    [InlineData(350, 4)]
    public void ToIntervals_RoundsLaterDelayToNearestInterval(int delayMs, long expectedIntervals)
    {
        var intervals = TimeBucketQueueManager.ToIntervals(
            [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(delayMs)],
            intervalMs: 100);

        Assert.Equal([1, expectedIntervals], intervals);
    }

    // The options accept any positive first delay, and one cut to zero milliseconds would divide the
    // clock by zero.
    [Fact]
    public async Task Enqueue_WhenFirstDelayIsShorterThanOneMillisecond_StillRuns()
    {
        using var manager = new TimeBucketQueueManager([TimeSpan.FromTicks(1)]);
        var probe = new ProcessorProbe(manager, inFlightRequestMinBackoff: null);

        manager.Enqueue("alpha", probe.ProcessAsync);

        Assert.True(await probe.WaitForNextAttemptAsync());
    }

    // A zero delay would aim at the bucket that is already being processed; the raise to one
    // interval moves the attempt to the next one.
    [Fact]
    public async Task Enqueue_WhenLaterDelayIsShorterThanFirst_RetriesAfterOneInterval()
    {
        using var manager = new TimeBucketQueueManager([_interval, TimeSpan.Zero]);
        var probe = new ProcessorProbe(manager, inFlightRequestMinBackoff: null);

        manager.Enqueue("alpha", probe.ProcessAsync);

        Assert.True(await probe.WaitForNextAttemptAsync());
        Assert.True(await probe.WaitForNextAttemptAsync(_minBackoffWaitMs));
    }

    // The retry is one attempt past the end of the list, so it has to wait the last delay again.
    [Fact]
    public async Task Enqueue_WhenDelaysRunOut_RepeatsLastDelay()
    {
        using var manager = new TimeBucketQueueManager([_interval]);
        var probe = new ProcessorProbe(manager, inFlightRequestMinBackoff: null);

        manager.Enqueue("alpha", probe.ProcessAsync);

        Assert.True(await probe.WaitForNextAttemptAsync());
        Assert.True(await probe.WaitForNextAttemptAsync(_minBackoffWaitMs));
    }

    [Fact]
    public async Task Enqueue_WhenPlainRequestArrivesDuringProcessing_KeepsBackoffProgression()
    {
        using var manager = CreateManager();
        var probe = new ProcessorProbe(manager, inFlightRequestMinBackoff: false);

        manager.Enqueue("alpha", probe.ProcessAsync);
        Assert.True(await probe.WaitForNextAttemptAsync());

        // The competing item asks for the next bucket, but it must lose to the in flight
        // progression, so no second attempt may happen while the backoff is still running.
        await Task.Delay(_backoffQuietWaitMs, TestContext.Current.CancellationToken);
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
        await Task.Delay(200, TestContext.Current.CancellationToken);
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

        public Task<RetryDecision> ProcessAsync(string value, CancellationToken cancellationToken)
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

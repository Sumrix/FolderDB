using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Retry;

namespace FolderDB.Tests.TestSupport;

internal sealed class InlineRetryScheduler : IRetryScheduler<string>
{
    private readonly Queue<(string Value, Func<string, CancellationToken, Task<RetryDecision>> Processor)> _queue = new();
    private bool _disposed;
    public int PendingCount => _queue.Count;

    public void Enqueue(
        string value,
        Func<string, CancellationToken, Task<RetryDecision>> processor,
        bool minBackoff = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _queue.Enqueue((value, processor));
    }

    public async Task<bool> RunNextAsync(CancellationToken cancellationToken = default)
    {
        if (_queue.Count == 0)
            return false;

        var (value, processor) = _queue.Dequeue();
        var result = await processor(value, cancellationToken);
        if (result != RetryDecision.Complete)
        {
            _queue.Enqueue((value, processor));
        }

        return true;
    }

    public async Task RunAllAsync(CancellationToken cancellationToken = default)
    {
        var safety = 0;
        while (_queue.Count > 0)
        {
            await RunNextAsync(cancellationToken);
            safety++;
            if (safety > 10_000)
                throw new InvalidOperationException("InlineRetryScheduler exceeded safety iteration limit.");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _queue.Clear();
    }
}

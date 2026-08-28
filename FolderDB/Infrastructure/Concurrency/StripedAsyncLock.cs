using System;
using System.Collections.Generic;
using System.Threading;
using Nito.AsyncEx;

namespace FolderDB.Infrastructure.Concurrency;

/// <summary>
/// Striped async mutex keyed by a value.
/// </summary>
public class StripedAsyncLock<TKey>(int stripeCountPow2, IEqualityComparer<TKey>? comparer = null)
    : StripedBase<TKey, AsyncLock>(stripeCountPow2, static () => new AsyncLock(), comparer)
    where TKey : notnull
{
    /// <summary>
    /// Acquires an async mutex lock for the stripe corresponding to <paramref name="key"/>.
    /// </summary>
    /// <returns>A disposable that releases the lock when disposed.</returns>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public AwaitableDisposable<IDisposable> LockAsync(TKey key, CancellationToken cancellationToken = default)
        => GetStripe(key).LockAsync(cancellationToken);
}
using System;
using System.Collections.Generic;

namespace FolderDB.Infrastructure.Concurrency;

/// <summary>
/// Base class for striped synchronization primitives.
/// </summary>
/// <remarks>
/// Striped locking uses a fixed array of locks ("stripes") and maps each key to a stripe
/// by hash. This is cheaper than allocating a dedicated lock per key, but it means
/// different keys may occasionally collide to the same stripe (reduced concurrency).
/// </remarks>
public abstract class StripedBase<TKey, TStripe>
    where TKey : notnull
    where TStripe : class
{
    private readonly TStripe[] _stripes;
    private readonly IEqualityComparer<TKey> _comparer;
    private readonly int _stripeMask;

    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="stripeCountPow2"/> is less than or equal to zero.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="stripeCountPow2"/> is not a power of two.
    /// </exception>
    protected StripedBase(
        int stripeCountPow2,
        Func<TStripe> stripeFactory,
        IEqualityComparer<TKey>? comparer = null)
    {
        if (stripeCountPow2 <= 0)
            throw new ArgumentOutOfRangeException(nameof(stripeCountPow2));

        // We require power-of-two stripe count to map hash -> stripe via bitmask.
        if ((stripeCountPow2 & (stripeCountPow2 - 1)) != 0)
            throw new ArgumentException("stripeCountPow2 must be a power of two.", nameof(stripeCountPow2));

        _stripeMask = stripeCountPow2 - 1;
        _comparer = comparer ?? EqualityComparer<TKey>.Default;

        _stripes = new TStripe[stripeCountPow2];
        for (int i = 0; i < _stripes.Length; i++)
            _stripes[i] = stripeFactory();
    }

    /// <summary>
    /// Maps the provided key to a stripe (lock) using the configured comparer hash code.
    /// Collisions are expected and acceptable.
    /// </summary>
    protected TStripe GetStripe(TKey key)
        => _stripes[_comparer.GetHashCode(key) & _stripeMask];
}
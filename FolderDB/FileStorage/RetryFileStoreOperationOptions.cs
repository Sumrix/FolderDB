using System;
using FolderDB.Retry;

namespace FolderDB.FileStorage;

public sealed class RetryFileStoreOperationOptions
{
    private int _maxAttempts = 1;
    private TimeSpan _delay = TimeSpan.Zero;
    private double _backoffMultiplier = 1;

    public int MaxAttempts
    {
        get => _maxAttempts;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxAttempts = value;
        }
    }

    public TimeSpan Delay
    {
        get => _delay;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, RetryConsts.MaxDelay);
            _delay = value;
        }
    }

    public double BackoffMultiplier
    {
        get => _backoffMultiplier;
        set
        {
            // ThrowIfLessThan cannot reject NaN: every comparison with it is false.
            if (double.IsNaN(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The value must be a number.");

            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _backoffMultiplier = value;
        }
    }

    internal static RetryFileStoreOperationOptions CreateReadDefaults()
    {
        return new RetryFileStoreOperationOptions
        {
            MaxAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(10)
        };
    }

    internal static RetryFileStoreOperationOptions CreateWriteDefaults()
    {
        return new RetryFileStoreOperationOptions
        {
            MaxAttempts = 5,
            Delay = TimeSpan.FromMilliseconds(25),
            BackoffMultiplier = 2
        };
    }
}

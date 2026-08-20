using System;
using FolderDB.Retry;

namespace FolderDB;

public sealed class DefaultRetrySchedulerOptions
{
    private TimeSpan _interval = TimeSpan.FromMilliseconds(100);
    private int _maxRetryIntervals = 10;
    private double _backoffMultiplier = 2;

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromMilliseconds(1));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, RetryConsts.MaxDelay);
            _interval = value;
        }
    }

    public int MaxRetryIntervals
    {
        get => _maxRetryIntervals;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxRetryIntervals = value;
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
}

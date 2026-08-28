using System;
using FolderDB.Retry;

namespace FolderDB;

/// <summary>
/// Provides options to be used with <see cref="FolderDatabase.CreateDefaultRetryScheduler"/>.
/// </summary>
public sealed class DefaultRetrySchedulerOptions
{
    private TimeSpan _interval = TimeSpan.FromMilliseconds(100);
    private int _maxRetryIntervals = 10;
    private double _backoffMultiplier = 2;

    /// <summary>
    /// Gets or sets the unit the retry schedule is measured in.
    /// </summary>
    /// <returns>The length of one interval. The default is 100 milliseconds.</returns>
    /// <remarks>
    /// Time is divided into intervals of this length, and a retry runs on an interval boundary
    /// rather than at an exact moment. The base delay is one interval: the first retry waits at
    /// least one and at most two, depending on where the boundary falls, and the delays below are
    /// counted in the same unit.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is less than one millisecond.
    ///
    /// -or-
    ///
    /// The value is greater than one day.
    /// </exception>
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

    /// <summary>
    /// Gets or sets the longest delay a retry backs off to, counted in <see cref="Interval"/> units.
    /// </summary>
    /// <returns>
    /// The longest delay, in intervals. The default is 10, which is one second at the default interval.
    /// </returns>
    /// <remarks>The delay stays at this cap for every further attempt.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxRetryIntervals
    {
        get => _maxRetryIntervals;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxRetryIntervals = value;
        }
    }

    /// <summary>
    /// Gets or sets the factor the delay of a failing item is multiplied by after each attempt.
    /// </summary>
    /// <returns>
    /// The backoff factor. The default is 2. A value of 1 indicates that every attempt is retried
    /// at the same delay.
    /// </returns>
    /// <remarks>
    /// The delay starts at one <see cref="Interval"/> and grows from there. A delay that lands
    /// between two intervals is rounded up to the next one.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is less than 1.
    ///
    /// -or-
    ///
    /// The value is <see cref="double.NaN"/>.
    /// </exception>
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

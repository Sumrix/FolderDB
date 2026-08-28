using System;
using FolderDB.Retry;

namespace FolderDB.FileStorage;

/// <summary>
/// Provides retry options to be used with <see cref="RetryFileStoreOptions.Read"/>,
/// <see cref="RetryFileStoreOptions.Write"/>, and <see cref="RetryFileStoreOptions.Delete"/>.
/// </summary>
public sealed class RetryFileStoreOperationOptions
{
    private int _maxAttempts = 1;
    private TimeSpan _delay = TimeSpan.Zero;
    private double _backoffMultiplier = 1;

    /// <summary>
    /// Gets or sets the number of times the operation is attempted, including the first attempt.
    /// </summary>
    /// <returns>
    /// The number of attempts. The default is 1, which indicates that the operation is not retried.
    /// </returns>
    /// <remarks>Only failures classified as transient are retried.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxAttempts
    {
        get => _maxAttempts;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxAttempts = value;
        }
    }

    /// <summary>
    /// Gets or sets the base delay between attempts.
    /// </summary>
    /// <returns>
    /// The base delay between attempts. The default is <see cref="TimeSpan.Zero"/>, which indicates
    /// that the operation is retried immediately.
    /// </returns>
    /// <remarks>
    /// The first retry waits this long, and every further one multiplies it by
    /// <see cref="BackoffMultiplier"/>, but no single delay exceeds one day.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative.
    ///
    /// -or-
    ///
    /// The value is greater than one day.
    /// </exception>
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

    /// <summary>
    /// Gets or sets the factor <see cref="Delay"/> is multiplied by after each attempt.
    /// </summary>
    /// <returns>
    /// The backoff factor. The default is 1, which indicates that every attempt is retried at the
    /// same delay.
    /// </returns>
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

    /// <summary>
    /// Creates the retry settings that reads use by default.
    /// </summary>
    /// <returns>A new instance configured for reads.</returns>
    public static RetryFileStoreOperationOptions CreateReadDefaults()
    {
        return new RetryFileStoreOperationOptions
        {
            MaxAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(10)
        };
    }

    /// <summary>
    /// Creates the retry settings that writes and deletes use by default.
    /// </summary>
    /// <returns>A new instance configured for writes and deletes.</returns>
    public static RetryFileStoreOperationOptions CreateWriteDefaults()
    {
        return new RetryFileStoreOperationOptions
        {
            MaxAttempts = 5,
            Delay = TimeSpan.FromMilliseconds(25),
            BackoffMultiplier = 2
        };
    }
}

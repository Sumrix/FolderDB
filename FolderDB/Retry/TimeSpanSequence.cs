using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderDB.Retry;

/// <summary>
/// Generates sequences of <see cref="TimeSpan"/> values.
/// </summary>
public static class TimeSpanSequence
{
    /// <summary>
    /// Generates a sequence that contains one repeated value.
    /// </summary>
    /// <param name="value">The value to repeat.</param>
    /// <param name="count">The number of values in the sequence.</param>
    /// <returns>A sequence of <paramref name="count"/> equal values.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static IEnumerable<TimeSpan> Constant(TimeSpan value, int count)
    {
        return Enumerable.Repeat(value, count);
    }

    /// <summary>
    /// Generates a sequence of <paramref name="count"/> values that starts at <paramref name="start"/>
    /// and is multiplied by <paramref name="multiplier"/> at each step, capped at <paramref name="max"/>
    /// when it is set.
    /// </summary>
    /// <param name="start">The first value of the sequence.</param>
    /// <param name="count">The number of values in the sequence.</param>
    /// <param name="multiplier">The factor each value is multiplied by to get the next one.</param>
    /// <param name="max">The largest value; a larger one is replaced by it.</param>
    /// <returns>A sequence of <paramref name="count"/> values.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> or <paramref name="multiplier"/> is negative.
    ///
    /// -or-
    ///
    /// The last value is not a number, is less than <see cref="TimeSpan.MinValue"/>, or is greater
    /// than <see cref="TimeSpan.MaxValue"/> while <paramref name="max"/> is <see langword="null"/>.
    /// </exception>
    public static IEnumerable<TimeSpan> Exponential(
        TimeSpan start,
        int count,
        double multiplier = 2,
        TimeSpan? max = null)
    {
        // With a negative multiplier the signs alternate, and the value before a capped last one
        // could overflow unchecked.
        ArgumentOutOfRangeException.ThrowIfNegative(multiplier);

        if (start == TimeSpan.Zero)
        {
            return Enumerable.Repeat(TimeSpan.Zero, count);
        }

        if (count > 0)
        {
            var last = start.TotalMilliseconds * Math.Pow(multiplier, count - 1);
            if ((max is null && last > TimeSpan.MaxValue.TotalMilliseconds) ||
                last < TimeSpan.MinValue.TotalMilliseconds ||
                double.IsNaN(last))
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "The last value exceeds TimeSpan limits; set max or reduce count.");
            }
        }

        var values = Enumerable.Range(0, count)
            .Select(i => start.TotalMilliseconds * Math.Pow(multiplier, i));

        return max is not null
            ? values
                .Select(ms => ms >= max.Value.TotalMilliseconds ? max.Value : TimeSpan.FromMilliseconds(ms))
            : values
                .Select(TimeSpan.FromMilliseconds);
    }
}

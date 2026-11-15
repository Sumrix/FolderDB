using System;
using System.Collections.Generic;

namespace FolderDB.Retry;

public static class RetryDelaysHelper
{
    // The limit is arbitrary, but we follow Polly's convention of using 1 day.
    public static readonly TimeSpan MaxDelay = TimeSpan.FromDays(1);

    public static IReadOnlyList<TimeSpan> CopyChecked(IReadOnlyList<TimeSpan> value, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value);

        IReadOnlyList<TimeSpan> delays = [.. value];
        if (!allowEmpty && delays.Count == 0)
        {
            throw new ArgumentException("The list of delays must not be empty.", nameof(value));
        }

        foreach (var delay in delays)
        {
            if (delay <= TimeSpan.Zero || delay > RetryDelaysHelper.MaxDelay)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    delay,
                    "A delay must be greater than zero and not greater than one day.");
            }
        }

        return delays;
    }
}

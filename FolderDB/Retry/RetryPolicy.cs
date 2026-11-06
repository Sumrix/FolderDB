using System;
using System.Collections.Generic;

namespace FolderDB.Retry;

/// <summary>
/// Provides predefined retry delays for <see cref="StoreOptions.Retry"/>.
/// </summary>
public static class RetryPolicy
{
    /// <summary>
    /// Returns the retry delays FolderDB uses for a file operation by default.
    /// </summary>
    /// <param name="operation">The file operation to return the delays for.</param>
    /// <returns>
    /// Delays that retry a read once after 10 milliseconds, and a write or a delete up to 4 times,
    /// starting at 25 milliseconds and doubling the delay.
    /// </returns>
    public static IEnumerable<TimeSpan> Default(FileOperation operation)
    {
        return operation == FileOperation.Read
            ? TimeSpanSequence.Constant(TimeSpan.FromMilliseconds(10), 1)
            : TimeSpanSequence.Exponential(TimeSpan.FromMilliseconds(25), 4);
    }
}

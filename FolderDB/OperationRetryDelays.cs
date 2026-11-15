using System;
using System.Collections.Generic;
using FolderDB.Retry;

namespace FolderDB;

/// <summary>
/// Provides the retry delay options for store operations.
/// </summary>
public sealed class OperationRetryDelays
{
    private static readonly IReadOnlyList<TimeSpan> _defaultRead = [
        TimeSpan.FromMilliseconds(10)
    ];
    private static readonly IReadOnlyList<TimeSpan> _defaultWrite = [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200)
    ];
    private IReadOnlyList<TimeSpan> _read = _defaultRead;
    private IReadOnlyList<TimeSpan> _write = _defaultWrite;

    /// <summary>
    /// Gets or sets the delay before each retry of reading a file.
    /// </summary>
    /// <remarks>
    /// Only a transient failure is retried. The delays must be greater than zero and no greater
    /// than one day.
    /// </remarks>
    /// <returns>The delay before each retry. The default is 10 milliseconds.</returns>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A delay is zero or negative or greater than one day.
    /// </exception>
    public IReadOnlyList<TimeSpan> Read
    {
        get => _read;
        set => _read = RetryDelaysHelper.CopyChecked(value, allowEmpty: true);
    }

    /// <summary>
    /// Gets or sets the delay before each retry of writing or deleting a file.
    /// </summary>
    /// <remarks>
    /// Only a transient failure is retried. The delays must be greater than zero and no greater
    /// than one day.
    /// </remarks>
    /// <returns>
    /// The delay before each retry. The default is 25, 50, 100 and 200 milliseconds.
    /// </returns>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A delay is zero or negative or greater than one day.
    /// </exception>
    public IReadOnlyList<TimeSpan> Write
    {
        get => _write;
        set => _write = RetryDelaysHelper.CopyChecked(value, allowEmpty: true);
    }
}

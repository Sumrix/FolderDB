using System;
using System.Collections.Generic;
using FolderDB.FileStorage;
using FolderDB.Retry;
using Microsoft.Extensions.Logging;

namespace FolderDB;

/// <summary>
/// Provides options to be used with <see cref="FolderStore"/>.
/// </summary>
public sealed class StoreOptions
{
    private TimeSpan _indexAutoSaveInterval = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyList<TimeSpan> _defaultReconciliationDelays = [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromSeconds(1)
    ];
    private IReadOnlyList<TimeSpan> _reconciliationRetryDelays = _defaultReconciliationDelays;

    /// <summary>
    /// Gets or sets the factory used to create loggers for the store.
    /// </summary>
    /// <returns>
    /// The logger factory. The default is <see langword="null"/>, which indicates that nothing is
    /// logged.
    /// </returns>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Gets or sets the time a change to a collection's in-memory index waits before the index
    /// file is written.
    /// </summary>
    /// <remarks>
    /// The timer starts when the index changes and is not restarted by further changes while it is
    /// already running, so a burst of changes costs one write rather than one per change. An index
    /// that does not change is never written.
    /// </remarks>
    /// <returns>The time a change waits before it is written. The default is 10 seconds.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is less than or equal to zero.
    /// </exception>
    public TimeSpan IndexAutoSaveInterval
    {
        get => _indexAutoSaveInterval;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _indexAutoSaveInterval = value;
        }
    }

    /// <summary>
    /// Gets or sets the file store used to access files.
    /// </summary>
    /// <remarks>
    /// Set this property to customize low-level file access behavior, including how file access
    /// errors are classified as transient or permanent.
    /// </remarks>
    /// <returns>
    /// The file store. The default is <see langword="null"/>, which indicates that
    /// <see cref="FileStorage.FileStore"/> is used.
    /// </returns>
    public IFileStore? FileStore { get; set; }

    /// <summary>
    /// Gets the retry delays for store operations.
    /// </summary>
    /// <returns>
    /// The retry delays of reads and of writes.
    /// </returns>
    public OperationRetryDelays OperationRetryDelays { get; } = new();

    /// <summary>
    /// Gets or sets the retry delays for index reconciliation.
    /// </summary>
    /// <remarks>
    /// The first delay is the base interval; each later delay is rounded to the nearest
    /// multiple of it. The last delay repeats until reconciliation succeeds. The delays
    /// must be greater than zero and no greater than one day.
    /// </remarks>
    /// <returns>
    /// The delay before each attempt. The default is 100, 200, 400, 800 and 1000 milliseconds.
    /// </returns>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A delay is zero or negative or greater than one day.
    /// </exception>
    public IReadOnlyList<TimeSpan> ReconciliationRetryDelays
    {
        get => _reconciliationRetryDelays;
        set => _reconciliationRetryDelays = RetryDelaysHelper.CopyChecked(value, allowEmpty: false);
    }
}

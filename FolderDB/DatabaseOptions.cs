using System;
using FolderDB.FileStorage;
using FolderDB.Retry;
using Microsoft.Extensions.Logging;

namespace FolderDB;

/// <summary>
/// Provides options to be used with <see cref="FolderDatabase"/>.
/// </summary>
public sealed class DatabaseOptions
{
    private int _maxFileNameReserveAttempts = 5;
    private TimeSpan _indexAutoSaveInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the factory used to create loggers for the database.
    /// </summary>
    /// <returns>
    /// The logger factory. The default is <see langword="null"/>, which indicates that nothing is
    /// logged.
    /// </returns>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of file names an upsert tries before it fails to store a record.
    /// </summary>
    /// <returns>The maximum number of file names. The default is 5.</returns>
    /// <remarks>
    /// Each attempt takes the next candidate name from
    /// <see cref="TableOptions{TKey, TRecord}.FileNameGenerator"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxFileNameReserveAttempts
    {
        get => _maxFileNameReserveAttempts;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxFileNameReserveAttempts = value;
        }
    }

    /// <summary>
    /// Gets or sets the time a change to a table's in-memory index waits before the index file is written.
    /// </summary>
    /// <returns>The time a change waits before it is written. The default is 10 seconds.</returns>
    /// <remarks>
    /// The timer starts when the index changes and is not restarted by further changes while it is
    /// already running, so a burst of changes costs one write rather than one per change. An index
    /// that does not change is never written.
    /// </remarks>
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
    /// Gets or sets the factory used to create the file store of the database.
    /// </summary>
    /// <returns>
    /// The file store factory. The default is <see langword="null"/>, which indicates that
    /// <see cref="FileStore"/> is used.
    /// </returns>
    /// <remarks>
    /// Set this property to customize low-level file access behavior, including how file access
    /// errors are classified as transient or permanent.
    /// </remarks>
    public Func<IFileStore>? FileStoreFactory { get; set; }

    /// <summary>
    /// Gets or sets the factory used to wrap the file store in the retry behavior of record operations.
    /// </summary>
    /// <returns>
    /// The retry factory. The default is <see langword="null"/>, which indicates that
    /// <see cref="RetryFileStore"/> is used.
    /// </returns>
    public Func<FileStoreRetryContext, IFileStore>? FileStoreRetryFactory { get; set; }

    /// <summary>
    /// Gets or sets the factory used to create the scheduler that re-runs deferred file work.
    /// </summary>
    /// <returns>
    /// The scheduler factory. The default is <see langword="null"/>, which indicates that
    /// <see cref="TimeBucketQueueManager"/> is used.
    /// </returns>
    /// <remarks>
    /// The scheduler is keyed by file path, so a replacement has to compare its keys the way the
    /// host operating system compares paths.
    /// </remarks>
    public Func<ILoggerFactory, IRetryScheduler<string>>? RetrySchedulerFactory { get; set; }
}

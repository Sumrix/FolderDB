using System;
using FolderDB.FileStorage;
using FolderDB.Retry;
using Microsoft.Extensions.Logging;

namespace FolderDB;

public sealed class DatabaseOptions
{
    private int _maxFileNameReserveAttempts = 5;
    private TimeSpan _indexAutoSaveInterval = TimeSpan.FromSeconds(10);

    public ILoggerFactory? LoggerFactory { get; set; }

    public int MaxFileNameReserveAttempts
    {
        get => _maxFileNameReserveAttempts;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxFileNameReserveAttempts = value;
        }
    }

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
    /// Creates the file store used by FolderDB. Override this to change low-level file access behavior,
    /// including how file access errors are classified as transient or permanent.
    /// </summary>
    public Func<IFileStore>? FileStoreFactory { get; set; }

    public Func<FileStoreRetryContext, IFileStore>? FileStoreRetryFactory { get; set; }

    public Func<ILoggerFactory, IRetryScheduler<string>>? RetrySchedulerFactory { get; set; }
}

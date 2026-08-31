using System;
using FolderDB.FileStorage;

namespace FolderDB;

/// <summary>
/// Provides options to be used with <see cref="RetryFileStore"/>.
/// </summary>
/// <remarks>
/// The settings are read on every operation, so changing them affects a store that is already
/// running.
/// </remarks>
public sealed class RetryFileStoreOptions
{
    internal static readonly RetryFileStoreOptions Default = new();

    private RetryFileStoreOperationOptions _read = RetryFileStoreOperationOptions.CreateReadDefaults();
    private RetryFileStoreOperationOptions _write = RetryFileStoreOperationOptions.CreateWriteDefaults();
    private RetryFileStoreOperationOptions _delete = RetryFileStoreOperationOptions.CreateWriteDefaults();

    /// <summary>
    /// Gets or sets the retry settings of read operations.
    /// </summary>
    /// <returns>
    /// The retry settings of read operations. The default is the value returned by
    /// <see cref="RetryFileStoreOperationOptions.CreateReadDefaults"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public RetryFileStoreOperationOptions Read
    {
        get => _read;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _read = value;
        }
    }

    /// <summary>
    /// Gets or sets the retry settings of write operations.
    /// </summary>
    /// <returns>
    /// The retry settings of write operations. The default is the value returned by
    /// <see cref="RetryFileStoreOperationOptions.CreateWriteDefaults"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public RetryFileStoreOperationOptions Write
    {
        get => _write;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _write = value;
        }
    }

    /// <summary>
    /// Gets or sets the retry settings of delete operations.
    /// </summary>
    /// <returns>
    /// The retry settings of delete operations. The default is the value returned by
    /// <see cref="RetryFileStoreOperationOptions.CreateWriteDefaults"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public RetryFileStoreOperationOptions Delete
    {
        get => _delete;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _delete = value;
        }
    }
}

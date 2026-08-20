using System;
using FolderDB.FileStorage;

namespace FolderDB;

public sealed class RetryFileStoreOptions
{
    internal static readonly RetryFileStoreOptions Default = new();

    private RetryFileStoreOperationOptions _read = RetryFileStoreOperationOptions.CreateReadDefaults();
    private RetryFileStoreOperationOptions _write = RetryFileStoreOperationOptions.CreateWriteDefaults();
    private RetryFileStoreOperationOptions _delete = RetryFileStoreOperationOptions.CreateWriteDefaults();

    public RetryFileStoreOperationOptions Read
    {
        get => _read;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _read = value;
        }
    }

    public RetryFileStoreOperationOptions Write
    {
        get => _write;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _write = value;
        }
    }

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

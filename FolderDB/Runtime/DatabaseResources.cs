using System;
using FolderDB.FileStorage;
using FolderDB.Infrastructure.Helpers;
using FolderDB.Retry;

namespace FolderDB.Runtime;

internal sealed class DatabaseResources(IFileStore fileStore, IRetryScheduler<string> retryScheduler) : IDisposable
{
    public IFileStore FileStore { get; } = fileStore;
    public IRetryScheduler<string> RetryScheduler { get; } = retryScheduler;

    public void Dispose()
    {
        DisposeHelper.SafeDispose(RetryScheduler);
        DisposeHelper.SafeDispose(FileStore as IDisposable);
    }
}

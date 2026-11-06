using System;
using System.Collections.Generic;
using FolderDB.FileStorage;
using FolderDB.Infrastructure.Helpers;
using FolderDB.Retry;

namespace FolderDB.Runtime;

internal sealed class StoreResources(
    IFileStore fileStore,
    IRetryScheduler<string> retryScheduler,
    Dictionary<FileOperation, IEnumerable<TimeSpan>?>? retry) : IDisposable
{
    public IFileStore FileStore { get; } = fileStore;
    public IRetryScheduler<string> RetryScheduler { get; } = retryScheduler;
    public Dictionary<FileOperation, IEnumerable<TimeSpan>?>? Retry { get; } = retry;

    public void Dispose()
    {
        DisposeHelper.SafeDispose(RetryScheduler);
        DisposeHelper.SafeDispose(FileStore as IDisposable);
    }
}

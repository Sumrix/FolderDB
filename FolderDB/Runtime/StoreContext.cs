using System;
using System.Collections.Generic;
using FolderDB.FileStorage;
using FolderDB.Infrastructure.Helpers;
using FolderDB.Retry;
using Microsoft.Extensions.Logging;

namespace FolderDB.Runtime;

public sealed class StoreContext : IDisposable
{
    public required ILoggerFactory LoggerFactory { get; init; }
    public required IFileStore FileStore { get; init; }
    public required IRetryScheduler<string> RetryScheduler { get; init; }
    public required IReadOnlyList<TimeSpan> ReadRetryDelays { get; init; }
    public required IReadOnlyList<TimeSpan> WriteRetryDelays { get; init; }
    public required TimeSpan IndexAutoSaveInterval { get; init; }

    public void Dispose()
    {
        DisposeHelper.SafeDispose(RetryScheduler);
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.FileStorage;
using FolderDB.Retry;
using FolderDB.Runtime;
using Microsoft.Extensions.Logging;

namespace FolderDB;

public interface ITableDefinition
{
    string Name { get; }
    Type RecordType { get; }
    
    Task<ITableEngine> StartEngineAsync(
        string tablePath,
        string indexFilePath,
        IFileStore fileStore,
        IRetryScheduler<string> retryScheduler,
        DatabaseOptions options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default);
}

using System.Threading;
using FolderDB.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderDB.Building;

public sealed class RecordScopedIndexEngineContext<TKey, TRecord, TProjection>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    public required TableContext<TKey, TRecord, TProjection> Table { get; init; }
    public required DatabaseOptions DatabaseOptions { get; init; }
    public required string IndexFilePath { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    public ILoggerFactory LoggerFactory => DatabaseOptions.LoggerFactory ?? NullLoggerFactory.Instance;
}

using System.Threading;
using FolderDB.Runtime;
using Microsoft.Extensions.Logging;

namespace FolderDB.Building;

public sealed class RecordScopedIndexEngineContext<TKey, TRecord, TProjection>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    public required CollectionContext<TKey, TRecord, TProjection> Collection { get; init; }
    public required StoreContext Store { get; init; }
    public required string IndexFilePath { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    public ILoggerFactory LoggerFactory => Store.LoggerFactory;
}

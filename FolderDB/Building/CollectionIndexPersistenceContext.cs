using FolderDB.Runtime;
using Microsoft.Extensions.Logging;

namespace FolderDB.Building;

public sealed class CollectionIndexPersistenceContext<TKey, TRecord, TProjection>(
    CollectionContext<TKey, TRecord, TProjection> collection,
    ILoggerFactory loggerFactory)
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    public CollectionContext<TKey, TRecord, TProjection> Collection { get; } = collection;
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;
}

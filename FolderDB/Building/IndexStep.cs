using System;
using System.Threading.Tasks;
using FolderDB.Indexing;
using FolderDB.Indexing.Persistence;

namespace FolderDB.Building;

internal record IndexStep<TKey, TRecord, TProjection>
(
    ProjectionStep<TKey, TRecord, TProjection> Previous,
    Func<CollectionIndexPersistenceContext<TKey, TRecord, TProjection>, ICollectionIndexPersistence<TKey, TProjection>>? IndexPersistenceFactory,
    Func<RecordScopedIndexEngineContext<TKey, TRecord, TProjection>, Task<IRecordScopedIndexEngine<TKey, TRecord, TProjection>>>? IndexEngineFactory
)
    where TRecord : class, IRecord<TKey>
    where TKey : notnull;

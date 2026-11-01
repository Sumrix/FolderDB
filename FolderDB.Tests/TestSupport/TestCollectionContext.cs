using System;
using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;
using FolderDB.Encoding;
using FolderDB.Indexing.Persistence;
using FolderDB.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderDB.Tests.TestSupport;

internal static class TestCollectionContext
{
    public static CollectionContext<TKey, TRecord, TProjection> Create<TKey, TRecord, TProjection>(
        Func<TRecord, TProjection> createProjection,
        Func<TRecord, IEnumerable<string>> generateFileNames,
        IEqualityComparer<TKey> keyEqualityComparer,
        IComparer<TKey> keyComparer,
        IRecordCodec<TKey, TRecord> codec)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return new CollectionContext<TKey, TRecord, TProjection>
        {
            Name = typeof(TRecord).Name,
            KeyComparer = keyComparer,
            KeyEqualityComparer = keyEqualityComparer,
            FileNameGenerator = generateFileNames,
            CreateProjection = createProjection,
            RecordCodec = codec,
        };
    }

    public static ICollectionIndexPersistence<TKey, TProjection> CreateJsonIndexPersistence<TKey, TProjection>(
        JsonTypeInfo<TKey> keyTypeInfo,
        JsonTypeInfo<TProjection> projectionTypeInfo,
        IEqualityComparer<TKey> keyEqualityComparer)
        where TKey : notnull
    {
        return new JsonCollectionIndexPersistence<TKey, TProjection>(
            keyTypeInfo,
            projectionTypeInfo,
            keyEqualityComparer,
            NullLoggerFactory.Instance.CreateLogger<JsonCollectionIndexPersistence<TKey, TProjection>>());
    }
}

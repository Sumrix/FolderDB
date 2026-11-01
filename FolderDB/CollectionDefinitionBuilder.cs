using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using FolderDB.Building;

namespace FolderDB;

public static class CollectionDefinitionBuilder
{
    public static RecordCodecBuilder<TKey, TRecord> Create<TKey, TRecord>()
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return new RecordCodecBuilder<TKey, TRecord>(
            new CollectionOptions<TKey, TRecord>());
    }

    [RequiresUnreferencedCode("Uses reflection-based resolver via populateMissingResolver.")]
    [RequiresDynamicCode("May require runtime code generation for reflection-based serialization.")]
    public static CollectionDefinition<TKey, TRecord, NoProjection> CreateDefault<TKey, TRecord>(
        JsonSerializerOptions? jsonOptions = null,
        CollectionOptions<TKey, TRecord>? collectionOptions = null)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return CreateDefault<TKey, TRecord, NoProjection>(
            static _ => default,
            jsonOptions,
            collectionOptions);
    }

    [RequiresUnreferencedCode("Uses reflection-based resolver via populateMissingResolver.")]
    [RequiresDynamicCode("May require runtime code generation for reflection-based serialization.")]
    public static CollectionDefinition<TKey, TRecord, TProjection> CreateDefault<TKey, TRecord, TProjection>(
        Func<TRecord, TProjection> createProjection,
        JsonSerializerOptions? jsonOptions = null,
        CollectionOptions<TKey, TRecord>? collectionOptions = null)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(createProjection);

        collectionOptions ??= new CollectionOptions<TKey, TRecord>();
        return new RecordCodecBuilder<TKey, TRecord>(collectionOptions)
            .UseJsonRecordCodec(jsonOptions)
            .WithProjection(createProjection)
            .UseJsonIndexPersistence(jsonOptions)
            .Build();
    }
}

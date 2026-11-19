using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;
using FolderDB.Encoding;
using FolderDB.Indexing.Persistence;

namespace FolderDB;

/// <summary>
/// Defines a collection to be opened with <see cref="FolderStore"/>.
/// </summary>
/// <typeparam name="TKey">The type of the record id.</typeparam>
/// <typeparam name="TRecord">The record type stored in the collection.</typeparam>
public class CollectionDefinition<TKey, TRecord> : CollectionDefinition<TKey, TRecord, NoProjection>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    [RequiresUnreferencedCode("Uses reflection-based resolver via populateMissingResolver.")]
    [RequiresDynamicCode("May require runtime code generation for reflection-based serialization.")]
    [SetsRequiredMembers]
    public CollectionDefinition()
        : base(
            new RecordCodec<TKey, TRecord>(new RecordSchemaBuilder().WithoutVersioning<TRecord>()),
            (JsonTypeInfo<TKey>)IndexJsonOptions.Reflection.GetTypeInfo(typeof(TKey)),
            IndexDtoJsonContext.Default.NoProjection)
    {
        Projection = static _ => default;
    }

    [SetsRequiredMembers]
    public CollectionDefinition(IRecordCodec<TKey, TRecord> codec, JsonTypeInfo<TKey> keyTypeInfo)
        : base(codec, keyTypeInfo, IndexDtoJsonContext.Default.NoProjection)
    {
        Projection = static _ => default;
    }
}

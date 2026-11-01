using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using IndexDtoJsonContext = FolderDB.Indexing.Persistence.IndexDtoJsonContext;

namespace FolderDB.Building;

public sealed class IndexBuilderNoProjection<TKey, TRecord>
    : IndexBuilder<TKey, TRecord, NoProjection, IndexBuilderNoProjection<TKey, TRecord>>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    protected override IndexBuilderNoProjection<TKey, TRecord> This => this;

    internal IndexBuilderNoProjection(
        ProjectionStep<TKey, TRecord, NoProjection> step,
        CollectionOptions<TKey, TRecord> options)
        : base(step, options)
    {
    }

    public FinalBuilder<TKey, TRecord, NoProjection> UseJsonIndexPersistence(JsonTypeInfo<TKey> keyTypeInfo)
    {
        return UseJsonIndexPersistenceCore(keyTypeInfo, IndexDtoJsonContext.Default.NoProjection);
    }

    [RequiresUnreferencedCode("Uses reflection-based resolver via populateMissingResolver.")]
    [RequiresDynamicCode("May require runtime code generation for reflection-based serialization.")]
    public FinalBuilder<TKey, TRecord, NoProjection> UseJsonIndexPersistence(JsonSerializerOptions? options = null)
    {
        options ??= JsonSerializerOptions.Default;
        options.MakeReadOnly(populateMissingResolver: true);
        var keyTypeInfo = (JsonTypeInfo<TKey>)options.GetTypeInfo(typeof(TKey));

        return UseJsonIndexPersistence(keyTypeInfo);
    }
}

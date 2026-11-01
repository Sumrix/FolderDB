using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FolderDB.Building;

public sealed class IndexBuilderWithProjection<TKey, TRecord, TProjection>
    : IndexBuilder<TKey, TRecord, TProjection, IndexBuilderWithProjection<TKey, TRecord, TProjection>>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    protected override IndexBuilderWithProjection<TKey, TRecord, TProjection> This => this;

    internal IndexBuilderWithProjection(
        ProjectionStep<TKey, TRecord, TProjection> step,
        CollectionOptions<TKey, TRecord> options)
        : base(step, options)
    {
    }

    public FinalBuilder<TKey, TRecord, TProjection> UseJsonIndexPersistence(
        JsonTypeInfo<TKey> keyTypeInfo,
        JsonTypeInfo<TProjection> projectionTypeInfo)
    {
        return UseJsonIndexPersistenceCore(keyTypeInfo, projectionTypeInfo);
    }

    [RequiresUnreferencedCode("Uses reflection-based resolver via populateMissingResolver.")]
    [RequiresDynamicCode("May require runtime code generation for reflection-based serialization.")]
    public FinalBuilder<TKey, TRecord, TProjection> UseJsonIndexPersistence(JsonSerializerOptions? options = null)
    {
        options ??= JsonSerializerOptions.Default;
        options.MakeReadOnly(populateMissingResolver: true);
        var keyTypeInfo = (JsonTypeInfo<TKey>)options.GetTypeInfo(typeof(TKey));
        var projectionTypeInfo = (JsonTypeInfo<TProjection>)options.GetTypeInfo(typeof(TProjection));

        return UseJsonIndexPersistence(keyTypeInfo, projectionTypeInfo);
    }
}

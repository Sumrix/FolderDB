using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FolderDB.Encoding;

namespace FolderDB.Building;

public sealed class RecordCodecBuilder<TKey, TRecord>
    : CollectionOptionsBuilder<TKey, TRecord, RecordCodecBuilder<TKey, TRecord>>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    protected override RecordCodecBuilder<TKey, TRecord> This => this;

    internal RecordCodecBuilder(CollectionOptions<TKey, TRecord> options)
        : base(options)
    {
    }

    public ProjectionBuilder<TKey, TRecord> UseRecordCodec(IRecordCodec<TKey, TRecord> recordCodec)
    {
        ArgumentNullException.ThrowIfNull(recordCodec);
        return UseRecordCodec(_ => recordCodec);
    }

    public ProjectionBuilder<TKey, TRecord> UseRecordCodec(
        Func<RecordCodecContext, IRecordCodec<TKey, TRecord>> recordCodecFactory)
    {
        ArgumentNullException.ThrowIfNull(recordCodecFactory);
        return new ProjectionBuilder<TKey, TRecord>(new(recordCodecFactory), Options);
    }

    public ProjectionBuilder<TKey, TRecord> WithRecordSchema(RecordSchema<TRecord> recordSchema)
    {
        ArgumentNullException.ThrowIfNull(recordSchema);
        return UseRecordCodec(_ => new RecordCodec<TKey, TRecord>(recordSchema));
    }

    [RequiresUnreferencedCode("Uses reflection-based resolver via populateMissingResolver.")]
    [RequiresDynamicCode("May require runtime code generation for reflection-based serialization.")]
    public ProjectionBuilder<TKey, TRecord> UseJsonRecordCodec(JsonSerializerOptions? options = null)
    {
        return WithRecordSchema(new RecordSchemaBuilder().WithoutVersioning<TRecord>(options));
    }

    public ProjectionBuilder<TKey, TRecord> UseJsonRecordCodec(JsonTypeInfo<TRecord> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        return WithRecordSchema(new RecordSchemaBuilder().WithoutVersioning(jsonTypeInfo));
    }

    public ProjectionBuilder<TKey, TRecord> WithRecordSchema(
        Func<RecordSchemaBuilder, IRecordSchemaFinalBuilder<TRecord>> recordSchemaFactory)
    {
        ArgumentNullException.ThrowIfNull(recordSchemaFactory);

        var schemaBuilder = recordSchemaFactory(new RecordSchemaBuilder());
        ArgumentNullException.ThrowIfNull(schemaBuilder);

        return WithRecordSchema(schemaBuilder.Build());
    }
}

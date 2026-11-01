using System;

namespace FolderDB.Building;

public sealed class ProjectionBuilder<TKey, TRecord>
    : CollectionOptionsBuilder<TKey, TRecord, ProjectionBuilder<TKey, TRecord>>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    protected override ProjectionBuilder<TKey, TRecord> This => this;
    private readonly RecordCodecStep<TKey, TRecord> _step;

    internal ProjectionBuilder(
        RecordCodecStep<TKey, TRecord> step,
        CollectionOptions<TKey, TRecord> options)
        : base(options)
    {
        _step = step;
    }

    public IndexBuilderNoProjection<TKey, TRecord> WithoutProjection()
    {
        return new IndexBuilderNoProjection<TKey, TRecord>(new(_step, static _ => default), Options);
    }

    public IndexBuilderWithProjection<TKey, TRecord, TProjection> WithProjection<TProjection>(Func<TRecord, TProjection> createProjection)
    {
        ArgumentNullException.ThrowIfNull(createProjection);
        return new IndexBuilderWithProjection<TKey, TRecord, TProjection>(new(_step, createProjection), Options);
    }
}

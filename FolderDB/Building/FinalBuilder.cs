using System;
using System.Collections.Generic;

namespace FolderDB.Building;

public sealed class FinalBuilder<TKey, TRecord, TProjection>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    private readonly IndexStep<TKey, TRecord, TProjection> _step;
    private readonly CollectionOptions<TKey, TRecord> _options;

    internal FinalBuilder(
        IndexStep<TKey, TRecord, TProjection> step,
        CollectionOptions<TKey, TRecord> options)
    {
        _step = step;
        _options = options;
    }

    public CollectionDefinition<TKey, TRecord, TProjection> Build()
    {
        return new CollectionDefinition<TKey, TRecord, TProjection>
        {
            Name = _options.Name ?? typeof(TRecord).Name,
            KeyComparer = _options.KeyComparer ?? Comparer<TKey>.Default,
            KeyEqualityComparer = _options.KeyEqualityComparer ?? EqualityComparer<TKey>.Default,
            FileNameGenerator = _options.FileNameGenerator ?? DefaultFileNaming,
            CreateProjection = _step.Previous.CreateProjection,
            RecordCodecFactory = _step.Previous.Previous.RecordCodecFactory,
            IndexPersistenceFactory = _step.IndexPersistenceFactory,
            IndexEngineFactory = _step.IndexEngineFactory
        };
    }

    private static IEnumerable<string> DefaultFileNaming(TRecord record)
    {
        var fileName = record.Id?.ToString();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException(
                $"Generated file name for record with id '{record.Id}' is null or whitespace.");
        }

        yield return fileName;
    }
}

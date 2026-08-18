using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Building;
using FolderDB.Encoding;
using FolderDB.FileStorage;
using FolderDB.Indexing;
using FolderDB.Indexing.Persistence;
using FolderDB.Retry;
using FolderDB.Runtime;
using Microsoft.Extensions.Logging;

namespace FolderDB;

public class TableDefinition<TKey, TRecord, TProjection>: ITableDefinition
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    public required string Name { get; init; }
    public Type RecordType => typeof(TRecord);
    public required IComparer<TKey> KeyComparer { get; init; }
    public required IEqualityComparer<TKey> KeyEqualityComparer { get; init; }
    public required Func<TRecord, IEnumerable<string>> FileNameGenerator { get; init; }
    public required Func<TRecord, TProjection> CreateProjection { get; init; }
    public required Func<RecordCodecContext, IRecordCodec<TKey, TRecord>> RecordCodecFactory { get; init; }
    public required Func<TableIndexPersistenceContext<TKey, TRecord, TProjection>, ITableIndexPersistence<TKey, TProjection>>? IndexPersistenceFactory { get; init; }
    public required Func<RecordScopedIndexEngineContext<TKey, TRecord, TProjection>, Task<IRecordScopedIndexEngine<TKey, TRecord, TProjection>>>? IndexEngineFactory { get; init; }

    public async Task<ITableEngine> StartEngineAsync(
        string tablePath,
        string indexFilePath,
        IFileStore fileStore,
        IRetryScheduler<string> retryScheduler,
        DatabaseOptions options,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        Validate();

        return await TableEngine<TKey, TRecord, TProjection>.StartAsync(
            tablePath,
            indexFilePath,
            this,
            fileStore,
            retryScheduler,
            options,
            loggerFactory,
            ct);
    }

    internal void Validate()
    {
        ThrowIfNull(KeyComparer, nameof(KeyComparer));
        ThrowIfNull(KeyEqualityComparer, nameof(KeyEqualityComparer));
        ThrowIfNull(FileNameGenerator, nameof(FileNameGenerator));
        ThrowIfNull(CreateProjection, nameof(CreateProjection));
        ThrowIfNull(RecordCodecFactory, nameof(RecordCodecFactory));
    }

    private void ThrowIfNull(object? member, string memberName)
    {
        if (member is null)
        {
            throw new InvalidOperationException(
                $"'{Name}' table's defined {memberName} is null.");
        }
    }
}

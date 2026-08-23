using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.FileStorage;
using FolderDB.Indexing;
using FolderDB.Indexing.Reconciliation;
using FolderDB.Indexing.State;
using FolderDB.Infrastructure.Collections;
using FolderDB.Infrastructure.Helpers;
using FolderDB.Infrastructure.Logging;
using FolderDB.Infrastructure.Watching;
using FolderDB.Building;
using FolderDB.Retry;
using Microsoft.Extensions.Logging;

namespace FolderDB.Runtime;

public class TableEngine<TKey, TRecord, TProjection> :
    ITableEngine,
    IIndexedTable<TKey, TRecord, TProjection>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    private readonly string _tablePath;
    private readonly IRetryScheduler<string> _retryScheduler;
    private readonly TableIndex<TKey, TRecord, TProjection> _index;
    private readonly FileReconciler<TKey, TRecord, TProjection> _fileReconciler;
    private readonly DirectoryReconciler<TKey, TRecord, TProjection> _directoryReconciler;
    private readonly DatabaseOperationProcessor<TKey, TRecord, TProjection> _processor;
    private readonly PathWatcher _watcher;
    private readonly IReadOnlyDictionary<TKey, IndexEntry<TProjection>> _indexEntries;
    private readonly IReadOnlyDictionary<TKey, TProjection> _projections;

    private bool _disposed;

    public IReadOnlyDictionary<TKey, TProjection> Index => _projections;
    public IReadOnlyDictionary<TKey, IndexEntry<TProjection>> Entries => _indexEntries;

    private TableEngine(
        string tablePath,
        IRetryScheduler<string> retryScheduler,
        TableIndex<TKey, TRecord, TProjection> index,
        FileReconciler<TKey, TRecord, TProjection> fileReconciler,
        DirectoryReconciler<TKey, TRecord, TProjection> directoryReconciler,
        DatabaseOperationProcessor<TKey, TRecord, TProjection> processor,
        PathWatcher watcher)
    {
        _tablePath = tablePath;
        _retryScheduler = retryScheduler;
        _index = index;
        _fileReconciler = fileReconciler;
        _directoryReconciler = directoryReconciler;
        _processor = processor;
        _watcher = watcher;
        _indexEntries = new MappedDictionaryView<TKey, IReadOnlyRecordIndexState<TKey, TProjection>, IndexEntry<TProjection>>(
            _index.Records, TryCreateIndexEntry);
        _projections = new MappedDictionaryView<TKey, IReadOnlyRecordIndexState<TKey, TProjection>, TProjection>(
            _index.Records, TryCreateProjection);
    }

    public static async Task<TableEngine<TKey, TRecord, TProjection>> StartAsync(
        string tablePath,
        string indexFilePath,
        TableDefinition<TKey, TRecord, TProjection> definition,
        IFileStore storage,
        IRetryScheduler<string> retryScheduler,
        DatabaseOptions options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var log = loggerFactory.CreateLogger<TableEngine<TKey, TRecord, TProjection>>();
        using var _ = log.BeginMethodScope();

        Directory.CreateDirectory(tablePath);

        var tableContext = CreateTableContext(definition, loggerFactory);
        var rawStore = new RecordStore<TKey, TRecord>(
            tableContext.RecordCodec,
            storage);
        var operationStore = new RecordStore<TKey, TRecord>(
            tableContext.RecordCodec,
            CreateRetryFileStore(
                storage,
                options,
                loggerFactory));
        var index = await CreateIndexAsync(indexFilePath, definition, tableContext, options, loggerFactory, cancellationToken);

        var fileReconciler = new FileReconciler<TKey, TRecord, TProjection>(
            tableContext,
            storage,
            rawStore,
            index,
            loggerFactory.CreateLogger<FileReconciler<TKey, TRecord, TProjection>>());
        var directoryReconciler = new DirectoryReconciler<TKey, TRecord, TProjection>(
            tablePath,
            index,
            fileReconciler,
            (path, minBackoff) => retryScheduler.Enqueue(path, fileReconciler.ReconcileAsync, minBackoff),
            loggerFactory.CreateLogger<DirectoryReconciler<TKey, TRecord, TProjection>>());

        var processor = new DatabaseOperationProcessor<TKey, TRecord, TProjection>(
            tablePath,
            tableContext,
            index,
            operationStore,
            options.MaxFileNameReserveAttempts,
            fileReconciler,
            (path, minBackoff) => retryScheduler.Enqueue(path, fileReconciler.ReconcileAsync, minBackoff),
            loggerFactory.CreateLogger<DatabaseOperationProcessor<TKey, TRecord, TProjection>>());

        var watcher = new PathWatcher(
            tablePath,
            ".json",
            loggerFactory.CreateLogger<PathWatcher>());
        var tableEngine = new TableEngine<TKey, TRecord, TProjection>(
            tablePath,
            retryScheduler,
            index,
            fileReconciler,
            directoryReconciler,
            processor,
            watcher);
        watcher.Changed += (_, path) => tableEngine.RequestFileReconcile(path);
        watcher.Error += (_, _) => tableEngine.RequestDirectoryReconcile();
        watcher.EnableRaisingEvents = true;

        tableEngine.RequestDirectoryReconcile();

        return tableEngine;
    }

    private static IFileStore CreateRetryFileStore(
        IFileStore inner,
        DatabaseOptions databaseOptions,
        ILoggerFactory loggerFactory)
    {
        if (databaseOptions.FileStoreRetryFactory is null)
        {
            return FolderDatabase.CreateDefaultRetryFileStore(
                inner,
                options: null,
                loggerFactory);
        }

        var retryStore = databaseOptions.FileStoreRetryFactory(new FileStoreRetryContext
        {
            Inner = inner,
            LoggerFactory = loggerFactory
        });
        if (retryStore is null)
        {
            throw new InvalidOperationException("The configured file store retry factory returned null.");
        }

        return retryStore;
    }

    private static TableContext<TKey, TRecord, TProjection> CreateTableContext(
        TableDefinition<TKey, TRecord, TProjection> definition,
        ILoggerFactory loggerFactory)
    {
        var recordContext = new RecordCodecContext(loggerFactory);
        var recordCodec = definition.RecordCodecFactory(recordContext);
        if (recordCodec is null)
        {
            throw new InvalidOperationException(
                $"'{definition.Name}' table's defined {nameof(definition.RecordCodecFactory)} does not provide record codec.");
        }

        return new TableContext<TKey, TRecord, TProjection>
        {
            Name = definition.Name,
            KeyComparer = definition.KeyComparer,
            KeyEqualityComparer = definition.KeyEqualityComparer,
            FileNameGenerator = definition.FileNameGenerator,
            CreateProjection = definition.CreateProjection,
            RecordCodec = recordCodec,
        };
    }

    private static async Task<TableIndex<TKey, TRecord, TProjection>> CreateIndexAsync(
        string indexFilePath,
        TableDefinition<TKey, TRecord, TProjection> definition,
        TableContext<TKey, TRecord, TProjection> tableContext,
        DatabaseOptions options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        IRecordScopedIndexEngine<TKey, TRecord, TProjection> engine;

        if (definition.IndexPersistenceFactory is not null)
        {
            var ctx = new TableIndexPersistenceContext<TKey, TRecord, TProjection>(tableContext, loggerFactory);
            var persistence = definition.IndexPersistenceFactory(ctx);
            if (persistence is null)
            {
                throw new InvalidOperationException(
                    $"'{tableContext.Name}' table's defined {nameof(definition.IndexPersistenceFactory)} does not provide index persistence.");
            }

            engine = await RecordScopedIndexEngine<TKey, TRecord, TProjection>.StartAsync(
                indexFilePath,
                tableContext,
                persistence,
                options.IndexAutoSaveInterval,
                loggerFactory,
                autoSaveEnabled: true,
                cancellationToken);
        }
        else if (definition.IndexEngineFactory is not null)
        {
            var ctx = new RecordScopedIndexEngineContext<TKey, TRecord, TProjection>
            {
                Table = tableContext,
                DatabaseOptions = options,
                IndexFilePath = indexFilePath,
                CancellationToken = cancellationToken
            };
            engine = await definition.IndexEngineFactory(ctx);
            if (engine is null)
            {
                throw new InvalidOperationException(
                    $"'{tableContext.Name}' table's defined {nameof(definition.IndexEngineFactory)} does not provide index engine.");
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"'{tableContext.Name}' table definition must provide either {nameof(definition.IndexPersistenceFactory)} or {nameof(definition.IndexEngineFactory)} to create the index engine.");
        }

        return new TableIndex<TKey, TRecord, TProjection>(engine, tableContext);
    }

    public Task<TRecord?> GetAsync(TKey id, CancellationToken cancellationToken = default) => _processor.GetAsync(id, cancellationToken);

    public Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default) => _processor.UpsertAsync(record, cancellationToken);

    public Task DeleteAsync(TKey id, CancellationToken cancellationToken = default) => _processor.DeleteAsync(id, cancellationToken);

    public Task<ReadResult<TRecord>> TryGetAsync(TKey id, CancellationToken cancellationToken = default) => _processor.TryGetAsync(id, cancellationToken);

    public Task<OperationResult> TryUpsertAsync(TRecord record, CancellationToken cancellationToken = default) => _processor.TryUpsertAsync(record, cancellationToken);

    public Task<OperationResult> TryDeleteAsync(TKey id, CancellationToken cancellationToken = default) => _processor.TryDeleteAsync(id, cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken = default) => _index.FlushAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, true))
            return;

        DisposeHelper.SafeDispose(_watcher);
        await DisposeHelper.SafeDispose(_index);
    }

    public void RequestDirectoryReconcile()
    {
        _retryScheduler.Enqueue(_tablePath, ReconcileDirectoryAsync);
    }

    private void RequestFileReconcile(string path)
    {
        _retryScheduler.Enqueue(path, ReconcileFileAsync);
    }

    private Task<RetryDecision> ReconcileFileAsync(string path, CancellationToken cancellationToken)
    {
        return _fileReconciler.ReconcileAsync(path, cancellationToken);
    }

    private Task<RetryDecision> ReconcileDirectoryAsync(string _, CancellationToken cancellationToken)
    {
        return _directoryReconciler.ReconcileAsync(cancellationToken);
    }

    private static bool TryCreateProjection(
        IReadOnlyRecordIndexState<TKey, TProjection> record,
        [MaybeNullWhen(false)] out TProjection projection)
    {
        var fileState = record.GetCurrentFileState();
        if (fileState.Status == FileIndexStatus.Committed && fileState.ErrorInfo is null)
        {
            projection = fileState.Projection!;
            return true;
        }

        projection = default;
        return false;
    }

    private static bool TryCreateIndexEntry(
        IReadOnlyRecordIndexState<TKey, TProjection> record,
        [MaybeNullWhen(false)] out IndexEntry<TProjection> entry)
    {
        var fileState = record.GetCurrentFileState();
        if (fileState.Status != FileIndexStatus.Reserved)
        {
            entry = new(fileState.Projection, fileState.ErrorInfo, record.CurrentFileName);
            return true;
        }

        entry = null;
        return false;
    }
}

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
using FolderDB.Infrastructure.Exceptions;
using FolderDB.Infrastructure.Helpers;
using FolderDB.Infrastructure.Logging;
using FolderDB.Infrastructure.Watching;
using FolderDB.Building;
using FolderDB.Retry;
using Microsoft.Extensions.Logging;

namespace FolderDB.Runtime;

public class CollectionEngine<TKey, TRecord, TProjection> :
    ICollectionEngine,
    IIndexedRecordCollection<TKey, TRecord, TProjection>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    private readonly string _collectionPath;
    private readonly IRetryScheduler<string> _retryScheduler;
    private readonly CollectionIndex<TKey, TRecord, TProjection> _index;
    private readonly FileReconciler<TKey, TRecord, TProjection> _fileReconciler;
    private readonly DirectoryReconciler<TKey, TRecord, TProjection> _directoryReconciler;
    private readonly StoreOperationProcessor<TKey, TRecord, TProjection> _processor;
    private readonly PathWatcher _watcher;
    private readonly IReadOnlyDictionary<TKey, IndexedFile<TProjection>> _indexedFiles;
    private readonly IReadOnlyDictionary<TKey, TProjection> _projections;

    private int _disposed;

    public IReadOnlyDictionary<TKey, TProjection> Projections => _projections;
    public IReadOnlyDictionary<TKey, IndexedFile<TProjection>> Files => _indexedFiles;

    private CollectionEngine(
        string collectionPath,
        IRetryScheduler<string> retryScheduler,
        CollectionIndex<TKey, TRecord, TProjection> index,
        FileReconciler<TKey, TRecord, TProjection> fileReconciler,
        DirectoryReconciler<TKey, TRecord, TProjection> directoryReconciler,
        StoreOperationProcessor<TKey, TRecord, TProjection> processor,
        PathWatcher watcher)
    {
        _collectionPath = collectionPath;
        _retryScheduler = retryScheduler;
        _index = index;
        _fileReconciler = fileReconciler;
        _directoryReconciler = directoryReconciler;
        _processor = processor;
        _watcher = watcher;
        _indexedFiles = new MappedDictionaryView<TKey, IReadOnlyRecordIndexState<TKey, TProjection>, IndexedFile<TProjection>>(
            _index.Records, TryCreateIndexedFile);
        _projections = new MappedDictionaryView<TKey, IReadOnlyRecordIndexState<TKey, TProjection>, TProjection>(
            _index.Records, TryCreateProjection);
    }

    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A factory configured on <paramref name="definition"/> or in <paramref name="options"/>
    /// returned null.
    ///
    /// -or-
    ///
    /// <paramref name="definition"/> provides neither an index persistence nor an index engine
    /// factory.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <remarks>
    /// Also exceptions from:
    /// <see cref="Directory.CreateDirectory(string)"/>,
    /// <see cref="PathWatcher(string, string, ILogger{PathWatcher})"/>,
    /// <see cref="PathWatcher.EnableRaisingEvents"/>.
    /// </remarks>
    public static async Task<CollectionEngine<TKey, TRecord, TProjection>> StartAsync(
        string collectionPath,
        string indexFilePath,
        CollectionDefinition<TKey, TRecord, TProjection> definition,
        IFileStore storage,
        IRetryScheduler<string> retryScheduler,
        StoreOptions options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var log = loggerFactory.CreateLogger<CollectionEngine<TKey, TRecord, TProjection>>();
        using var _ = log.BeginMethodScope();

        Directory.CreateDirectory(collectionPath);

        var collectionContext = CreateCollectionContext(definition, loggerFactory);
        var rawStore = new RecordStore<TKey, TRecord>(
            collectionContext.RecordCodec,
            storage);
        var operationStore = new RecordStore<TKey, TRecord>(
            collectionContext.RecordCodec,
            CreateRetryFileStore(
                storage,
                options,
                loggerFactory));
        var index = await CreateIndexAsync(indexFilePath, definition, collectionContext, options, loggerFactory, cancellationToken);

        var fileReconciler = new FileReconciler<TKey, TRecord, TProjection>(
            collectionContext,
            storage,
            rawStore,
            index,
            loggerFactory.CreateLogger<FileReconciler<TKey, TRecord, TProjection>>());
        var directoryReconciler = new DirectoryReconciler<TKey, TRecord, TProjection>(
            collectionPath,
            index,
            fileReconciler,
            (path, minBackoff) => retryScheduler.Enqueue(path, fileReconciler.ReconcileAsync, minBackoff),
            loggerFactory.CreateLogger<DirectoryReconciler<TKey, TRecord, TProjection>>());

        var processor = new StoreOperationProcessor<TKey, TRecord, TProjection>(
            collectionPath,
            collectionContext,
            index,
            operationStore,
            options.MaxFileNameReserveAttempts,
            fileReconciler,
            (path, minBackoff) => retryScheduler.Enqueue(path, fileReconciler.ReconcileAsync, minBackoff),
            loggerFactory.CreateLogger<StoreOperationProcessor<TKey, TRecord, TProjection>>());

        var watcher = new PathWatcher(
            collectionPath,
            ".json",
            loggerFactory.CreateLogger<PathWatcher>());
        var collectionEngine = new CollectionEngine<TKey, TRecord, TProjection>(
            collectionPath,
            retryScheduler,
            index,
            fileReconciler,
            directoryReconciler,
            processor,
            watcher);
        watcher.Changed += (_, path) => collectionEngine.RequestFileReconcile(path);
        watcher.Error += (_, _) => collectionEngine.RequestDirectoryReconcile();
        watcher.EnableRaisingEvents = true;

        collectionEngine.RequestDirectoryReconcile();

        return collectionEngine;
    }

    /// <exception cref="InvalidOperationException">
    /// The configured file store retry factory returned null.
    /// </exception>
    private static IFileStore CreateRetryFileStore(
        IFileStore inner,
        StoreOptions storeOptions,
        ILoggerFactory loggerFactory)
    {
        if (storeOptions.FileStoreRetryFactory is null)
        {
            return new RetryFileStore(inner, options: null, loggerFactory);
        }

        var retryStore = storeOptions.FileStoreRetryFactory(new FileStoreRetryContext
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

    /// <exception cref="InvalidOperationException">
    /// The record codec factory configured on the definition returned null.
    /// </exception>
    private static CollectionContext<TKey, TRecord, TProjection> CreateCollectionContext(
        CollectionDefinition<TKey, TRecord, TProjection> definition,
        ILoggerFactory loggerFactory)
    {
        var recordContext = new RecordCodecContext(loggerFactory);
        var recordCodec = definition.RecordCodecFactory(recordContext);
        if (recordCodec is null)
        {
            throw new InvalidOperationException(
                $"'{definition.Name}' collection's defined {nameof(definition.RecordCodecFactory)} does not provide record codec.");
        }

        return new CollectionContext<TKey, TRecord, TProjection>
        {
            Name = definition.Name,
            KeyComparer = definition.KeyComparer,
            KeyEqualityComparer = definition.KeyEqualityComparer,
            FileNameGenerator = definition.FileNameGenerator,
            CreateProjection = definition.CreateProjection,
            RecordCodec = recordCodec,
        };
    }

    /// <exception cref="InvalidOperationException">
    /// The index persistence factory configured on the definition returned null.
    ///
    /// -or-
    ///
    /// The index engine factory configured on the definition returned null.
    ///
    /// -or-
    ///
    /// The definition provides neither of the two factories.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    private static async Task<CollectionIndex<TKey, TRecord, TProjection>> CreateIndexAsync(
        string indexFilePath,
        CollectionDefinition<TKey, TRecord, TProjection> definition,
        CollectionContext<TKey, TRecord, TProjection> collectionContext,
        StoreOptions options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        IRecordScopedIndexEngine<TKey, TRecord, TProjection> engine;

        if (definition.IndexPersistenceFactory is not null)
        {
            var ctx = new CollectionIndexPersistenceContext<TKey, TRecord, TProjection>(collectionContext, loggerFactory);
            var persistence = definition.IndexPersistenceFactory(ctx);
            if (persistence is null)
            {
                throw new InvalidOperationException(
                    $"'{collectionContext.Name}' collection's defined {nameof(definition.IndexPersistenceFactory)} does not provide index persistence.");
            }

            engine = await RecordScopedIndexEngine<TKey, TRecord, TProjection>.StartAsync(
                indexFilePath,
                collectionContext,
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
                Collection = collectionContext,
                StoreOptions = options,
                IndexFilePath = indexFilePath,
                CancellationToken = cancellationToken
            };
            engine = await definition.IndexEngineFactory(ctx);
            if (engine is null)
            {
                throw new InvalidOperationException(
                    $"'{collectionContext.Name}' collection's defined {nameof(definition.IndexEngineFactory)} does not provide index engine.");
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"'{collectionContext.Name}' collection definition must provide either {nameof(definition.IndexPersistenceFactory)} or {nameof(definition.IndexEngineFactory)} to create the index engine.");
        }

        return new CollectionIndex<TKey, TRecord, TProjection>(engine, collectionContext);
    }

    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileOperationException">
    /// The record file could not be read.
    ///
    /// -or-
    ///
    /// Its content could not be decoded.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public Task<TRecord?> GetAsync(TKey id, CancellationToken cancellationToken = default) => _processor.GetAsync(id, cancellationToken);

    /// <exception cref="ArgumentNullException">
    /// <paramref name="record"/> is <see langword="null"/>.
    ///
    /// -or-
    ///
    /// The <see cref="IRecord{TKey}.Id"/> of the provided <paramref name="record"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="FileNameGenerationException">
    /// The configured <see cref="CollectionOptions{TKey, TRecord}.FileNameGenerator"/> threw.
    ///
    /// -or-
    ///
    /// The generator produced a candidate that is <see langword="null"/>, empty, or consists only of white-space characters,
    /// too long, or contains one or more of the invalid characters defined in <see cref="Path.GetInvalidFileNameChars"/>.
    ///
    /// -or-
    ///
    /// The generator ran out of candidates.
    ///
    /// -or-
    ///
    /// No free file name was found within
    /// <see cref="StoreOptions.MaxFileNameReserveAttempts"/> attempts.
    /// </exception>
    /// <exception cref="FileOperationException">
    /// The record could not be encoded.
    ///
    /// -or-
    ///
    /// The record file could not be written.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default) => _processor.UpsertAsync(record, cancellationToken);

    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileOperationException">The record file could not be deleted.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public Task DeleteAsync(TKey id, CancellationToken cancellationToken = default) => _processor.DeleteAsync(id, cancellationToken);

    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public Task<ReadResult<TRecord>> TryGetAsync(TKey id, CancellationToken cancellationToken = default) => _processor.TryGetAsync(id, cancellationToken);

    /// <exception cref="ArgumentNullException">
    /// <paramref name="record"/> is <see langword="null"/>.
    ///
    /// -or-
    ///
    /// The <see cref="IRecord{TKey}.Id"/> of the provided <paramref name="record"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="FileNameGenerationException">
    /// The configured <see cref="CollectionOptions{TKey, TRecord}.FileNameGenerator"/> threw.
    ///
    /// -or-
    ///
    /// The generator produced a candidate that is <see langword="null"/>, empty, or consists only of white-space characters,
    /// too long, or contains one or more of the invalid characters defined in <see cref="Path.GetInvalidFileNameChars"/>.
    ///
    /// -or-
    ///
    /// The generator ran out of candidates.
    ///
    /// -or-
    ///
    /// No free file name was found within
    /// <see cref="StoreOptions.MaxFileNameReserveAttempts"/> attempts.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public Task<OperationResult> TryUpsertAsync(TRecord record, CancellationToken cancellationToken = default) => _processor.TryUpsertAsync(record, cancellationToken);

    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public Task<OperationResult> TryDeleteAsync(TKey id, CancellationToken cancellationToken = default) => _processor.TryDeleteAsync(id, cancellationToken);

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public Task FlushAsync(CancellationToken cancellationToken = default) => _index.FlushAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        DisposeHelper.SafeDispose(_watcher);
        await DisposeHelper.SafeDispose(_index);
    }

    public void RequestDirectoryReconcile()
    {
        _retryScheduler.Enqueue(_collectionPath, ReconcileDirectoryAsync);
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

    private static bool TryCreateIndexedFile(
        IReadOnlyRecordIndexState<TKey, TProjection> record,
        [MaybeNullWhen(false)] out IndexedFile<TProjection> file)
    {
        var fileState = record.GetCurrentFileState();
        if (fileState.Status != FileIndexStatus.Reserved)
        {
            file = new(fileState.Projection, fileState.ErrorInfo, record.CurrentFileName);
            return true;
        }

        file = null;
        return false;
    }
}

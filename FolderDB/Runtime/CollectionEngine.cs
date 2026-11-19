using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.FileStorage;
using FolderDB.Indexing;
using FolderDB.Indexing.Persistence;
using FolderDB.Indexing.Reconciliation;
using FolderDB.Indexing.State;
using FolderDB.Infrastructure.Collections;
using FolderDB.Infrastructure.Exceptions;
using FolderDB.Infrastructure.Helpers;
using FolderDB.Infrastructure.Logging;
using FolderDB.Infrastructure.Watching;
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

    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="storeContext"/> is null.
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
        StoreContext storeContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(storeContext);

        var storage = storeContext.FileStore;
        var retryScheduler = storeContext.RetryScheduler;
        var name = definition.Name ?? typeof(TRecord).Name;
        var loggerFactory = storeContext.LoggerFactory.CreateCollectionScopedLoggerFactory(name);

        var log = loggerFactory.CreateLogger<CollectionEngine<TKey, TRecord, TProjection>>();
        using var _ = log.BeginMethodScope();

        Directory.CreateDirectory(collectionPath);

        var collectionContext = CreateCollectionContext(name, definition);
        var rawStore = new RecordStore<TKey, TRecord>(
            collectionContext.RecordCodec,
            storage);
        var operationStore = new RecordStore<TKey, TRecord>(
            collectionContext.RecordCodec,
            new RetryFileStore(
                storage,
                storeContext.ReadRetryDelays,
                storeContext.WriteRetryDelays,
                loggerFactory));
        var index = await CreateIndexAsync(indexFilePath, collectionContext, storeContext, loggerFactory, cancellationToken);

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

    internal static CollectionContext<TKey, TRecord, TProjection> CreateCollectionContext(
        string name,
        CollectionDefinition<TKey, TRecord, TProjection> definition)
    {
        return new CollectionContext<TKey, TRecord, TProjection>
        {
            Name = name,
            KeyComparer = definition.KeyComparer ?? Comparer<TKey>.Default,
            KeyEqualityComparer = definition.KeyEqualityComparer ?? EqualityComparer<TKey>.Default,
            FileNameGenerator = definition.FileNames ?? DefaultFileNaming,
            CreateProjection = definition.Projection,
            RecordCodec = definition.Codec,
            KeyTypeInfo = definition.KeyTypeInfo,
            ProjectionTypeInfo = definition.ProjectionTypeInfo,
        };
    }

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    private static async Task<CollectionIndex<TKey, TRecord, TProjection>> CreateIndexAsync(
        string indexFilePath,
        CollectionContext<TKey, TRecord, TProjection> collectionContext,
        StoreContext storeContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var persistence = new JsonCollectionIndexPersistence<TKey, TProjection>(
            collectionContext.KeyTypeInfo,
            collectionContext.ProjectionTypeInfo,
            collectionContext.KeyEqualityComparer,
            loggerFactory.CreateLogger<JsonCollectionIndexPersistence<TKey, TProjection>>());

        var engine = await RecordScopedIndexEngine<TKey, TRecord, TProjection>.StartAsync(
            indexFilePath,
            collectionContext,
            persistence,
            storeContext.IndexAutoSaveInterval,
            loggerFactory,
            autoSaveEnabled: true,
            cancellationToken);

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
    /// The configured <see cref="CollectionDefinition{TKey, TRecord, TProjection}.FileNames"/> threw.
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
    /// The generator repeated a candidate.
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
    /// The configured <see cref="CollectionDefinition{TKey, TRecord, TProjection}.FileNames"/> threw.
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
    /// The generator repeated a candidate.
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

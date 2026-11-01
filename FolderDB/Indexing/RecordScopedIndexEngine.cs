using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.FileStorage;
using FolderDB.Indexing.Persistence;
using FolderDB.Indexing.State;
using FolderDB.Infrastructure.Collections;
using FolderDB.Infrastructure.Concurrency;
using FolderDB.Infrastructure.Logging;
using FolderDB.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nito.AsyncEx;

namespace FolderDB.Indexing;

public class RecordScopedIndexEngine<TKey, TRecord, TProjection> : IRecordScopedIndexEngine<TKey, TRecord, TProjection>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    private readonly CollectionContext<TKey, TRecord, TProjection> _context;
    private readonly ILogger<RecordScopedIndexEngine<TKey, TRecord, TProjection>> _logger;
    private readonly AutoSaver _autoSaver;
    private readonly CollectionIndexState<TKey, TProjection> _indexState;

    public IReadOnlyDictionary<TKey, IReadOnlyRecordIndexState<TKey, TProjection>> Records { get; }
    public IReadOnlyDictionary<string, IReadOnlyFileIndexState<TKey, TProjection>> Files { get; }
    public AsyncReaderWriterLock Barrier { get; } = new();
    public StripedAsyncLock<TKey> IdLocks { get; }

    private RecordScopedIndexEngine(
        string indexPath,
        CollectionIndexState<TKey, TProjection> state,
        ICollectionIndexPersistence<TKey, TProjection> persistence,
        CollectionContext<TKey, TRecord, TProjection> context,
        TimeSpan savingInterval,
        ILogger<RecordScopedIndexEngine<TKey, TRecord, TProjection>> logger,
        ILoggerFactory loggerFactory)
    {
        _indexState = state;
        _context = context;
        _logger = logger;

        IdLocks = new StripedAsyncLock<TKey>(128, context.KeyEqualityComparer);
        Records = new CovariantReadOnlyDictionary<TKey, RecordIndexState<TKey, TProjection>, IReadOnlyRecordIndexState<TKey, TProjection>>(_indexState.Records);
        Files = new CovariantReadOnlyDictionary<string, FileIndexState<TKey, TProjection>, IReadOnlyFileIndexState<TKey, TProjection>>(_indexState.Files);
        
        _autoSaver = new AutoSaver(
            indexPath,
            savingInterval,
            cancellationToken =>
            {
                using (Barrier.WriterLock(cancellationToken))
                {
                    return persistence.SerializeToBytes(_indexState);
                }
            },
            loggerFactory.CreateLogger<AutoSaver>());
    }

    public static async Task<RecordScopedIndexEngine<TKey, TRecord, TProjection>> StartAsync(
        string indexFilePath,
        CollectionContext<TKey, TRecord, TProjection> context,
        ICollectionIndexPersistence<TKey, TProjection> persistence,
        TimeSpan? savingInterval = null,
        ILoggerFactory? loggerFactory = null,
        bool autoSaveEnabled = true,
        CancellationToken cancellationToken = default)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        var engineLogger = loggerFactory.CreateLogger<RecordScopedIndexEngine<TKey, TRecord, TProjection>>();

        using var _ = engineLogger.BeginMethodScope();

        savingInterval ??= TimeSpan.FromSeconds(10);

        var state = await persistence.LoadIfExistsAsync(indexFilePath, cancellationToken)
            ?? new CollectionIndexState<TKey, TProjection>(context.KeyEqualityComparer);

        var engine = new RecordScopedIndexEngine<TKey, TRecord, TProjection>(
            indexFilePath,
            state,
            persistence,
            context,
            savingInterval.Value,
            engineLogger,
            loggerFactory);
        if (autoSaveEnabled)
        {
            engine._autoSaver.Start();
        }
        return engine;
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        return _autoSaver.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _autoSaver.DisposeAsync();
    }
    
    public IndexOperationResult Upsert(
        TKey id,
        string fileName,
        FileFingerprint fingerprint,
        int? schemaVersion,
        TRecord record)
    {
        return Upsert(id, fileName, fingerprint, schemaVersion, record, null);
    }

    public IndexOperationResult Upsert(TKey id, string fileName, FileFingerprint fingerprint, FileErrorInfo errorInfo)
    {
        return Upsert(id, fileName, fingerprint, null, null, errorInfo);
    }

    private IndexOperationResult Upsert(
        TKey id,
        string fileName,
        FileFingerprint fingerprint,
        int? schemaVersion,
        TRecord? record,
        FileErrorInfo? errorInfo)
    {
        using var _ = _logger.BeginMethodScope();

        var recordState = GetOrCreateRecord(id, out var recordIsNew);
        if (!recordIsNew && recordState.Files.TryGetValue(fileName, out var ownedFile))
        {
            return UpdateExistingFile(id, fileName, fingerprint, schemaVersion, record, errorInfo, ownedFile);
        }

        var fileState = CreateFileState(recordState, fingerprint, schemaVersion, record, errorInfo);
        fileState = _indexState.Files.GetOrAdd(fileName, fileState);
        if (ReferenceEquals(fileState.Record, recordState))
        {
            recordState.Files[fileName] = fileState;
            recordState.RecalculateCurrent();
            if (recordIsNew)
            {
                _indexState.Records[recordState.Id] = recordState;
            }

            _logger.LogDebug("Added: file=\"{File}\" id={Id} status={Status} indexedRecords={IndexedRecords}",
                fileName, id, fileState.Status, _indexState.Records.Count);
            _autoSaver.MarkDirty();

            return IndexOperationResult.Applied;
        }

        if (_context.KeyEqualityComparer.Equals(fileState.Record.Id, id))
        {
            throw new InvalidOperationException(
                $"Index invariant violation, same id has different record states: id={id} file=\"{fileName}\"");
        }

        _logger.LogTrace("Upsert failed, file already exists with different id: id={Id} file=\"{File}\" existingId={ExistingId}",
            id, fileName, fileState.Record.Id);
        return IndexOperationResult.BlockedByAnotherId;
    }

    public bool TryReserveFileName(TKey id, string fileName)
    {
        using var _ = _logger.BeginMethodScope();

        var recordState = GetOrCreateRecord(id, out var recordIsNew);
        var fileState = new FileIndexState<TKey, TProjection>
        {
            Record = recordState,
            Status = FileIndexStatus.Reserved,
            ErrorInfo = null,
            Projection = default,
            Fingerprint = default,
            SchemaVersion = null
        };

        if (!_indexState.Files.TryAdd(fileName, fileState))
        {
            return false;
        }

        recordState.Files[fileName] = fileState;
        recordState.RecalculateCurrent();
        if (recordIsNew)
        {
            _indexState.Records[recordState.Id] = recordState;
        }

        _logger.LogDebug("Reserved file name: id={Id} file=\"{File}\" indexedRecords={IndexedRecords}", id, fileName, _indexState.Records.Count);
        _autoSaver.MarkDirty();
        return true;
    }

    public bool CommitReservedFileName(TKey id, string fileName, FileFingerprint fingerprint, TRecord record)
    {
        using var _ = _logger.BeginMethodScope();

        if (_indexState.Files.TryGetValue(fileName, out var file) &&
            _context.KeyEqualityComparer.Equals(file.Record.Id, id) &&
            file.Status == FileIndexStatus.Reserved)
        {
            var projection = _context.CreateProjection(record);
            file.Status = FileIndexStatus.Committed;
            file.ErrorInfo = null;
            file.Projection = projection;
            file.Fingerprint = fingerprint;
            // A successfully written reserved file has the codec's current physical schema version.
            file.SchemaVersion = _context.RecordCodec.CurrentSchemaVersion;
            file.Record.RecalculateCurrent();
            _indexState.Records[id] = file.Record;

            _logger.LogDebug("Committed reserved file: id={Id} file=\"{File}\"", id, fileName);
            _autoSaver.MarkDirty();
            return true;
        }
        else
        {
            _logger.LogWarning("Commit failed, file is not reserved by id: id={Id} file=\"{File}\"", id, fileName);
            return false;
        }
    }

    public bool Delete(TKey id)
    {
        using var __ = _logger.BeginMethodScope();

        if (!_indexState.Records.Remove(id, out var record))
        {
            return false;
        }

        foreach (var fileName in record.Files.Keys)
        {
            _indexState.Files.Remove(fileName, out _);
        }

        _logger.LogDebug("Deleted: id={Id} indexedRecords={IndexedRecords}", id, _indexState.Records.Count);
        _autoSaver.MarkDirty();

        return true;

    }

    public IndexOperationResult DeleteFile(TKey id, string fileName)
    {
        using var __ = _logger.BeginMethodScope();

        if (!_indexState.Files.TryGetValue(fileName, out var file))
        {
            return IndexOperationResult.NoChanges;
        }

        if (!_context.KeyEqualityComparer.Equals(file.Record.Id, id))
        {
            _logger.LogTrace(
                "Delete file blocked by another id: id={Id} file=\"{File}\" existingId={ExistingId}",
                id,
                fileName,
                file.Record.Id);
            return IndexOperationResult.BlockedByAnotherId;
        }

        if (!_indexState.Files.TryRemove(new(fileName, file)))
        {
            return IndexOperationResult.NoChanges;
        }

        var record = file.Record;
        record.Files.Remove(fileName);
        if (record.Files.Count > 0)
        {
            record.RecalculateCurrent();
        }
        else
        {
            _indexState.Records.Remove(record.Id, out _);
        }

        _logger.LogDebug("Deleted: id={Id} file=\"{File}\" indexedRecords={IndexedRecords}", id, fileName, _indexState.Records.Count);
        _autoSaver.MarkDirty();

        return IndexOperationResult.Applied;
    }

    public void Clear()
    {
        using var _ = _logger.BeginMethodScope();

        _indexState.Records.Clear();
        _indexState.Files.Clear();
        _logger.LogDebug("Index cleared");

        _autoSaver.MarkDirty();
    }

    private RecordIndexState<TKey, TProjection> GetOrCreateRecord(TKey id, out bool isNew)
    {
        if (_indexState.Records.TryGetValue(id, out var record))
        {
            isNew = false;
            return record;
        }

        isNew = true;
        return new RecordIndexState<TKey, TProjection> { Id = id };
    }

    private FileIndexState<TKey, TProjection> CreateFileState(
        RecordIndexState<TKey, TProjection> recordState,
        FileFingerprint fingerprint,
        int? schemaVersion,
        TRecord? record,
        FileErrorInfo? errorInfo)
    {
        return record != null
            ? new FileIndexState<TKey, TProjection>
            {
                Record = recordState,
                Status = FileIndexStatus.Committed,
                Projection = _context.CreateProjection(record),
                Fingerprint = fingerprint,
                SchemaVersion = schemaVersion
            }
            : new FileIndexState<TKey, TProjection>
            {
                Record = recordState,
                Status = FileIndexStatus.Committed,
                ErrorInfo = errorInfo,
                Projection = default,
                Fingerprint = fingerprint,
                SchemaVersion = null
            };
    }

    private IndexOperationResult UpdateExistingFile(
        TKey id,
        string fileName,
        FileFingerprint fingerprint,
        int? schemaVersion,
        TRecord? record,
        FileErrorInfo? errorInfo,
        FileIndexState<TKey, TProjection> fileState)
    {
        var updated = false;

        if (fileState.Fingerprint != fingerprint)
        {
            // Refresh projection only after successfully processing a changed file.
            // Error states keep the previous projection until a valid file version is read.
            if (record != null)
            {
                // Create projection before updating fingerprint to avoid partial update if projection creation throws.
                fileState.Projection = _context.CreateProjection(record);
            }

            fileState.Fingerprint = fingerprint;
            updated = true;
        }

        if (record != null && fileState.SchemaVersion != schemaVersion)
        {
            fileState.SchemaVersion = schemaVersion;
            updated = true;
        }

        if (fileState.Status != FileIndexStatus.Committed)
        {
            fileState.Status = FileIndexStatus.Committed;
            updated = true;
        }

        if (fileState.ErrorInfo != errorInfo)
        {
            fileState.ErrorInfo = errorInfo;
            updated = true;
        }

        if (updated)
        {
            fileState.Record.RecalculateCurrent();
            _logger.LogDebug("Updated fingerprint: file=\"{File}\" id={Id} fingerprint=\"{Fingerprint}\" status={Status}",
                fileName, id, fingerprint, fileState.Status);
            _autoSaver.MarkDirty();

            return IndexOperationResult.Applied;
        }
        else
        {
            _logger.LogTrace("Upsert skipped, same file already exists: id={Id} file=\"{File}\"", id, fileName);
            return IndexOperationResult.NoChanges;
        }
    }
}

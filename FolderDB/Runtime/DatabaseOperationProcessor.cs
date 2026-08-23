using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.FileStorage;
using FolderDB.Indexing;
using FolderDB.Indexing.Reconciliation;
using FolderDB.Indexing.Scopes;
using FolderDB.Indexing.State;
using FolderDB.Infrastructure.Exceptions;
using FolderDB.Infrastructure.Logging;
using FolderDB.Retry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderDB.Runtime;

internal class DatabaseOperationProcessor<TKey, TRecord, TProjection>(
    string tablePath,
    TableContext<TKey, TRecord, TProjection> context,
    TableIndex<TKey, TRecord, TProjection> index,
    RecordStore<TKey, TRecord> store,
    int maxFileNameReserveAttempts,
    FileReconciler<TKey, TRecord, TProjection> fileReconciler,
    Action<string, bool> requestFileReconcile,
    ILogger<DatabaseOperationProcessor<TKey, TRecord, TProjection>>? logger)
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    private readonly int _maxFileNameReserveAttempts = Math.Max(1, maxFileNameReserveAttempts);
    private readonly ILogger<DatabaseOperationProcessor<TKey, TRecord, TProjection>> _logger =
        logger ?? NullLogger<DatabaseOperationProcessor<TKey, TRecord, TProjection>>.Instance;

    public async Task<TRecord?> GetAsync(TKey id, CancellationToken cancellationToken = default)
    {
        var result = await TryGetAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            ThrowOperationFailed("get", id, result);
        }

        return result.Record;
    }

    public async Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        var result = await TryUpsertAsync(record, cancellationToken);
        if (!result.IsSuccess)
        {
            ThrowOperationFailed("upsert", record.Id, result);
        }
    }

    public async Task DeleteAsync(TKey id, CancellationToken cancellationToken = default)
    {
        var result = await TryDeleteAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            ThrowOperationFailed("delete", id, result);
        }
    }

    public async Task<ReadResult<TRecord>> TryGetAsync(TKey id, CancellationToken cancellationToken = default)
    {
        using var _ = _logger.BeginMethodScope();
        using var sharedIndexScope = await index.EnterSharedScopeAsync(cancellationToken);
        using var recordScope = await sharedIndexScope.LockRecordAsync(id, cancellationToken);

        if (!recordScope.TryGetState(out var recordState))
        {
            _logger.LogTrace("Get miss in index: id={Id}", id);
            return new ReadResult<TRecord>(null);
        }

        var fileName = recordState.CurrentFileName;
        var filePath = Path.Combine(tablePath, fileName);
        var readResult = await store.ReadAsync(filePath, cancellationToken);

        var retryDecision = await fileReconciler.ContinueAfterReadAsync(
            filePath,
            sharedIndexScope,
            recordScope,
            readResult,
            cancellationToken);
        if (retryDecision != RetryDecision.Complete)
        {
            requestFileReconcile(filePath, retryDecision == RetryDecision.RetryWithMinBackoff);
        }

        if (readResult.Error != null)
        {
            return new ReadResult<TRecord>(null, readResult.Error, fileName);
        }

        if (!readResult.Fingerprint.Exists)
        {
            return new ReadResult<TRecord>(null, null, fileName);
        }

        var record = readResult.Value.Record;
        if (!context.KeyEqualityComparer.Equals(record.Id, id))
        {
            _logger.LogDebug(
                "Get found file assigned to another id: expectedId={ExpectedId} actualId={ActualId} file=\"{File}\" retryDecision={RetryDecision}",
                id,
                record.Id,
                fileName,
                retryDecision);
            return new ReadResult<TRecord>(null, null, fileName);
        }

        _logger.LogTrace("Get hit: id={Id} file=\"{File}\"", id, fileName);
        return new ReadResult<TRecord>(record, null, fileName);
    }

    public async Task<OperationResult> TryUpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var _ = _logger.BeginMethodScope();
        using var sharedIndexScope = await index.EnterSharedScopeAsync(cancellationToken);
        using var recordScope = await sharedIndexScope.LockRecordAsync(record.Id, cancellationToken);

        if (!recordScope.TryGetState(out var recordState))
        {
            return await InsertAsync(recordScope, record, cancellationToken);
        }

        var fileName = recordState.CurrentFileName;
        var currentPath = Path.Combine(tablePath, fileName);
        var writeResult = await store.WriteAsync(currentPath, record, cancellationToken);

        if (writeResult.Error != null)
        {
            _logger.LogWarning(
                "Upsert write failed: id={Id} file=\"{File}\" errorReason={ErrorReason} errorPersistence={ErrorPersistence}",
                record.Id,
                fileName,
                writeResult.Error.Reason,
                writeResult.Error.Persistence);
            return new OperationResult(writeResult.Error, fileName);
        }

        var upsertResult = recordScope.Upsert(
            fileName,
            writeResult.Fingerprint!.Value,
            context.RecordCodec.CurrentSchemaVersion,
            record);
        if (upsertResult == IndexOperationResult.BlockedByAnotherId)
        {
            // Impossible scenario since the file is locked
            throw new InvalidOperationException($"Failed to upsert record id '{record.Id}' to existing file '{fileName}'.");
        }

        _logger.LogDebug("Upsert applied to current file: id={Id} file=\"{File}\"", record.Id, fileName);
        return new OperationResult(null, fileName);
    }

    public async Task<OperationResult> TryDeleteAsync(TKey id, CancellationToken cancellationToken = default)
    {
        using var _ = _logger.BeginMethodScope();
        using var sharedIndexScope = await index.EnterSharedScopeAsync(cancellationToken);
        using var recordScope = await sharedIndexScope.LockRecordAsync(id, cancellationToken);

        if (!recordScope.TryGetState(out var recordState))
        {
            _logger.LogTrace("Delete miss in index: id={Id}", id);
            return OperationResult.Success;
        }

        var fileNames = recordState.Files.Keys.ToArray();
        foreach (var fileName in fileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = Path.Combine(tablePath, fileName);
            var deleteResult = await store.DeleteAsync(filePath, cancellationToken);
            if (deleteResult.Error != null)
            {
                _logger.LogError(
                    "Delete failed: id={Id} file=\"{File}\" errorReason={ErrorReason} errorPersistence={ErrorPersistence}",
                    id,
                    fileName,
                    deleteResult.Error.Reason,
                    deleteResult.Error.Persistence);
                return new OperationResult(deleteResult.Error, fileName);
            }

            var deleteFileResult = recordScope.DeleteFile(fileName);
            if (deleteFileResult == IndexOperationResult.BlockedByAnotherId)
            {
                // Impossible scenario since the file is locked
                throw new InvalidOperationException($"Failed to delete file '{fileName}' for record id '{id}'.");
            }

            _logger.LogDebug(
                "Deleted file: id={Id} file=\"{File}\" indexDeleteResult={IndexDeleteResult}",
                id,
                fileName,
                deleteFileResult);
        }

        _logger.LogDebug("Delete applied: id={Id} files={Files}", id, fileNames.Length);
        return new OperationResult(null, fileNames[0]);
    }

    private static void ThrowOperationFailed(string operation, TKey id, OperationResult result)
    {
        if (result.Error?.Exception is not null)
        {
            throw result.Error.Exception;
        }

        throw new InvalidOperationException(
            $"Failed to {operation} record id '{id}'. Error: {result.ErrorReason}.");
    }

    private async Task<OperationResult> InsertAsync(
        RecordScope<TKey, TRecord, TProjection> recordScope,
        TRecord record,
        CancellationToken cancellationToken)
    {
        using var iterator = CreateFileNameIterator(record);
        for (var attempt = 0; attempt < _maxFileNameReserveAttempts; attempt++)
        {
            if (!MoveNextFileNameCandidate(record.Id, iterator))
            {
                throw new InvalidOperationException($"Unable to reserve a file name for record id '{record.Id}'.");
            }

            var reservedFileName = BuildFileName(record.Id, iterator.Current);
            if (!recordScope.TryReserveFileName(reservedFileName))
            {
                continue;
            }

            try
            {
                var reservedPath = Path.Combine(tablePath, reservedFileName);

                // This can overwrite a disk-only file with the same name, which is acceptable because
                // the file system is not lockable by FolderDB and can change at any time.
                var writeResult = await store.WriteAsync(reservedPath, record, cancellationToken);
                if (writeResult.Error != null)
                {
                    recordScope.DeleteFile(reservedFileName);
                    _logger.LogWarning(
                        "Upsert write failed: id={Id} file=\"{File}\" errorReason={ErrorReason} errorPersistence={ErrorPersistence}",
                        record.Id,
                        reservedFileName,
                        writeResult.Error.Reason,
                        writeResult.Error.Persistence);
                    return new OperationResult(writeResult.Error, reservedFileName);
                }

                if (!recordScope.CommitReservedFileName(reservedFileName, writeResult.Fingerprint!.Value, record))
                {
                    // Impossible scenario since the file is reserved
                    throw new InvalidOperationException($"Failed to commit reserved file name for record id '{record.Id}'.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                recordScope.DeleteFile(reservedFileName);
                throw;
            }
            catch (Exception)
            {
                // Just in case of unexpected exceptions
                recordScope.DeleteFile(reservedFileName);
                throw;
            }

            _logger.LogDebug("Upsert applied to reserved file: id={Id} file=\"{File}\"", record.Id, reservedFileName);
            return new OperationResult(null, reservedFileName);
        }

        throw new InvalidOperationException(
            $"Unable to reserve a file name for record id '{record.Id}' after {_maxFileNameReserveAttempts} attempts.");
    }

    private IEnumerator<string> CreateFileNameIterator(TRecord record)
    {
        try
        {
            return context.FileNameGenerator(record).GetEnumerator();
        }
        catch (Exception ex)
        {
            throw new FileNameGenerationException(
                $"Failed to start file-name generation for record id '{record.Id}'.", ex);
        }
    }

    private static bool MoveNextFileNameCandidate(TKey id, IEnumerator<string> iterator)
    {
        try
        {
            return iterator.MoveNext();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNameGenerationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FileNameGenerationException(
                $"Failed to generate file name for record id '{id}'.", ex);
        }
    }

    private static string BuildFileName(TKey id, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new FileNameGenerationException(
                $"File-name generator produced an empty candidate for record id '{id}'.");
        }

        if (candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new FileNameGenerationException(
                $"File-name generator produced a candidate with invalid file-name characters for record id '{id}'.");
        }

        const string extension = ".json";
        const int maxFileNameLength = 255;
        if (candidate.Length > maxFileNameLength - extension.Length)
        {
            throw new FileNameGenerationException(
                $"File-name generator produced a candidate that is too long for record id '{id}'.");
        }

        return candidate + extension;
    }
}

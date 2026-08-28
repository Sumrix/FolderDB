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

    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileOperationException">
    /// The record file could not be read.
    ///
    /// -or-
    ///
    /// Its content could not be decoded.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public async Task<TRecord?> GetAsync(TKey id, CancellationToken cancellationToken = default)
    {
        var result = await TryGetAsync(id, cancellationToken);
        ThrowIfFailed(result);

        return result.Record;
    }

    /// <exception cref="ArgumentNullException">
    /// <paramref name="record"/> is <see langword="null"/>.
    ///
    /// -or-
    ///
    /// The <see cref="IRecord{TKey}.Id"/> of the provided <paramref name="record"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="FileNameGenerationException">
    /// The configured <see cref="TableOptions{TKey, TRecord}.FileNameGenerator"/> threw.
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
    /// <see cref="DatabaseOptions.MaxFileNameReserveAttempts"/> attempts.
    /// </exception>
    /// <exception cref="FileOperationException">
    /// The record could not be encoded.
    ///
    /// -or-
    ///
    /// The record file could not be written.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public async Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        var result = await TryUpsertAsync(record, cancellationToken);
        ThrowIfFailed(result);
    }

    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileOperationException">The record file could not be deleted.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public async Task DeleteAsync(TKey id, CancellationToken cancellationToken = default)
    {
        var result = await TryDeleteAsync(id, cancellationToken);
        ThrowIfFailed(result);
    }

    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public async Task<ReadResult<TRecord>> TryGetAsync(TKey id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

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

    /// <exception cref="ArgumentNullException">
    /// <paramref name="record"/> is <see langword="null"/>.
    ///
    /// -or-
    ///
    /// The <see cref="IRecord{TKey}.Id"/> of the provided <paramref name="record"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="FileNameGenerationException">
    /// The configured <see cref="TableOptions{TKey, TRecord}.FileNameGenerator"/> threw.
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
    /// <see cref="DatabaseOptions.MaxFileNameReserveAttempts"/> attempts.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public async Task<OperationResult> TryUpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(record.Id);

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

    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public async Task<OperationResult> TryDeleteAsync(TKey id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

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

    /// <exception cref="FileOperationException">The operation failed with a file error.</exception>
    private static void ThrowIfFailed(OperationResult result)
    {
        if (!result.IsSuccess)
        {
            throw new FileOperationException(
                $"File operation failed: reason={result.Error.Reason} persistence={result.Error.Persistence} file=\"{result.FileName}\"",
                result.Error.Reason,
                result.Error.Persistence,
                result.Error.Exception);
        }
    }

    /// <exception cref="FileNameGenerationException">
    /// The configured <see cref="TableOptions{TKey, TRecord}.FileNameGenerator"/> threw.
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
    /// <see cref="DatabaseOptions.MaxFileNameReserveAttempts"/> attempts.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
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
                throw new FileNameGenerationException($"Unable to reserve a file name for record id '{record.Id}'.");
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

        throw new FileNameGenerationException(
            $"Unable to reserve a file name for record id '{record.Id}' after {_maxFileNameReserveAttempts} attempts.");
    }

    /// <exception cref="FileNameGenerationException">
    /// The configured <see cref="TableOptions{TKey, TRecord}.FileNameGenerator"/> threw while
    /// starting generation.
    /// </exception>
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

    /// <exception cref="FileNameGenerationException">
    /// The configured <see cref="TableOptions{TKey, TRecord}.FileNameGenerator"/> threw while
    /// producing the next candidate.
    /// </exception>
    private static bool MoveNextFileNameCandidate(TKey id, IEnumerator<string> iterator)
    {
        try
        {
            return iterator.MoveNext();
        }
        catch (Exception ex)
        {
            throw new FileNameGenerationException(
                $"Failed to generate file name for record id '{id}'.", ex);
        }
    }

    /// <exception cref="FileNameGenerationException">
    /// The candidate is <see langword="null"/>, empty, or consists only of white-space characters
    ///
    /// -or-
    ///
    /// The candidate contains one or more of the invalid characters defined in
    /// <see cref="Path.GetInvalidFileNameChars"/>.
    ///
    /// -or-
    ///
    /// The candidate is too long for a file name.
    /// </exception>
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

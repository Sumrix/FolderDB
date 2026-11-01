using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Encoding;
using FolderDB.FileStorage;
using FolderDB.Indexing.Scopes;
using FolderDB.Indexing.State;
using FolderDB.Infrastructure.Helpers;
using FolderDB.Infrastructure.Logging;
using FolderDB.Infrastructure.Primitives;
using FolderDB.Retry;
using FolderDB.Runtime;
using Microsoft.Extensions.Logging;

namespace FolderDB.Indexing.Reconciliation;

internal class FileReconciler<TKey, TRecord, TProjection>(
    CollectionContext<TKey, TRecord, TProjection> context,
    IFileStore fileStore,
    RecordStore<TKey, TRecord> recordStore,
    CollectionIndex<TKey, TRecord, TProjection> index,
    ILogger<FileReconciler<TKey, TRecord, TProjection>> logger)
    where TKey : notnull
    where TRecord : class, IRecord<TKey>
{
    private readonly IndexDecisionMaker<TKey, TRecord, TProjection> _indexDecisionMaker =
        new(context.KeyEqualityComparer);

    private readonly FileUpdateDecisionMaker<TKey, TRecord, TProjection>? _fileUpdateDecisionMaker =
        context.RecordCodec.CurrentSchemaVersion is { } currentSchemaVersion
            ? new(currentSchemaVersion)
            : null;

    private static readonly IndexDecisionExecutor<TKey, TRecord, TProjection> _indexDecisionExecutor = new();

    private readonly FileUpdateDecisionExecutor<TKey, TRecord, TProjection> _fileUpdateDecisionExecutor =
        new(recordStore);

    private static readonly RetryDecisionMaker _retryDecisionMaker = new();

    public async Task<RetryDecision> ReconcileAsync(string path, CancellationToken cancellationToken)
    {
        using var _ = logger.BeginMethodScope();
        var stopwatch = Stopwatch.StartNew();
        var fileName = Path.GetFileName(path);

        logger.LogDebug("Started: file=\"{File}\"", fileName);

        try
        {
            var outcome = await ReconcileCoreAsync(path, cancellationToken);

            LogOutcome(outcome, fileName, stopwatch.ElapsedMilliseconds);

            return outcome.Retry;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Reconciliation failed, skipped: file=\"{File}\" durationMs={DurationMs}",
                fileName,
                stopwatch.ElapsedMilliseconds);

            return RetryDecision.Complete;
        }
    }

    public async Task<RetryDecision> ContinueAfterReadAsync(
        string path,
        SharedIndexScope<TKey, TRecord, TProjection> sharedIndexScope,
        RecordScope<TKey, TRecord, TProjection> heldScope,
        FileReadResult<RecordDecodeResult<TRecord>> readResult,
        CancellationToken cancellationToken = default)
    {
        using var _ = logger.BeginMethodScope();
        var stopwatch = Stopwatch.StartNew();
        var fileName = Path.GetFileName(path);

        logger.LogDebug(
            "Started: file=\"{File}\" heldId={HeldId}",
            fileName,
            heldScope.Id);

        try
        {
            var outcome = await ContinueAfterReadCoreAsync(path, sharedIndexScope, heldScope, readResult, cancellationToken);

            LogOutcome(outcome, fileName, stopwatch.ElapsedMilliseconds);

            return outcome.Retry;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Reconciliation failed, skipped: file=\"{File}\" durationMs={DurationMs}",
                fileName,
                stopwatch.ElapsedMilliseconds);

            return RetryDecision.Complete;
        }
    }

    private async Task<ReconciliationOutcome> ReconcileCoreAsync(string path, CancellationToken cancellationToken)
    {
        using var sharedIndexScope = await index.EnterSharedScopeAsync(cancellationToken);

        var firstPass = await DecideAsync(path, sharedIndexScope, null, cancellationToken);
        if (firstPass.IsComplete)
        {
            return CreateOutcome(firstPass);
        }

        var fileName = Path.GetFileName(path);

        logger.LogTrace(
            "Acquiring locks: file=\"{File}\" indexedId={IndexedId} diskId={DiskId}",
            fileName,
            firstPass.IndexedId.ToObject(),
            firstPass.DiskId.ToObject());

        var lockWaitStart = Stopwatch.GetTimestamp();
        using var scopes = await sharedIndexScope.LockRecordsAsync(firstPass.IndexedId, firstPass.DiskId, cancellationToken);

        logger.LogTrace(
            "Locks acquired: file=\"{File}\" indexedId={IndexedId} diskId={DiskId} waitMs={WaitMs}",
            fileName,
            firstPass.IndexedId.ToObject(),
            firstPass.DiskId.ToObject(),
            Math.Round(Stopwatch.GetElapsedTime(lockWaitStart).TotalMilliseconds, 3));

        var secondPass = await DecideAsync(path, sharedIndexScope, firstPass.ReadResult, cancellationToken);
        return await ExecutePassAsync(
            path,
            secondPass,
            scopes.First,
            scopes.Second,
            executeFileUpdateDecision: true,
            cancellationToken);
    }

    private Task<ReconciliationOutcome> ContinueAfterReadCoreAsync(
        string path,
        SharedIndexScope<TKey, TRecord, TProjection> sharedIndexScope,
        RecordScope<TKey, TRecord, TProjection> heldScope,
        FileReadResult<RecordDecodeResult<TRecord>> readResult,
        CancellationToken cancellationToken = default)
    {
        var fileName = Path.GetFileName(path);
        var indexedState = sharedIndexScope.Files.GetValueOrDefault(fileName);

        LogObservedState(readResult.Fingerprint, indexedState, fileName);

        var indexDecision = _indexDecisionMaker.MakePostReadDecision(readResult, indexedState);
        var fileUpdateIntent = _fileUpdateDecisionMaker?.MakePostReadIntent(readResult);
        var pass = new DecisionPass(
            indexDecision,
            fileUpdateIntent,
            readResult.Fingerprint,
            readResult,
            indexedState);

        return ExecutePassAsync(
            path,
            pass,
            heldScope,
            null,
            executeFileUpdateDecision: false,
            cancellationToken);
    }

    private async Task<ReconciliationOutcome> ExecutePassAsync(
        string path,
        DecisionPass pass,
        RecordScope<TKey, TRecord, TProjection>? firstScope,
        RecordScope<TKey, TRecord, TProjection>? secondScope,
        bool executeFileUpdateDecision,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);

        if (pass.IsComplete)
        {
            return CreateOutcome(pass);
        }

        if (pass.IndexDecision.RequiresRead)
        {
            throw new InvalidOperationException("A read decision cannot reach execution.");
        }

        if (pass.FileUpdateIntent == FileUpdateIntent.ReadFile)
        {
            throw new InvalidOperationException("A file update read intent cannot reach execution.");
        }

        var indexedIdScope = GetScope(firstScope, secondScope, pass.IndexedId);
        var diskIdScope = GetScope(firstScope, secondScope, pass.DiskId);

        // Execute the indexed id part of the index reconciliation decision
        var indexedIdPart = pass.IndexDecision.IndexedIdPart;
        if (indexedIdPart != IndexMutation.None && indexedIdScope is null)
        {
            return CreateOutcome(pass, idLockMismatch: true);
        }

        _indexDecisionExecutor.Execute(
            indexedIdPart,
            fileName,
            pass.Fingerprint,
            pass.ReadResult,
            indexedIdScope);

        // Execute the disk id part of the index reconciliation decision and the FileUpdateIntent
        var diskIdPart = pass.IndexDecision.DiskIdPart;
        var diskIdLockRequired =
            diskIdPart != IndexMutation.None ||
            pass.FileUpdateIntent == FileUpdateIntent.UpdateIfCurrentFile;
        if (diskIdLockRequired && diskIdScope is null)
        {
            return CreateOutcome(pass, idLockMismatch: true);
        }

        var diskIdResult = _indexDecisionExecutor.Execute(
            diskIdPart,
            fileName,
            pass.Fingerprint,
            pass.ReadResult,
            diskIdScope);
        if (diskIdResult == IndexDecisionExecutionResult.IdLockMismatch)
        {
            return CreateOutcome(pass, idLockMismatch: true);
        }

        // Resolve FileUpdateIntent against the recalculated CurrentFileName and update the file
        if (pass.FileUpdateIntent is null or FileUpdateIntent.DoNothing)
        {
            return CreateOutcome(pass);
        }

        var currentFile = diskIdScope!.TryGetState(out var recordState) &&
                          PathHelper.OSDependedPathComparer.Equals(recordState.CurrentFileName, fileName);
        var fileUpdateDecision = _fileUpdateDecisionMaker!.MakeDecision(pass.FileUpdateIntent.Value, currentFile);
        if (fileUpdateDecision == FileUpdateDecision.DoNothing)
        {
            return CreateOutcome(pass, fileUpdate: fileUpdateDecision);
        }

        if (!executeFileUpdateDecision)
        {
            return new(pass, RetryDecision.RetryWithMinBackoff, fileUpdateDecision);
        }

        var writeError = await _fileUpdateDecisionExecutor.ExecuteAsync(
            fileUpdateDecision,
            path,
            fileName,
            pass.ReadResult,
            diskIdScope,
            cancellationToken);

        if (writeError is null)
        {
            logger.LogDebug(
                "File updated: file=\"{File}\" sourceSchemaVersion={SourceSchemaVersion} targetSchemaVersion={TargetSchemaVersion}",
                fileName,
                pass.ReadResult.Value.SourceSchemaVersion,
                pass.ReadResult.Value.TargetSchemaVersion);
        }
        else
        {
            logger.LogWarning(
                "File update failed: file=\"{File}\" errorReason={ErrorReason} errorPersistence={ErrorPersistence}",
                fileName,
                writeError.Reason,
                writeError.Persistence);
        }

        return CreateOutcome(pass, fileUpdate: fileUpdateDecision, writeError: writeError);
    }

    private async Task<DecisionPass> DecideAsync(
        string path,
        SharedIndexScope<TKey, TRecord, TProjection> sharedIndexScope,
        FileReadResult<RecordDecodeResult<TRecord>>? readCache,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        var fingerprint = fileStore.GetFileFingerprint(path);
        var indexedState = sharedIndexScope.Files.GetValueOrDefault(fileName);

        LogObservedState(fingerprint, indexedState, fileName);

        var indexDecision = _indexDecisionMaker.MakePreReadDecision(fingerprint, indexedState);
        var fileUpdateIntent = _fileUpdateDecisionMaker?.MakePreReadIntent(fileName, fingerprint, indexedState);

        if (!indexDecision.RequiresRead && fileUpdateIntent != FileUpdateIntent.ReadFile)
        {
            return new(indexDecision, fileUpdateIntent, fingerprint, readCache ?? default, indexedState);
        }

        var readResult = await ReadFile(path, readCache, fingerprint, fileName, cancellationToken);

        indexDecision = _indexDecisionMaker.MakePostReadDecision(readResult, indexedState);
        fileUpdateIntent = _fileUpdateDecisionMaker?.MakePostReadIntent(readResult);

        return new(indexDecision, fileUpdateIntent, readResult.Fingerprint, readResult, indexedState);
    }

    private async Task<FileReadResult<RecordDecodeResult<TRecord>>> ReadFile(
        string path,
        FileReadResult<RecordDecodeResult<TRecord>>? readCache,
        FileFingerprint fingerprint,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (readCache is not null &&
            readCache.Value.Fingerprint == fingerprint)
        {
            return readCache.Value;
        }

        var readResult = await recordStore.ReadAsync(path, cancellationToken);

        LogReadResult(readResult, fileName);

        return readResult;
    }

    private void LogObservedState(
        FileFingerprint fingerprint,
        IReadOnlyFileIndexState<TKey, TProjection>? indexedState,
        string fileName)
    {
        logger.LogTrace(
            "Observed: file=\"{File}\" fingerprint=\"{Fingerprint}\" indexedId={IndexedId} indexedFingerprint=\"{IndexedFingerprint}\" indexedStatus={IndexedStatus} indexedSchemaVersion={IndexedSchemaVersion} indexedErrorReason={IndexedErrorReason}",
            fileName,
            fingerprint,
            indexedState is not null ? indexedState.Record.Id : null,
            indexedState?.Fingerprint,
            indexedState?.Status,
            indexedState?.SchemaVersion,
            indexedState?.ErrorInfo?.Reason);
    }

    private void LogReadResult(FileReadResult<RecordDecodeResult<TRecord>> readResult, string fileName)
    {
        logger.LogTrace(
            "File read: file=\"{File}\" fingerprint=\"{Fingerprint}\" diskId={DiskId} sourceSchemaVersion={SourceSchemaVersion} targetSchemaVersion={TargetSchemaVersion} errorReason={ErrorReason} errorPersistence={ErrorPersistence}",
            fileName,
            readResult.Fingerprint,
            readResult.IsSuccess ? readResult.Value.Record.Id : null,
            readResult.IsSuccess ? readResult.Value.SourceSchemaVersion : null,
            readResult.IsSuccess ? readResult.Value.TargetSchemaVersion : null,
            readResult.Error?.Reason,
            readResult.Error?.Persistence);
    }

    private static ReconciliationOutcome CreateOutcome(
        DecisionPass pass,
        bool idLockMismatch = false,
        FileUpdateDecision? fileUpdate = null,
        FileError? writeError = null)
    {
        return new(
            pass,
            _retryDecisionMaker.MakeDecision(writeError ?? pass.ReadResult.Error, idLockMismatch),
            fileUpdate,
            idLockMismatch);
    }

    private void LogOutcome(ReconciliationOutcome outcome, string fileName, long durationMs)
    {
        logger.LogDebug(
            "Finished: file=\"{File}\" indexedId={IndexedId} diskId={DiskId} fingerprint=\"{Fingerprint}\" indexReconciliation={Decision} fileUpdate={FileUpdate} retry={RetryDecision} idLockMismatch={IdLockMismatch} durationMs={DurationMs}",
            fileName,
            outcome.IndexedId.ToObject(),
            outcome.DiskId.ToObject(),
            outcome.Fingerprint,
            outcome.Decision,
            outcome.FileUpdate ?? FileUpdateDecision.DoNothing,
            outcome.Retry,
            outcome.IdLockMismatch,
            durationMs);
    }

    private RecordScope<TKey, TRecord, TProjection>? GetScope(
        RecordScope<TKey, TRecord, TProjection>? firstScope,
        RecordScope<TKey, TRecord, TProjection>? secondScope,
        Option<TKey> id)
    {
        if (id.IsNone)
        {
            return null;
        }

        if (firstScope != null && context.KeyEqualityComparer.Equals(firstScope.Id, id.Value))
        {
            return firstScope;
        }

        return secondScope != null && context.KeyEqualityComparer.Equals(secondScope.Id, id.Value)
            ? secondScope
            : null;
    }

    private record DecisionPass(
        FileReconciliationDecision IndexDecision,
        FileUpdateIntent? FileUpdateIntent,
        FileFingerprint Fingerprint,
        FileReadResult<RecordDecodeResult<TRecord>> ReadResult,
        IReadOnlyFileIndexState<TKey, TProjection>? IndexedState)
    {
        public Option<TKey> IndexedId { get; } = IndexedState is not null
            ? Option<TKey>.Some(IndexedState.Record.Id)
            : Option<TKey>.None;

        public Option<TKey> DiskId { get; } = ReadResult.IsSuccess
            ? Option<TKey>.Some(ReadResult.Value.Record.Id)
            : Option<TKey>.None;

        public bool IsComplete =>
            IndexDecision == FileReconciliationDecision.Skip &&
            FileUpdateIntent is null or FolderDB.Indexing.Reconciliation.FileUpdateIntent.DoNothing;
    }

    private class ReconciliationOutcome(
        DecisionPass pass,
        RetryDecision retryDecision,
        FileUpdateDecision? fileUpdate = null,
        bool idLockMismatch = false)
    {
        public RetryDecision Retry => retryDecision;
        public Option<TKey> IndexedId => pass.IndexedId;
        public Option<TKey> DiskId => pass.DiskId;
        public FileFingerprint Fingerprint => pass.Fingerprint;
        public FileReconciliationDecision Decision => pass.IndexDecision;
        public FileUpdateDecision? FileUpdate => fileUpdate;
        public bool IdLockMismatch => idLockMismatch;
    }
}

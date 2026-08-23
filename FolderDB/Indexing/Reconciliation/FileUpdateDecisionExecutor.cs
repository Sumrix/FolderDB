using System;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Encoding;
using FolderDB.FileStorage;
using FolderDB.Indexing.Scopes;
using FolderDB.Indexing.State;

namespace FolderDB.Indexing.Reconciliation;

internal class FileUpdateDecisionExecutor<TKey, TRecord, TProjection>(
    RecordStore<TKey, TRecord> recordStore)
    where TKey : notnull
{
    public async Task<FileError?> ExecuteAsync(
        FileUpdateDecision decision,
        string path,
        string fileName,
        FileReadResult<RecordDecodeResult<TRecord>> readResult,
        RecordScope<TKey, TRecord, TProjection> fileRecordScope,
        CancellationToken cancellationToken)
    {
        if (decision == FileUpdateDecision.DoNothing)
        {
            return null;
        }

        if (decision != FileUpdateDecision.UpdateFile)
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, null);
        }

        var writeResult = await recordStore.WriteAsync(path, readResult.Value.Record, cancellationToken);
        if (!writeResult.IsSuccess)
        {
            return writeResult.Error;
        }

        var result = fileRecordScope.Upsert(
            fileName,
            writeResult.Fingerprint.Value,
            readResult.Value.TargetSchemaVersion,
            readResult.Value.Record);
        return result switch
        {
            IndexOperationResult.Applied => null,
            IndexOperationResult.NoChanges =>
                throw new InvalidOperationException("File update result did not apply to the index."),
            IndexOperationResult.BlockedByAnotherId =>
                throw new InvalidOperationException("File update result belongs to another id."),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null)
        };
    }
}
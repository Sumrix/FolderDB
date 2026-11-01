using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.FileStorage;
using FolderDB.Indexing.State;
using FolderDB.Infrastructure.Concurrency;
using Nito.AsyncEx;

namespace FolderDB.Indexing;

/// <summary>
/// Defines low-level operations over the in-memory collection index.
/// Mutating operations are expected to be called from index scopes while the related record locks are held.
/// </summary>
public interface IRecordScopedIndexEngine<TKey, in TRecord, TProjection> : IAsyncDisposable
    where TKey : notnull
{
    IReadOnlyDictionary<TKey, IReadOnlyRecordIndexState<TKey, TProjection>> Records { get; }
    IReadOnlyDictionary<string, IReadOnlyFileIndexState<TKey, TProjection>> Files { get; }

    AsyncReaderWriterLock Barrier { get; }

    StripedAsyncLock<TKey> IdLocks { get; }

    IndexOperationResult Upsert(
        TKey id,
        string fileName,
        FileFingerprint fingerprint,
        int? schemaVersion,
        TRecord record);

    IndexOperationResult Upsert(
        TKey id,
        string fileName,
        FileFingerprint fingerprint,
        FileErrorInfo errorInfo);

    bool TryReserveFileName(TKey id, string fileName);

    bool CommitReservedFileName(TKey id, string fileName, FileFingerprint fingerprint, TRecord record);

    bool Delete(TKey id);

    IndexOperationResult DeleteFile(TKey id, string fileName);

    void Clear();

    Task FlushAsync(CancellationToken cancellationToken = default);
}

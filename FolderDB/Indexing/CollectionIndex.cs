using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Indexing.Scopes;
using FolderDB.Indexing.State;
using FolderDB.Runtime;

namespace FolderDB.Indexing;

public class CollectionIndex<TKey, TRecord, TProjection>(
    IRecordScopedIndexEngine<TKey, TRecord, TProjection> engine,
    CollectionContext<TKey, TRecord, TProjection> context) : IAsyncDisposable
    where TKey : notnull
{
    public IReadOnlyDictionary<TKey, IReadOnlyRecordIndexState<TKey, TProjection>> Records => engine.Records;

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public async Task<SharedIndexScope<TKey, TRecord, TProjection>> EnterSharedScopeAsync(CancellationToken cancellationToken = default)
    {
        var readerLock = await engine.Barrier.ReaderLockAsync(cancellationToken);
        return new(engine, context, readerLock);
    }

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public async Task<ExclusiveIndexScope<TKey, TRecord, TProjection>> EnterExclusiveScopeAsync(CancellationToken cancellationToken = default)
    {
        var writerLock = await engine.Barrier.WriterLockAsync(cancellationToken);
        return new(engine, writerLock);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        return engine.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await engine.DisposeAsync();
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Indexing.State;
using FolderDB.Infrastructure.Helpers;
using FolderDB.Infrastructure.Primitives;
using FolderDB.Runtime;

namespace FolderDB.Indexing.Scopes;

public class SharedIndexScope<TKey, TRecord, TProjection>(
    IRecordScopedIndexEngine<TKey, TRecord, TProjection> engine,
    TableContext<TKey, TRecord, TProjection> context,
    IDisposable readerLock) : IDisposable
    where TKey : notnull
{
    public IReadOnlyDictionary<TKey, IReadOnlyRecordIndexState<TKey, TProjection>> Records => engine.Records;
    public IReadOnlyDictionary<string, IReadOnlyFileIndexState<TKey, TProjection>> Files => engine.Files;

    public async Task<RecordScope<TKey, TRecord, TProjection>> LockRecordAsync(
        TKey id,
        CancellationToken cancellationToken = default)
    {
        var idLock = await engine.IdLocks.LockAsync(id, cancellationToken);
        return new RecordScope<TKey, TRecord, TProjection>(engine, idLock, id);
    }

    public async Task<RecordScopePair<TKey, TRecord, TProjection>> LockRecordsAsync(
        Option<TKey> firstId,
        Option<TKey> secondId,
        CancellationToken cancellationToken = default)
    {
        switch (firstId.HasValue, secondId.HasValue)
        {
            case (false, false):
                return new(null, null);

            case (true, false):
                return new(await LockRecordAsync(firstId.Value!, cancellationToken), null);

            case (false, true):
                return new(null, await LockRecordAsync(secondId.Value!, cancellationToken));

            case (true, true):
                if (context.KeyEqualityComparer.Equals(firstId.Value!, secondId.Value!))
                {
                    var scope = await LockRecordAsync(firstId.Value!, cancellationToken);
                    return new(scope, scope);
                }

                RecordScope<TKey, TRecord, TProjection>? lowerScope = null;
                RecordScope<TKey, TRecord, TProjection>? higherScope = null;
                try
                {
                    var firstIdIsLower = context.KeyComparer.Compare(firstId.Value!, secondId.Value!) <= 0;
                    var (lowerId, higherId) = firstIdIsLower
                        ? (firstId.Value!, secondId.Value!)
                        : (secondId.Value!, firstId.Value!);
                    lowerScope = await LockRecordAsync(lowerId, cancellationToken);
                    higherScope = await LockRecordAsync(higherId, cancellationToken);

                    return firstIdIsLower
                        ? new(lowerScope, higherScope)
                        : new RecordScopePair<TKey, TRecord, TProjection>(higherScope, lowerScope);
                }
                catch
                {
                    DisposeHelper.SafeDispose(lowerScope);
                    DisposeHelper.SafeDispose(higherScope);
                    throw;
                }
        }
    }

    public void Dispose()
    {
        readerLock.Dispose();
    }
}

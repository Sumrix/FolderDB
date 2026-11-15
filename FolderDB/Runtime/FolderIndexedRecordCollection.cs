using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Infrastructure.Helpers;

namespace FolderDB.Runtime;

public class FolderIndexedRecordCollection<TKey, TRecord, TProjection> :
    IFolderIndexedRecordCollection<TKey, TRecord, TProjection>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    private readonly StoreContext _context;
    private readonly CollectionEngine<TKey, TRecord, TProjection> _engine;

    private int _disposed;

    public string RootPath { get; }

    public IReadOnlyDictionary<TKey, TProjection> Projections
    {
        get
        {
            ThrowIfDisposed();
            return _engine.Projections;
        }
    }

    public IReadOnlyDictionary<TKey, IndexedFile<TProjection>> Files
    {
        get
        {
            ThrowIfDisposed();
            return _engine.Files;
        }
    }

    internal FolderIndexedRecordCollection(
        string rootPath,
        StoreContext context,
        CollectionEngine<TKey, TRecord, TProjection> engine)
    {
        RootPath = rootPath;
        _context = context;
        _engine = engine;
    }

    public Task<TRecord?> GetAsync(TKey id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _engine.GetAsync(id, cancellationToken);
    }

    public Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _engine.UpsertAsync(record, cancellationToken);
    }

    public Task DeleteAsync(TKey id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _engine.DeleteAsync(id, cancellationToken);
    }

    public Task<ReadResult<TRecord>> TryGetAsync(TKey id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _engine.TryGetAsync(id, cancellationToken);
    }

    public Task<OperationResult> TryUpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _engine.TryUpsertAsync(record, cancellationToken);
    }

    public Task<OperationResult> TryDeleteAsync(TKey id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _engine.TryDeleteAsync(id, cancellationToken);
    }

    public Task FlushIndexAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _engine.FlushAsync(cancellationToken);
    }

    public void RequestRescan()
    {
        ThrowIfDisposed();
        _engine.RequestDirectoryReconcile();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await DisposeHelper.SafeDispose(_engine);

        _context.Dispose();

        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
    }
}

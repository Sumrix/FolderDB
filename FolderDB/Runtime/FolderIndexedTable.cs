using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Infrastructure.Helpers;

namespace FolderDB.Runtime;

public class FolderIndexedTable<TKey, TRecord, TProjection> :
    IFolderIndexedTable<TKey, TRecord, TProjection>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    private readonly DatabaseResources _resources;
    private readonly TableEngine<TKey, TRecord, TProjection> _engine;

    private bool _disposed;

    public string RootPath { get; }

    public IReadOnlyDictionary<TKey, TProjection> Index
    {
        get
        {
            ThrowIfDisposed();
            return _engine.Index;
        }
    }

    public IReadOnlyDictionary<TKey, IndexEntry<TProjection>> Entries
    {
        get
        {
            ThrowIfDisposed();
            return _engine.Entries;
        }
    }

    internal FolderIndexedTable(
        string rootPath,
        DatabaseResources resources,
        TableEngine<TKey, TRecord, TProjection> engine)
    {
        RootPath = rootPath;
        _resources = resources;
        _engine = engine;
    }

    public Task<TRecord?> GetAsync(TKey id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _engine.GetAsync(id, ct);
    }

    public Task UpsertAsync(TRecord record, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _engine.UpsertAsync(record, ct);
    }

    public Task DeleteAsync(TKey id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _engine.DeleteAsync(id, ct);
    }

    public Task<ReadResult<TRecord>> TryGetAsync(TKey id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _engine.TryGetAsync(id, ct);
    }

    public Task<OperationResult> TryUpsertAsync(TRecord record, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _engine.TryUpsertAsync(record, ct);
    }

    public Task<OperationResult> TryDeleteAsync(TKey id, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _engine.TryDeleteAsync(id, ct);
    }

    public Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _engine.FlushAsync(ct);
    }

    public void RequestRescan()
    {
        ThrowIfDisposed();
        _engine.RequestDirectoryReconcile();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, true))
            return;

        await DisposeHelper.SafeDispose(_engine);

        _resources.Dispose();

        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

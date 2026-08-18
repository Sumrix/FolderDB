using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.FileStorage;
using FolderDB.Infrastructure.Helpers;
using FolderDB.Infrastructure.Watching;
using FolderDB.Retry;
using FolderDB.Runtime;
using Microsoft.Extensions.Logging;

namespace FolderDB;

public class FolderDatabase : IDatabase
{
    private readonly DatabaseResources _resources;
    private readonly IReadOnlyDictionary<Type, ITableEngine> _tableEnginesByType;
    private readonly IReadOnlyDictionary<string, ITableEngine> _tableEnginesByDirectoryName;
    private readonly PathWatcher _rootPathWatcher;

    private bool _disposed;

    public string RootPath { get; }

    // Internal because a FolderDatabase exists only as a running database.
    // Making it public would allow creating one in a non-running state.
    internal FolderDatabase(
        string rootPath,
        DatabaseResources resources,
        IReadOnlyDictionary<Type, ITableEngine> tableEnginesByType,
        IReadOnlyDictionary<string, ITableEngine> tableEnginesByDirectoryName,
        PathWatcher rootPathWatcher)
    {
        RootPath = rootPath;
        _resources = resources;
        _tableEnginesByType = tableEnginesByType;
        _tableEnginesByDirectoryName = tableEnginesByDirectoryName;
        _rootPathWatcher = rootPathWatcher;

        // Wired last: the handlers read the fields above, and enabling the watcher can raise an
        // event before this constructor returns.
        _rootPathWatcher.Changed += (_, path) => HandleRootDirectoryChange(path);
        _rootPathWatcher.Error += (_, _) => RequestAllDirectoriesReconcile();
        _rootPathWatcher.EnableRaisingEvents = true;
    }

    /// <exception cref="ArgumentNullException">The path is null.</exception>
    /// <exception cref="ArgumentException">The system could not retrieve the absolute path.</exception>
    /// <exception cref="SecurityException">The caller does not have the required permissions.</exception>
    /// <exception cref="NotSupportedException">The path contains a format that is not supported.</exception>
    /// <exception cref="PathTooLongException">The specified path, file name, or both exceed the system-defined maximum length.</exception>
    public static Task<FolderDatabase> StartAsync(
        string path,
        IReadOnlyList<ITableDefinition> tableDefinitions,
        DatabaseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return DatabaseFactory.StartDatabaseAsync(path, tableDefinitions, options, cancellationToken);
    }

    public static async Task<IFolderTable<TKey, TRecord>> StartTableAsync<TKey, TRecord>(
        string path,
        TableDefinition<TKey, TRecord, NoProjection> definition,
        DatabaseOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return await DatabaseFactory.StartTableAsync(path, definition, options, cancellationToken);
    }

    public static async Task<IFolderIndexedTable<TKey, TRecord, TProjection>> StartIndexedTableAsync<TKey, TRecord, TProjection>(
        string path,
        TableDefinition<TKey, TRecord, TProjection> definition,
        DatabaseOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return await DatabaseFactory.StartTableAsync(path, definition, options, cancellationToken);
    }

    public static IRetryScheduler<string> CreateDefaultRetryScheduler(
        DefaultRetrySchedulerOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        return DatabaseFactory.CreateDefaultRetryScheduler(options, loggerFactory);
    }

    public static IFileStore CreateDefaultRetryFileStore(
        IFileStore inner,
        RetryFileStoreOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        return new RetryFileStore(
            inner,
            options,
            loggerFactory?.CreateLogger<RetryFileStore>());
    }

    public ITable<TKey, TRecord> Table<TKey, TRecord>()
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return GetTable<TRecord, ITable<TKey, TRecord>>();
    }

    public IIndexedTable<TKey, TRecord, TProjection> IndexedTable<TKey, TRecord, TProjection>()
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return GetTable<TRecord, IIndexedTable<TKey, TRecord, TProjection>>();
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        foreach (var tableEngine in _tableEnginesByType.Values)
        {
            ct.ThrowIfCancellationRequested();
            await tableEngine.FlushAsync(ct);
        }
    }

    public void RequestRescan()
    {
        ThrowIfDisposed();
        RequestAllDirectoriesReconcile();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, true))
            return;

        DisposeHelper.SafeDispose(_rootPathWatcher);

        foreach (var tableEngine in _tableEnginesByType.Values)
            await DisposeHelper.SafeDispose(tableEngine);

        _resources.Dispose();

        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private TTable GetTable<TRecord, TTable>()
        where TTable : class
    {
        ThrowIfDisposed();

        if (!_tableEnginesByType.TryGetValue(typeof(TRecord), out var tableEngine))
        {
            throw new InvalidOperationException(
                $"No table is defined for record type '{typeof(TRecord)}'. " +
                $"Pass its table definition to {nameof(FolderDatabase)}.{nameof(StartAsync)}.");
        }

        if (tableEngine is not TTable table)
        {
            throw new InvalidOperationException(
                $"The table for record type '{typeof(TRecord)}' cannot be accessed as '{typeof(TTable)}'. " +
                "The requested key or projection type differs from the one the table was defined with.");
        }

        return table;
    }

    private void HandleRootDirectoryChange(string path)
    {
        var directoryName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(directoryName))
            return;

        if (_tableEnginesByDirectoryName.TryGetValue(directoryName, out var tableEngine))
        {
            tableEngine.RequestDirectoryReconcile();
        }
    }

    private void RequestAllDirectoriesReconcile()
    {
        foreach (var tableEngine in _tableEnginesByDirectoryName.Values)
        {
            tableEngine.RequestDirectoryReconcile();
        }
    }
}

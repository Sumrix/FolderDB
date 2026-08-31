using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Infrastructure.Helpers;
using FolderDB.Infrastructure.Watching;
using FolderDB.Runtime;

namespace FolderDB;

/// <summary>
/// Represents a running set of file-backed tables stored under a single root directory.
/// </summary>
public class FolderDatabase : IDatabase
{
    private readonly DatabaseResources _resources;
    private readonly IReadOnlyDictionary<Type, ITableEngine> _tableEnginesByType;
    private readonly IReadOnlyDictionary<string, ITableEngine> _tableEnginesByDirectoryName;
    private readonly PathWatcher _rootPathWatcher;

    private int _disposed;

    /// <inheritdoc/>
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

    /// <summary>
    /// Opens a database with the specified tables at the specified path.
    /// </summary>
    /// <remarks>
    /// Opening takes longer the first time the database is opened in a directory, or after its index cache was discarded.
    /// </remarks>
    /// <param name="path">
    /// The path to the database, specified as a relative or absolute path.
    /// A relative path is interpreted as relative to the current working directory.
    /// </param>
    /// <param name="tableDefinitions">The definitions that describe and configure the tables in the database.</param>
    /// <param name="options">Options to control the behavior of the database.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that returns the opened <see cref="FolderDatabase"/> instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> or <paramref name="tableDefinitions"/> is <see langword="null"/>.
    ///
    /// -or-
    ///
    /// <paramref name="tableDefinitions"/> contains a <see langword="null"/> definition.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is a zero-length string, contains only white space on Windows systems,
    /// or contains one or more of the invalid characters defined in
    /// <see cref="Path.GetInvalidPathChars"/>.
    ///
    /// -or-
    ///
    /// The system could not retrieve the absolute path.
    ///
    /// -or-
    ///
    /// <paramref name="path"/> is prefixed with, or contains, only a colon character (:).
    ///
    /// -or-
    ///
    /// <paramref name="tableDefinitions"/> contains a definition with an invalid name, or two
    /// definitions that collide by name or by record type.
    ///
    /// -or-
    ///
    /// A directory to watch does not exist, or its path has not been set or is invalid.
    /// </exception>
    /// <exception cref="SecurityException">The caller does not have the required permissions.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="path"/> contains a colon character (:) that is not part of a drive label ("C:\").
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="tableDefinitions"/> contains a definition with a <see langword="null"/> value.
    ///
    /// -or-
    ///
    /// A factory configured in <paramref name="options"/> or on <paramref name="tableDefinitions"/> returned
    /// <see langword="null"/>.
    ///
    /// -or-
    ///
    /// A definition provides neither an index persistence nor an index engine factory.
    /// </exception>
    /// <exception cref="IOException">
    /// A directory to create is a file.
    ///
    /// -or-
    ///
    /// The network name is not known.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The caller does not have the required permission.</exception>
    /// <exception cref="DirectoryNotFoundException">
    /// The specified path is invalid (for example, it is on an unmapped drive).
    /// </exception>
    /// <exception cref="PathTooLongException">
    /// The specified path, file name, or both exceed the system-defined maximum length.
    /// </exception>
    /// <exception cref="FileNotFoundException">A directory to watch could not be found.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The current operating system is not Microsoft Windows NT or later.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public static Task<FolderDatabase> OpenAsync(
        string path,
        IReadOnlyList<ITableDefinition> tableDefinitions,
        DatabaseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return DatabaseFactory.OpenDatabaseAsync(path, tableDefinitions, options, cancellationToken);
    }

    /// <summary>
    /// Opens a database as a single table at the specified path.
    /// </summary>
    /// <remarks>
    /// Opening takes longer the first time the database is opened in a directory, or after its index cache was discarded.
    /// </remarks>
    /// <param name="path">
    /// The path to the database, specified as a relative or absolute path.
    /// A relative path is interpreted as relative to the current working directory.
    /// The directory that holds the table is created when missing.
    /// </param>
    /// <param name="definition">The definition that describes and configures the table.</param>
    /// <param name="options">Options to control the behavior of the database.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that returns the opened <see cref="IFolderTable{TKey, TRecord}"/> instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> or <paramref name="definition"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is a zero-length string, contains only white space on Windows systems,
    /// or contains one or more of the invalid characters defined in
    /// <see cref="Path.GetInvalidPathChars"/>.
    ///
    /// -or-
    ///
    /// The system could not retrieve the absolute path.
    ///
    /// -or-
    ///
    /// <paramref name="path"/> is prefixed with, or contains, only a colon character (:).
    ///
    /// -or-
    ///
    /// <paramref name="definition"/> has an invalid name.
    ///
    /// -or-
    ///
    /// A directory to watch does not exist, or its path has not been set or is invalid.
    /// </exception>
    /// <exception cref="SecurityException">The caller does not have the required permissions.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="path"/> contains a colon character (:) that is not part of a drive label ("C:\").
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="definition"/> contains a <see langword="null"/> value.
    ///
    /// -or-
    ///
    /// A factory configured in <paramref name="options"/> or on <paramref name="definition"/> returned
    /// <see langword="null"/>.
    ///
    /// -or-
    ///
    /// <paramref name="definition"/> provides neither an index persistence nor an index engine factory.
    /// </exception>
    /// <exception cref="IOException">
    /// A directory to create is a file.
    ///
    /// -or-
    ///
    /// The network name is not known.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The caller does not have the required permission.</exception>
    /// <exception cref="DirectoryNotFoundException">
    /// The specified path is invalid (for example, it is on an unmapped drive).
    /// </exception>
    /// <exception cref="PathTooLongException">
    /// The specified path, file name, or both exceed the system-defined maximum length.
    /// </exception>
    /// <exception cref="FileNotFoundException">A directory to watch could not be found.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The current operating system is not Microsoft Windows NT or later.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public static async Task<IFolderTable<TKey, TRecord>> OpenTableAsync<TKey, TRecord>(
        string path,
        TableDefinition<TKey, TRecord, NoProjection> definition,
        DatabaseOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return await DatabaseFactory.OpenTableAsync(path, definition, options, cancellationToken);
    }

    /// <summary>
    /// Opens a database as a single indexed table at the specified path.
    /// </summary>
    /// <remarks>
    /// Opening takes longer the first time the database is opened in a directory, or after its index cache was discarded.
    /// </remarks>
    /// <param name="path">
    /// The path to the database, specified as a relative or absolute path.
    /// A relative path is interpreted as relative to the current working directory.
    /// The directory that holds the table is created when missing.
    /// </param>
    /// <param name="definition">The definition that describes and configures the table.</param>
    /// <param name="options">Options to control the behavior of the database.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that returns the opened <see cref="IFolderIndexedTable{TKey, TRecord, TProjection}"/> instance.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> or <paramref name="definition"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is a zero-length string, contains only white space on Windows systems,
    /// or contains one or more of the invalid characters defined in
    /// <see cref="Path.GetInvalidPathChars"/>.
    ///
    /// -or-
    ///
    /// The system could not retrieve the absolute path.
    ///
    /// -or-
    ///
    /// <paramref name="path"/> is prefixed with, or contains, only a colon character (:).
    ///
    /// -or-
    ///
    /// <paramref name="definition"/> has an invalid name.
    ///
    /// -or-
    ///
    /// A directory to watch does not exist, or its path has not been set or is invalid.
    /// </exception>
    /// <exception cref="SecurityException">The caller does not have the required permissions.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="path"/> contains a colon character (:) that is not part of a drive label ("C:\").
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="definition"/> contains a <see langword="null"/> value.
    ///
    /// -or-
    ///
    /// A factory configured in <paramref name="options"/> or on <paramref name="definition"/> returned
    /// <see langword="null"/>.
    ///
    /// -or-
    ///
    /// <paramref name="definition"/> provides neither an index persistence nor an index engine factory.
    /// </exception>
    /// <exception cref="IOException">
    /// A directory to create is a file.
    ///
    /// -or-
    ///
    /// The network name is not known.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The caller does not have the required permission.</exception>
    /// <exception cref="DirectoryNotFoundException">
    /// The specified path is invalid (for example, it is on an unmapped drive).
    /// </exception>
    /// <exception cref="PathTooLongException">
    /// The specified path, file name, or both exceed the system-defined maximum length.
    /// </exception>
    /// <exception cref="FileNotFoundException">A directory to watch could not be found.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// The current operating system is not Microsoft Windows NT or later.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public static async Task<IFolderIndexedTable<TKey, TRecord, TProjection>> OpenIndexedTableAsync<TKey, TRecord, TProjection>(
        string path,
        TableDefinition<TKey, TRecord, TProjection> definition,
        DatabaseOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return await DatabaseFactory.OpenTableAsync(path, definition, options, cancellationToken);
    }

    /// <summary>
    /// Gets the table that stores <typeparamref name="TRecord"/>.
    /// </summary>
    /// <typeparam name="TKey">The type of the record id the table was defined with.</typeparam>
    /// <typeparam name="TRecord">The record type the table stores.</typeparam>
    /// <returns>The table that stores <typeparamref name="TRecord"/>.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// There is no table defined with <typeparamref name="TKey"/> and <typeparamref name="TRecord"/>.
    /// </exception>
    public ITable<TKey, TRecord> Table<TKey, TRecord>()
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return GetTable<TRecord, ITable<TKey, TRecord>>();
    }

    /// <summary>
    /// Gets the table that stores <typeparamref name="TRecord"/>, together with its projection index.
    /// </summary>
    /// <typeparam name="TKey">The type of the record id the table was defined with.</typeparam>
    /// <typeparam name="TRecord">The record type the table stores.</typeparam>
    /// <typeparam name="TProjection">The projection type the table was defined with.</typeparam>
    /// <returns>
    /// The table that stores <typeparamref name="TRecord"/>, together with its projection index.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// There is no table defined with <typeparamref name="TKey"/>, <typeparamref name="TRecord"/>
    /// and <typeparamref name="TProjection"/>.
    /// </exception>
    public IIndexedTable<TKey, TRecord, TProjection> IndexedTable<TKey, TRecord, TProjection>()
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return GetTable<TRecord, IIndexedTable<TKey, TRecord, TProjection>>();
    }

    /// <inheritdoc/>
    public async Task FlushIndexAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        foreach (var tableEngine in _tableEnginesByType.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await tableEngine.FlushAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public void RequestRescan()
    {
        ThrowIfDisposed();
        RequestAllDirectoriesReconcile();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        DisposeHelper.SafeDispose(_rootPathWatcher);

        foreach (var tableEngine in _tableEnginesByType.Values)
            await DisposeHelper.SafeDispose(tableEngine);

        _resources.Dispose();

        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
    }

    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// There is no table for <typeparamref name="TRecord"/>.
    ///
    /// -or-
    ///
    /// The table cannot be accessed as <typeparamref name="TTable"/>.
    /// </exception>
    private TTable GetTable<TRecord, TTable>()
        where TTable : class
    {
        ThrowIfDisposed();

        if (!_tableEnginesByType.TryGetValue(typeof(TRecord), out var tableEngine))
        {
            throw new InvalidOperationException(
                $"No table is defined for record type '{typeof(TRecord)}'. " +
                $"Pass its table definition to {nameof(FolderDatabase)}.{nameof(OpenAsync)}.");
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

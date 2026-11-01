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
/// Represents a running set of file-backed collections stored under a single root directory.
/// </summary>
public class FolderDatabase : IDatabase
{
    private readonly DatabaseResources _resources;
    private readonly IReadOnlyDictionary<Type, ICollectionEngine> _collectionEnginesByType;
    private readonly IReadOnlyDictionary<string, ICollectionEngine> _collectionEnginesByDirectoryName;
    private readonly PathWatcher _rootPathWatcher;

    private int _disposed;

    /// <inheritdoc/>
    public string RootPath { get; }

    // Internal because a FolderDatabase exists only as a running database.
    // Making it public would allow creating one in a non-running state.
    internal FolderDatabase(
        string rootPath,
        DatabaseResources resources,
        IReadOnlyDictionary<Type, ICollectionEngine> collectionEnginesByType,
        IReadOnlyDictionary<string, ICollectionEngine> collectionEnginesByDirectoryName,
        PathWatcher rootPathWatcher)
    {
        RootPath = rootPath;
        _resources = resources;
        _collectionEnginesByType = collectionEnginesByType;
        _collectionEnginesByDirectoryName = collectionEnginesByDirectoryName;
        _rootPathWatcher = rootPathWatcher;

        // Wired last: the handlers read the fields above, and enabling the watcher can raise an
        // event before this constructor returns.
        _rootPathWatcher.Changed += (_, path) => HandleRootDirectoryChange(path);
        _rootPathWatcher.Error += (_, _) => RequestAllDirectoriesReconcile();
        _rootPathWatcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Opens a database with the specified collections at the specified path.
    /// </summary>
    /// <remarks>
    /// Opening takes longer the first time the database is opened in a directory, or after its index cache was discarded.
    /// </remarks>
    /// <param name="path">
    /// The path to the database, specified as a relative or absolute path.
    /// A relative path is interpreted as relative to the current working directory.
    /// </param>
    /// <param name="collectionDefinitions">The definitions that describe and configure the collections in the database.</param>
    /// <param name="options">Options to control the behavior of the database.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that returns the opened <see cref="FolderDatabase"/> instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> or <paramref name="collectionDefinitions"/> is <see langword="null"/>.
    ///
    /// -or-
    ///
    /// <paramref name="collectionDefinitions"/> contains a <see langword="null"/> definition.
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
    /// <paramref name="collectionDefinitions"/> contains a definition with an invalid name, or two
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
    /// <paramref name="collectionDefinitions"/> contains a definition with a <see langword="null"/> value.
    ///
    /// -or-
    ///
    /// A factory configured in <paramref name="options"/> or on <paramref name="collectionDefinitions"/> returned
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
        IReadOnlyList<ICollectionDefinition> collectionDefinitions,
        DatabaseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return DatabaseFactory.OpenDatabaseAsync(path, collectionDefinitions, options, cancellationToken);
    }

    /// <summary>
    /// Opens a database as a single collection at the specified path.
    /// </summary>
    /// <remarks>
    /// Opening takes longer the first time the database is opened in a directory, or after its index cache was discarded.
    /// </remarks>
    /// <param name="path">
    /// The path to the database, specified as a relative or absolute path.
    /// A relative path is interpreted as relative to the current working directory.
    /// The directory that holds the collection is created when missing.
    /// </param>
    /// <param name="definition">The definition that describes and configures the collection.</param>
    /// <param name="options">Options to control the behavior of the database.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that returns the opened <see cref="IFolderRecordCollection{TKey, TRecord}"/> instance.</returns>
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
    public static async Task<IFolderRecordCollection<TKey, TRecord>> OpenCollectionAsync<TKey, TRecord>(
        string path,
        CollectionDefinition<TKey, TRecord, NoProjection> definition,
        DatabaseOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return await DatabaseFactory.OpenCollectionAsync(path, definition, options, cancellationToken);
    }

    /// <summary>
    /// Opens a database as a single indexed collection at the specified path.
    /// </summary>
    /// <remarks>
    /// Opening takes longer the first time the database is opened in a directory, or after its index cache was discarded.
    /// </remarks>
    /// <param name="path">
    /// The path to the database, specified as a relative or absolute path.
    /// A relative path is interpreted as relative to the current working directory.
    /// The directory that holds the collection is created when missing.
    /// </param>
    /// <param name="definition">The definition that describes and configures the collection.</param>
    /// <param name="options">Options to control the behavior of the database.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that returns the opened <see cref="IFolderIndexedRecordCollection{TKey, TRecord, TProjection}"/> instance.
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
    public static async Task<IFolderIndexedRecordCollection<TKey, TRecord, TProjection>> OpenIndexedCollectionAsync<TKey, TRecord, TProjection>(
        string path,
        CollectionDefinition<TKey, TRecord, TProjection> definition,
        DatabaseOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return await DatabaseFactory.OpenCollectionAsync(path, definition, options, cancellationToken);
    }

    /// <summary>
    /// Gets the collection that stores <typeparamref name="TRecord"/>.
    /// </summary>
    /// <typeparam name="TKey">The type of the record id the collection was defined with.</typeparam>
    /// <typeparam name="TRecord">The record type the collection stores.</typeparam>
    /// <returns>The collection that stores <typeparamref name="TRecord"/>.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// There is no collection defined with <typeparamref name="TKey"/> and <typeparamref name="TRecord"/>.
    /// </exception>
    public IRecordCollection<TKey, TRecord> Collection<TKey, TRecord>()
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return GetCollection<TRecord, IRecordCollection<TKey, TRecord>>();
    }

    /// <summary>
    /// Gets the collection that stores <typeparamref name="TRecord"/>, together with its projection index.
    /// </summary>
    /// <typeparam name="TKey">The type of the record id the collection was defined with.</typeparam>
    /// <typeparam name="TRecord">The record type the collection stores.</typeparam>
    /// <typeparam name="TProjection">The projection type the collection was defined with.</typeparam>
    /// <returns>
    /// The collection that stores <typeparamref name="TRecord"/>, together with its projection index.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// There is no collection defined with <typeparamref name="TKey"/>, <typeparamref name="TRecord"/>
    /// and <typeparamref name="TProjection"/>.
    /// </exception>
    public IIndexedRecordCollection<TKey, TRecord, TProjection> IndexedCollection<TKey, TRecord, TProjection>()
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return GetCollection<TRecord, IIndexedRecordCollection<TKey, TRecord, TProjection>>();
    }

    /// <inheritdoc/>
    public async Task FlushIndexAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        foreach (var collectionEngine in _collectionEnginesByType.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await collectionEngine.FlushAsync(cancellationToken);
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

        foreach (var collectionEngine in _collectionEnginesByType.Values)
            await DisposeHelper.SafeDispose(collectionEngine);

        _resources.Dispose();

        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
    }

    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// There is no collection for <typeparamref name="TRecord"/>.
    ///
    /// -or-
    ///
    /// The collection cannot be accessed as <typeparamref name="TCollection"/>.
    /// </exception>
    private TCollection GetCollection<TRecord, TCollection>()
        where TCollection : class
    {
        ThrowIfDisposed();

        if (!_collectionEnginesByType.TryGetValue(typeof(TRecord), out var collectionEngine))
        {
            throw new InvalidOperationException(
                $"No collection is defined for record type '{typeof(TRecord)}'. " +
                $"Pass its collection definition to {nameof(FolderDatabase)}.{nameof(OpenAsync)}.");
        }

        if (collectionEngine is not TCollection collection)
        {
            throw new InvalidOperationException(
                $"The collection for record type '{typeof(TRecord)}' cannot be accessed as '{typeof(TCollection)}'. " +
                "The requested key or projection type differs from the one the collection was defined with.");
        }

        return collection;
    }

    private void HandleRootDirectoryChange(string path)
    {
        var directoryName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(directoryName))
            return;

        if (_collectionEnginesByDirectoryName.TryGetValue(directoryName, out var collectionEngine))
        {
            collectionEngine.RequestDirectoryReconcile();
        }
    }

    private void RequestAllDirectoriesReconcile()
    {
        foreach (var collectionEngine in _collectionEnginesByDirectoryName.Values)
        {
            collectionEngine.RequestDirectoryReconcile();
        }
    }
}

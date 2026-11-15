using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Building;
using FolderDB.Encoding;
using FolderDB.FileStorage;
using FolderDB.Indexing;
using FolderDB.Indexing.Persistence;
using FolderDB.Infrastructure.Watching;
using FolderDB.Retry;
using FolderDB.Runtime;
using Microsoft.Extensions.Logging;

namespace FolderDB;

/// <summary>
/// Specifies the values a store uses to create and start a collection.
/// </summary>
/// <typeparam name="TKey">The type of the record id.</typeparam>
/// <typeparam name="TRecord">The record type stored in the collection.</typeparam>
/// <typeparam name="TProjection">The type of the indexed projection of a record.</typeparam>
public class CollectionDefinition<TKey, TRecord, TProjection>: ICollectionDefinition
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    /// <inheritdoc/>
    public required string Name { get; init; }

    /// <inheritdoc/>
    public Type RecordType => typeof(TRecord);

    /// <summary>
    /// Gets the comparer that orders record ids.
    /// </summary>
    /// <returns>The comparer that orders record ids.</returns>
    public required IComparer<TKey> KeyComparer { get; init; }

    /// <summary>
    /// Gets the comparer that decides whether two record ids are the same.
    /// </summary>
    /// <returns>The comparer that decides whether two record ids are the same.</returns>
    public required IEqualityComparer<TKey> KeyEqualityComparer { get; init; }

    /// <summary>
    /// Gets the generator that proposes file names for a record, in the order they are tried.
    /// </summary>
    /// <returns>The generator that proposes file names for a record.</returns>
    /// <remarks>
    /// <para>
    /// A candidate is a file name without an extension, which the collection appends. It must not be
    /// <see langword="null"/>, empty, or consist only of white-space characters, must not be too
    /// long for a file name, and must not contain any of the characters returned by
    /// <see cref="Path.GetInvalidFileNameChars"/>.
    /// </para>
    /// <para>
    /// Candidates are enumerated lazily. The next one is taken when a name is already taken by
    /// another record, up to <see cref="StoreOptions.MaxFileNameReserveAttempts"/> times;
    /// running out of candidates fails the operation.
    /// </para>
    /// </remarks>
    public required Func<TRecord, IEnumerable<string>> FileNameGenerator { get; init; }

    /// <summary>
    /// Gets the delegate that produces the indexed projection of a record.
    /// </summary>
    /// <returns>The delegate that produces the indexed projection of a record.</returns>
    public required Func<TRecord, TProjection> CreateProjection { get; init; }

    /// <summary>
    /// Gets the factory that creates the codec that converts between records and the files they
    /// are stored in.
    /// </summary>
    /// <returns>
    /// The factory that creates the codec that converts between records and the files they are
    /// stored in.
    /// </returns>
    public required Func<RecordCodecContext, IRecordCodec<TKey, TRecord>> RecordCodecFactory { get; init; }

    /// <summary>
    /// Gets the factory that creates the persistence the index is loaded from and saved to.
    /// </summary>
    /// <returns>
    /// The index persistence factory, or <see langword="null"/> if the collection brings its own index
    /// engine instead.
    /// </returns>
    /// <remarks>
    /// Only one of the two index factories is used: this one takes precedence over
    /// <see cref="IndexEngineFactory"/>, and starting the collection fails when both are
    /// <see langword="null"/>.
    /// </remarks>
    public required Func<CollectionIndexPersistenceContext<TKey, TRecord, TProjection>, ICollectionIndexPersistence<TKey, TProjection>>? IndexPersistenceFactory { get; init; }

    /// <summary>
    /// Gets the factory that creates the index engine the collection runs its index on.
    /// </summary>
    /// <returns>
    /// The index engine factory, or <see langword="null"/> if the collection uses the built-in engine
    /// over an index persistence instead.
    /// </returns>
    /// <remarks>
    /// Only one of the two index factories is used: <see cref="IndexPersistenceFactory"/> takes
    /// precedence over this one, and starting the collection fails when both are <see langword="null"/>.
    /// </remarks>
    public required Func<RecordScopedIndexEngineContext<TKey, TRecord, TProjection>, Task<IRecordScopedIndexEngine<TKey, TRecord, TProjection>>>? IndexEngineFactory { get; init; }

    /// <summary>
    /// Asynchronously creates and starts the engine that runs the collection.
    /// </summary>
    /// <param name="collectionPath">The directory that holds the collection.</param>
    /// <param name="indexFilePath">The file the collection index is persisted to.</param>
    /// <param name="fileStore">The file store the collection reads and writes records through.</param>
    /// <param name="retryScheduler">The scheduler that queues reconciliation retries.</param>
    /// <param name="options">Options to control the behavior of the store.</param>
    /// <param name="loggerFactory">The logger factory scoped to this collection.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that returns the asynchronous start operation, which wraps the started engine.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// This definition contains a null value.
    ///
    /// -or-
    ///
    /// A factory configured on this definition or in <paramref name="options"/> returned null.
    ///
    /// -or-
    ///
    /// This definition provides neither an index persistence nor an index engine factory.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <remarks>
    /// Also exceptions from:
    /// <see cref="Directory.CreateDirectory(string)"/>,
    /// <see cref="PathWatcher(string, string, ILogger{PathWatcher})"/>,
    /// <see cref="PathWatcher.EnableRaisingEvents"/>.
    /// </remarks>
    public async Task<ICollectionEngine> StartEngineAsync(
        string collectionPath,
        string indexFilePath,
        IFileStore fileStore,
        IRetryScheduler<string> retryScheduler,
        StoreOptions options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        Validate();

        return await CollectionEngine<TKey, TRecord, TProjection>.StartAsync(
            collectionPath,
            indexFilePath,
            this,
            fileStore,
            retryScheduler,
            options,
            loggerFactory,
            cancellationToken);
    }

    /// <exception cref="InvalidOperationException">This definition contains a null value.</exception>
    internal void Validate()
    {
        ThrowIfNull(KeyComparer, nameof(KeyComparer));
        ThrowIfNull(KeyEqualityComparer, nameof(KeyEqualityComparer));
        ThrowIfNull(FileNameGenerator, nameof(FileNameGenerator));
        ThrowIfNull(CreateProjection, nameof(CreateProjection));
        ThrowIfNull(RecordCodecFactory, nameof(RecordCodecFactory));
    }

    /// <exception cref="InvalidOperationException"><paramref name="member"/> is null.</exception>
    private void ThrowIfNull(object? member, string memberName)
    {
        if (member is null)
        {
            throw new InvalidOperationException(
                $"'{Name}' collection's defined {memberName} is null.");
        }
    }
}

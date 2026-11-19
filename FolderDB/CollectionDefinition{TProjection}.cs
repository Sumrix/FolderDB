using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Encoding;
using FolderDB.Indexing.Persistence;
using FolderDB.Infrastructure.Watching;
using FolderDB.Runtime;
using Microsoft.Extensions.Logging;

namespace FolderDB;

/// <summary>
/// Defines an indexed collection to be opened with <see cref="FolderStore"/>.
/// </summary>
/// <typeparam name="TKey">The type of the record id.</typeparam>
/// <typeparam name="TRecord">The record type stored in the collection.</typeparam>
/// <typeparam name="TProjection">The type of the indexed projection of a record.</typeparam>
public class CollectionDefinition<TKey, TRecord, TProjection> : ICollectionDefinition
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    private string? _name;
    private IRecordCodec<TKey, TRecord> _codec;
    private Func<TRecord, TProjection> _projection = null!;

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">The value is empty.</exception>
    public string? Name
    {
        get => _name;
        set
        {
            if (value is not null)
            {
                ArgumentException.ThrowIfNullOrEmpty(value);
            }

            _name = value;
        }
    }

    /// <inheritdoc/>
    public Type RecordType => typeof(TRecord);

    /// <summary>
    /// Gets or sets the comparer that orders record ids.
    /// </summary>
    /// <returns>The comparer that orders record ids.</returns>
    public IComparer<TKey>? KeyComparer { get; set; }

    /// <summary>
    /// Gets or sets the comparer that decides whether two record ids are the same.
    /// </summary>
    /// <returns>The comparer that decides whether two record ids are the same.</returns>
    public IEqualityComparer<TKey>? KeyEqualityComparer { get; set; }

    /// <summary>
    /// Gets or sets the generator that proposes file names for a record, in the order they are tried.
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
    /// another record; running out of candidates fails the operation. A repeated candidate fails
    /// the operation.
    /// </para>
    /// </remarks>
    public Func<TRecord, IEnumerable<string>>? FileNames { get; set; }

    /// <summary>
    /// Gets or sets a codec that converts between records and the files.
    /// </summary>
    /// <returns>
    /// The codec that converts between records and the files.
    /// </returns>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public IRecordCodec<TKey, TRecord> Codec
    {
        get => _codec;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _codec = value;
        }
    }

    /// <summary>
    /// Gets or sets the delegate that produces the indexed projection of a record.
    /// </summary>
    /// <returns>The delegate that produces the indexed projection of a record.</returns>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public required Func<TRecord, TProjection> Projection
    {
        get => _projection;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _projection = value;
        }
    }

    public JsonTypeInfo<TKey> KeyTypeInfo { get; }

    public JsonTypeInfo<TProjection> ProjectionTypeInfo { get; }

    [RequiresUnreferencedCode("Uses reflection-based resolver via populateMissingResolver.")]
    [RequiresDynamicCode("May require runtime code generation for reflection-based serialization.")]
    public CollectionDefinition()
        : this(
            new RecordCodec<TKey, TRecord>(new RecordSchemaBuilder().WithoutVersioning<TRecord>()),
            (JsonTypeInfo<TKey>)IndexJsonOptions.Reflection.GetTypeInfo(typeof(TKey)),
            (JsonTypeInfo<TProjection>)IndexJsonOptions.Reflection.GetTypeInfo(typeof(TProjection)))
    {
    }

    public CollectionDefinition(
        IRecordCodec<TKey, TRecord> codec,
        JsonTypeInfo<TKey> keyTypeInfo,
        JsonTypeInfo<TProjection> projectionTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(keyTypeInfo);
        ArgumentNullException.ThrowIfNull(projectionTypeInfo);

        _codec = codec;
        KeyTypeInfo = keyTypeInfo;
        ProjectionTypeInfo = projectionTypeInfo;
    }

    /// <summary>
    /// Asynchronously creates and starts the engine that runs the collection.
    /// </summary>
    /// <param name="collectionPath">The directory that holds the collection.</param>
    /// <param name="indexFilePath">The file the collection index is persisted to.</param>
    /// <param name="storeContext">
    /// The values and services of the store that the collection is started in.
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that returns the asynchronous start operation, which wraps the started engine.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="storeContext"/> is null.</exception>
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
        StoreContext storeContext,
        CancellationToken cancellationToken = default)
    {
        return await CollectionEngine<TKey, TRecord, TProjection>.StartAsync(
            collectionPath,
            indexFilePath,
            this,
            storeContext,
            cancellationToken);
    }
}

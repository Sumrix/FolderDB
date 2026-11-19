using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.FileStorage;
using FolderDB.Infrastructure.Helpers;
using FolderDB.Infrastructure.Logging;
using FolderDB.Infrastructure.Watching;
using FolderDB.Retry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderDB.Runtime;

// Internal because it is part of FolderStore's implementation.
internal sealed class StoreFactory
{
    private const string IndexDirectoryName = ".indices";

    private readonly string _indexDirectoryPath;
    private readonly StoreContext _context;

    private string RootPath { get; }

    private ILoggerFactory LoggerFactory => _context.LoggerFactory;

    /// <exception cref="ArgumentNullException"><paramref name="rootPath"/> is null.</exception>
    /// <remarks>
    /// Also exceptions from:
    /// <see cref="Path.GetFullPath(string)"/>.
    /// </remarks>
    private StoreFactory(string rootPath, StoreOptions? options)
    {
        ArgumentNullException.ThrowIfNull(rootPath);

        options ??= new StoreOptions();

        RootPath = PathHelper.NormalizePath(rootPath);
        _indexDirectoryPath = Path.Combine(RootPath, IndexDirectoryName);
        _context = CreateContext(options);
    }

    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> or <paramref name="collectionDefinitions"/> is <see langword="null"/>.
    ///
    /// -or-
    ///
    /// <paramref name="collectionDefinitions"/> contains a <see langword="null"/> definition.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="collectionDefinitions"/> contains a definition with an invalid name, or two
    /// definitions that collide by name or by record type.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A definition in <paramref name="collectionDefinitions"/> returned no collection engine.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <remarks>
    /// Also exceptions from:
    /// <see cref="Path.GetFullPath(string)"/>,
    /// <see cref="Directory.CreateDirectory(string)"/>,
    /// <see cref="PathWatcher(string, string, ILogger{PathWatcher})"/>,
    /// <see cref="PathWatcher.EnableRaisingEvents"/>.
    /// </remarks>
    public static async Task<FolderStore> OpenStoreAsync(
        string path,
        IReadOnlyList<ICollectionDefinition> collectionDefinitions,
        StoreOptions? options,
        CancellationToken cancellationToken)
    {
        // Validation
        ArgumentNullException.ThrowIfNull(collectionDefinitions);

        var enginesByType = new Dictionary<Type, ICollectionEngine>(collectionDefinitions.Count);
        var enginesByDirectoryName = new Dictionary<string, ICollectionEngine>(
            collectionDefinitions.Count, PathHelper.OSDependedPathComparer);
        var directoryNames = new string[collectionDefinitions.Count];

        for (var i = 0; i < collectionDefinitions.Count; i++)
        {
            var collectionDefinition = collectionDefinitions[i];
            if (collectionDefinition is null)
            {
                throw new ArgumentNullException(
                    nameof(collectionDefinitions),
                    $"The collection definition at index {i} is null.");
            }

            var directoryName = GetCollectionDirectoryName(collectionDefinition, nameof(collectionDefinitions));

            if (!enginesByType.TryAdd(collectionDefinition.RecordType, NullEngine.Instance))
            {
                throw new ArgumentException(
                    $"Multiple collections with the same record type detected: '{collectionDefinition.RecordType.FullName}'.",
                    nameof(collectionDefinitions));
            }
            if (!enginesByDirectoryName.TryAdd(directoryName, NullEngine.Instance))
            {
                throw new ArgumentException(
                    $"Directory name collision detected after sanitizing collection names: '{directoryName}'.",
                    nameof(collectionDefinitions));
            }

            directoryNames[i] = directoryName;
        }

        // Construction
        var factory = new StoreFactory(path, options);
        var logger = factory.LoggerFactory.CreateLogger<StoreFactory>();

        using var _ = logger.BeginMethodScope();

        logger.LogTrace("Opening: path=\"{RootPath}\"", factory.RootPath);
        PathWatcher? rootPathWatcher = null;

        try
        {
            factory.CreateDirectories();

            for (var i = 0; i < collectionDefinitions.Count; i++)
            {
                var collectionDefinition = collectionDefinitions[i];
                var directoryName = directoryNames[i];

                var collectionEngine = await factory.StartEngineAsync(
                    collectionDefinition,
                    Path.Combine(factory.RootPath, directoryName),
                    factory.GetIndexFilePath(directoryName),
                    cancellationToken);

                enginesByType[collectionDefinition.RecordType] = collectionEngine;
                enginesByDirectoryName[directoryName] = collectionEngine;
            }

            rootPathWatcher = new PathWatcher(
                factory.RootPath,
                logger: factory.LoggerFactory.CreateLogger<PathWatcher>());

            var store = new FolderStore(
                factory.RootPath,
                factory._context,
                enginesByType,
                enginesByDirectoryName,
                rootPathWatcher);

            logger.LogDebug("Opened: path=\"{RootPath}\"", factory.RootPath);

            return store;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open: path=\"{RootPath}\"", factory.RootPath);

            DisposeHelper.SafeDispose(rootPathWatcher);
            foreach (var collectionEngine in enginesByType.Values)
                await DisposeHelper.SafeDispose(collectionEngine);
            factory._context.Dispose();

            throw;
        }
    }

    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> or <paramref name="definition"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="definition"/> has an invalid name.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <remarks>
    /// Also exceptions from:
    /// <see cref="Path.GetFullPath(string)"/>,
    /// <see cref="Directory.CreateDirectory(string)"/>,
    /// <see cref="PathWatcher(string, string, ILogger{PathWatcher})"/>,
    /// <see cref="PathWatcher.EnableRaisingEvents"/>.
    /// </remarks>
    public static async Task<FolderIndexedRecordCollection<TKey, TRecord, TProjection>> OpenCollectionAsync<TKey, TRecord, TProjection>(
        string path,
        CollectionDefinition<TKey, TRecord, TProjection> definition,
        StoreOptions? options,
        CancellationToken cancellationToken)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(definition);

        var directoryName = GetCollectionDirectoryName(definition, nameof(definition));

        var factory = new StoreFactory(path, options);
        var logger = factory.LoggerFactory.CreateLogger<StoreFactory>();

        using var _ = logger.BeginMethodScope();

        logger.LogTrace("Opening: path=\"{RootPath}\"", factory.RootPath);

        CollectionEngine<TKey, TRecord, TProjection>? collectionEngine = null;

        try
        {
            factory.CreateDirectories();

            var collectionPath = Path.Combine(factory.RootPath, directoryName);

            collectionEngine = await factory.StartEngineAsync(
                definition,
                collectionPath,
                factory.GetIndexFilePath(directoryName),
                cancellationToken);

            var collection = new FolderIndexedRecordCollection<TKey, TRecord, TProjection>(
                factory.RootPath,
                factory._context,
                collectionEngine);

            logger.LogDebug("Opened: path=\"{RootPath}\"", factory.RootPath);

            return collection;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open: path=\"{RootPath}\"", factory.RootPath);

            await DisposeHelper.SafeDispose(collectionEngine);
            factory._context.Dispose();

            throw;
        }
    }

    /// <exception cref="ArgumentException">
    /// The definition returned a name that sanitizes to the reserved index directory name.
    /// </exception>
    private static string GetCollectionDirectoryName(ICollectionDefinition collectionDefinition, string paramName)
    {
        var name = collectionDefinition.Name ?? collectionDefinition.RecordType.Name;

        var directoryName = PathHelper.SanitizeFileName(name);

        if (PathHelper.OSDependedPathComparer.Equals(directoryName, IndexDirectoryName))
        {
            throw new ArgumentException(
                $"The collection name '{name}' collides with the reserved index directory '{IndexDirectoryName}'.",
                paramName);
        }

        return directoryName;
    }

    /// <remarks>
    /// Also exceptions from:
    /// <see cref="Directory.CreateDirectory(string)"/>.
    /// </remarks>
    private void CreateDirectories()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(_indexDirectoryPath);
    }

    private string GetIndexFilePath(string directoryName)
    {
        return Path.Combine(_indexDirectoryPath, $"{directoryName}.index.json");
    }

    /// <exception cref="InvalidOperationException">The definition returned no engine.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <remarks>
    /// Also exceptions from:
    /// <see cref="Directory.CreateDirectory(string)"/>,
    /// <see cref="PathWatcher(string, string, ILogger{PathWatcher})"/>,
    /// <see cref="PathWatcher.EnableRaisingEvents"/>.
    /// </remarks>
    private async Task<ICollectionEngine> StartEngineAsync(
        ICollectionDefinition collectionDefinition,
        string collectionPath,
        string indexFilePath,
        CancellationToken cancellationToken)
    {
        var collectionEngine = await collectionDefinition.StartEngineAsync(
            collectionPath,
            indexFilePath,
            _context,
            cancellationToken);

        if (collectionEngine is null)
        {
            throw new InvalidOperationException(
                $"The definition of collection '{collectionDefinition.Name}' returned no collection engine.");
        }

        return collectionEngine;
    }

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <remarks>
    /// Also exceptions from:
    /// <see cref="Directory.CreateDirectory(string)"/>,
    /// <see cref="PathWatcher(string, string, ILogger{PathWatcher})"/>,
    /// <see cref="PathWatcher.EnableRaisingEvents"/>.
    /// </remarks>
    private Task<CollectionEngine<TKey, TRecord, TProjection>> StartEngineAsync<TKey, TRecord, TProjection>(
        CollectionDefinition<TKey, TRecord, TProjection> collectionDefinition,
        string collectionPath,
        string indexFilePath,
        CancellationToken cancellationToken)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        return CollectionEngine<TKey, TRecord, TProjection>.StartAsync(
            collectionPath,
            indexFilePath,
            collectionDefinition,
            _context,
            cancellationToken);
    }

    private static StoreContext CreateContext(StoreOptions options)
    {
        var loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;

        return new StoreContext
        {
            LoggerFactory = loggerFactory,
            FileStore = options.FileStore ?? new FileStore(loggerFactory.CreateLogger<FileStore>()),
            RetryScheduler = new TimeBucketQueueManager(options.ReconciliationRetryDelays, loggerFactory),
            ReadRetryDelays = options.OperationRetryDelays.Read,
            WriteRetryDelays = options.OperationRetryDelays.Write,
            IndexAutoSaveInterval = options.IndexAutoSaveInterval
        };
    }

    private sealed class NullEngine : ICollectionEngine
    {
        public static readonly NullEngine Instance = new();

        public void RequestDirectoryReconcile() { }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

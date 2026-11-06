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
    private readonly StoreOptions _options;
    private readonly StoreResources _resources;

    private string RootPath { get; }

    private ILoggerFactory LoggerFactory => _options.LoggerFactory ?? NullLoggerFactory.Instance;

    /// <exception cref="ArgumentNullException"><paramref name="rootPath"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A configured factory returned null.</exception>
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
        _options = options;
        _resources = CreateResources(options);
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
                factory._resources,
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
            factory._resources.Dispose();

            throw;
        }
    }

    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> or <paramref name="definition"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="definition"/> has an invalid name.
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
                factory._resources,
                collectionEngine);

            logger.LogDebug("Opened: path=\"{RootPath}\"", factory.RootPath);

            return collection;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open: path=\"{RootPath}\"", factory.RootPath);

            await DisposeHelper.SafeDispose(collectionEngine);
            factory._resources.Dispose();

            throw;
        }
    }

    /// <exception cref="ArgumentException">
    /// The definition returned a null or empty name.
    ///
    /// -or-
    ///
    /// The definition returned a name that sanitizes to the reserved index directory name.
    /// </exception>
    private static string GetCollectionDirectoryName(ICollectionDefinition collectionDefinition, string paramName)
    {
        if (string.IsNullOrEmpty(collectionDefinition.Name))
        {
            throw new ArgumentException(
                $"The definition '{collectionDefinition.GetType().FullName}' returned a null or empty collection name.",
                paramName);
        }

        var directoryName = PathHelper.SanitizeFileName(collectionDefinition.Name);

        if (PathHelper.OSDependedPathComparer.Equals(directoryName, IndexDirectoryName))
        {
            throw new ArgumentException(
                $"The collection name '{collectionDefinition.Name}' collides with the reserved index directory '{IndexDirectoryName}'.",
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

    /// <exception cref="InvalidOperationException">
    /// The definition returned no engine.
    ///
    /// -or-
    ///
    /// The definition contains a null value.
    ///
    /// -or-
    ///
    /// A factory configured on the definition or in the options returned null.
    ///
    /// -or-
    ///
    /// The definition provides neither an index persistence nor an index engine factory.
    /// </exception>
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
            _resources.FileStore,
            _resources.RetryScheduler,
            _resources.Retry,
            _options,
            LoggerFactory.CreateCollectionScopedLoggerFactory(collectionDefinition.Name),
            cancellationToken);

        if (collectionEngine is null)
        {
            throw new InvalidOperationException(
                $"The definition of collection '{collectionDefinition.Name}' returned no collection engine.");
        }

        return collectionEngine;
    }

    /// <exception cref="InvalidOperationException">
    /// The definition contains a null value.
    ///
    /// -or-
    ///
    /// A factory configured on the definition or in the options returned null.
    ///
    /// -or-
    ///
    /// The definition provides neither an index persistence nor an index engine factory.
    /// </exception>
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
        // The non-generic overload gets this from CollectionDefinition.StartEngineAsync; this one reaches
        // the engine directly, so it has to run the check itself.
        collectionDefinition.Validate();

        return CollectionEngine<TKey, TRecord, TProjection>.StartAsync(
            collectionPath,
            indexFilePath,
            collectionDefinition,
            _resources.FileStore,
            _resources.RetryScheduler,
            _resources.Retry,
            _options,
            LoggerFactory.CreateCollectionScopedLoggerFactory(collectionDefinition.Name),
            cancellationToken);
    }

    private static StoreResources CreateResources(StoreOptions options)
    {
        IFileStore? fileStore = null;
        IRetryScheduler<string>? retryScheduler = null;
        var loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;

        try
        {
            fileStore = options.FileStoreFactory?.Invoke()
                        ?? new FileStore(loggerFactory.CreateLogger<FileStore>());
            if (fileStore is null)
            {
                throw new InvalidOperationException("The configured file store factory returned null.");
            }

            retryScheduler = options.RetrySchedulerFactory is null
                ? new TimeBucketQueueManager(loggerFactory: loggerFactory)
                : options.RetrySchedulerFactory(loggerFactory);
            if (retryScheduler is null)
            {
                throw new InvalidOperationException("The configured retry scheduler factory returned null.");
            }

            return new StoreResources(fileStore, retryScheduler, ResolveRetry(options.Retry));
        }
        catch
        {
            DisposeHelper.SafeDispose(fileStore as IDisposable);
            DisposeHelper.SafeDispose(retryScheduler);
            throw;
        }
    }

    private static Dictionary<FileOperation, IEnumerable<TimeSpan>?>? ResolveRetry(Func<FileOperation, IEnumerable<TimeSpan>?>? retry)
    {
        if (retry is null)
            return null;

        return new()
        {
            [FileOperation.Read] = retry(FileOperation.Read),
            [FileOperation.Write] = retry(FileOperation.Write),
            [FileOperation.Delete] = retry(FileOperation.Delete)
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

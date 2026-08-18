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

// Internal because it is part of FolderDatabase's implementation.
internal sealed class DatabaseFactory
{
    private const string IndexDirectoryName = ".indices";

    private readonly string _indexDirectoryPath;
    private readonly DatabaseOptions _options;
    private readonly DatabaseResources _resources;

    private string RootPath { get; }

    private ILoggerFactory LoggerFactory => _options.LoggerFactory ?? NullLoggerFactory.Instance;

    private DatabaseFactory(string rootPath, DatabaseOptions? options)
    {
        ArgumentNullException.ThrowIfNull(rootPath);

        options ??= new DatabaseOptions();

        RootPath = PathHelper.NormalizePath(rootPath);
        _indexDirectoryPath = Path.Combine(RootPath, IndexDirectoryName);
        _options = options;
        _resources = CreateResources(options);
    }

    public static async Task<FolderDatabase> StartDatabaseAsync(
        string path,
        IReadOnlyList<ITableDefinition> tableDefinitions,
        DatabaseOptions? options,
        CancellationToken cancellationToken)
    {
        // Validation
        ArgumentNullException.ThrowIfNull(tableDefinitions);

        var enginesByType = new Dictionary<Type, ITableEngine>(tableDefinitions.Count);
        var enginesByDirectoryName = new Dictionary<string, ITableEngine>(
            tableDefinitions.Count, PathHelper.OSDependedPathComparer);
        var directoryNames = new string[tableDefinitions.Count];

        for (var i = 0; i < tableDefinitions.Count; i++)
        {
            var tableDefinition = tableDefinitions[i];
            if (tableDefinition is null)
            {
                throw new ArgumentNullException(
                    nameof(tableDefinitions),
                    $"The table definition at index {i} is null.");
            }

            var directoryName = GetTableDirectoryName(tableDefinition, nameof(tableDefinitions));

            if (!enginesByType.TryAdd(tableDefinition.RecordType, NullEngine.Instance))
            {
                throw new ArgumentException(
                    $"Multiple tables with the same record type detected: '{tableDefinition.RecordType.FullName}'.",
                    nameof(tableDefinitions));
            }
            if (!enginesByDirectoryName.TryAdd(directoryName, NullEngine.Instance))
            {
                throw new ArgumentException(
                    $"Directory name collision detected after sanitizing table names: '{directoryName}'.",
                    nameof(tableDefinitions));
            }

            directoryNames[i] = directoryName;
        }

        // Construction
        var factory = new DatabaseFactory(path, options);
        var logger = factory.LoggerFactory.CreateLogger<DatabaseFactory>();

        using var _ = logger.BeginMethodScope();

        logger.LogTrace("Starting: path=\"{RootPath}\"", factory.RootPath);
        PathWatcher? rootPathWatcher = null;

        try
        {
            factory.CreateDirectories();

            for (var i = 0; i < tableDefinitions.Count; i++)
            {
                var tableDefinition = tableDefinitions[i];
                var directoryName = directoryNames[i];

                var tableEngine = await factory.StartEngineAsync(
                    tableDefinition,
                    Path.Combine(factory.RootPath, directoryName),
                    factory.GetIndexFilePath(directoryName),
                    cancellationToken);

                enginesByType[tableDefinition.RecordType] = tableEngine;
                enginesByDirectoryName[directoryName] = tableEngine;
            }

            rootPathWatcher = new PathWatcher(
                factory.RootPath,
                logger: factory.LoggerFactory.CreateLogger<PathWatcher>());

            var database = new FolderDatabase(
                factory.RootPath,
                factory._resources,
                enginesByType,
                enginesByDirectoryName,
                rootPathWatcher);

            logger.LogDebug("Started: path=\"{RootPath}\"", factory.RootPath);

            return database;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start: path=\"{RootPath}\"", factory.RootPath);

            DisposeHelper.SafeDispose(rootPathWatcher);
            foreach (var tableEngine in enginesByType.Values)
                await DisposeHelper.SafeDispose(tableEngine);
            factory._resources.Dispose();

            throw;
        }
    }

    public static async Task<FolderIndexedTable<TKey, TRecord, TProjection>> StartTableAsync<TKey, TRecord, TProjection>(
        string path,
        TableDefinition<TKey, TRecord, TProjection> definition,
        DatabaseOptions? options,
        CancellationToken ct)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(definition);

        var directoryName = GetTableDirectoryName(definition, nameof(definition));

        var factory = new DatabaseFactory(path, options);
        var logger = factory.LoggerFactory.CreateLogger<DatabaseFactory>();

        using var _ = logger.BeginMethodScope();

        logger.LogTrace("Starting: path=\"{RootPath}\"", factory.RootPath);

        TableEngine<TKey, TRecord, TProjection>? tableEngine = null;

        try
        {
            factory.CreateDirectories();

            var tablePath = Path.Combine(factory.RootPath, directoryName);

            tableEngine = await factory.StartEngineAsync(
                definition,
                tablePath,
                factory.GetIndexFilePath(directoryName),
                ct);

            var table = new FolderIndexedTable<TKey, TRecord, TProjection>(
                factory.RootPath,
                factory._resources,
                tableEngine);

            logger.LogDebug("Started: path=\"{RootPath}\"", factory.RootPath);

            return table;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start: path=\"{RootPath}\"", factory.RootPath);

            await DisposeHelper.SafeDispose(tableEngine);
            factory._resources.Dispose();

            throw;
        }
    }

    private static string GetTableDirectoryName(ITableDefinition tableDefinition, string paramName)
    {
        if (string.IsNullOrEmpty(tableDefinition.Name))
        {
            throw new ArgumentException(
                $"The definition '{tableDefinition.GetType().FullName}' returned a null or empty table name.",
                paramName);
        }

        var directoryName = PathHelper.SanitizeFileName(tableDefinition.Name);

        if (PathHelper.OSDependedPathComparer.Equals(directoryName, IndexDirectoryName))
        {
            throw new ArgumentException(
                $"The table name '{tableDefinition.Name}' collides with the reserved index directory '{IndexDirectoryName}'.",
                paramName);
        }

        return directoryName;
    }

    private void CreateDirectories()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(_indexDirectoryPath);
    }

    private string GetIndexFilePath(string directoryName)
    {
        return Path.Combine(_indexDirectoryPath, $"{directoryName}.index.json");
    }

    private async Task<ITableEngine> StartEngineAsync(
        ITableDefinition tableDefinition,
        string tablePath,
        string indexFilePath,
        CancellationToken ct)
    {
        var tableEngine = await tableDefinition.StartEngineAsync(
            tablePath,
            indexFilePath,
            _resources.FileStore,
            _resources.RetryScheduler,
            _options,
            LoggerFactory.CreateTableScopedLoggerFactory(tableDefinition.Name),
            ct);

        if (tableEngine is null)
        {
            throw new InvalidOperationException(
                $"The definition of table '{tableDefinition.Name}' returned no table engine.");
        }

        return tableEngine;
    }

    private Task<TableEngine<TKey, TRecord, TProjection>> StartEngineAsync<TKey, TRecord, TProjection>(
        TableDefinition<TKey, TRecord, TProjection> tableDefinition,
        string tablePath,
        string indexFilePath,
        CancellationToken ct)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        // The non-generic overload gets this from TableDefinition.StartEngineAsync; this one reaches
        // the engine directly, so it has to run the check itself.
        tableDefinition.Validate();

        return TableEngine<TKey, TRecord, TProjection>.StartAsync(
            tablePath,
            indexFilePath,
            tableDefinition,
            _resources.FileStore,
            _resources.RetryScheduler,
            _options,
            LoggerFactory.CreateTableScopedLoggerFactory(tableDefinition.Name),
            ct);
    }

    public static IRetryScheduler<string> CreateDefaultRetryScheduler(
        DefaultRetrySchedulerOptions? options,
        ILoggerFactory? loggerFactory)
    {
        var effectiveOptions = options ?? new DefaultRetrySchedulerOptions();

        return new TimeBucketQueueManager(
            intervalMs: effectiveOptions.IntervalMs,
            maxRetryIntervals: effectiveOptions.MaxRetryIntervals,
            backoffMultiplier: effectiveOptions.BackoffMultiplier,
            // The values are paths, so we need to use the OS-dependent comparer
            valueComparer: PathHelper.OSDependedPathComparer,
            loggerFactory: loggerFactory);
    }

    private static DatabaseResources CreateResources(DatabaseOptions options)
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
                ? CreateDefaultRetryScheduler(options: null, loggerFactory)
                : options.RetrySchedulerFactory(loggerFactory);
            if (retryScheduler is null)
            {
                throw new InvalidOperationException("The configured retry scheduler factory returned null.");
            }

            return new DatabaseResources(fileStore, retryScheduler);
        }
        catch
        {
            DisposeHelper.SafeDispose(fileStore as IDisposable);
            DisposeHelper.SafeDispose(retryScheduler);
            throw;
        }
    }

    private sealed class NullEngine : ITableEngine
    {
        public static readonly NullEngine Instance = new();

        public void RequestDirectoryReconcile() { }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

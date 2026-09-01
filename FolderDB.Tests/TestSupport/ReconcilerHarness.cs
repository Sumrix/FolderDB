using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FolderDB.Encoding;
using FolderDB.FileStorage;
using FolderDB.Indexing;
using FolderDB.Indexing.Reconciliation;
using FolderDB.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderDB.Tests.TestSupport;

internal sealed class ReconcilerHarness : IAsyncDisposable
{
    private readonly string _rootPath;
    private readonly string _tablePath;
    private readonly InlineRetryScheduler _retryScheduler;
    private readonly FileReconciler<string, TestRecord, string> _fileReconciler;
    private readonly DirectoryReconciler<string, TestRecord, string> _directoryReconciler;

    private ReconcilerHarness(
        string rootPath,
        string tablePath,
        InlineRetryScheduler retryScheduler,
        TableIndex<string, TestRecord, string> index,
        FileReconciler<string, TestRecord, string> fileReconciler,
        DirectoryReconciler<string, TestRecord, string> directoryReconciler)
    {
        _rootPath = rootPath;
        _tablePath = tablePath;
        _retryScheduler = retryScheduler;
        _fileReconciler = fileReconciler;
        _directoryReconciler = directoryReconciler;
        TablePath = tablePath;
        RetryScheduler = retryScheduler;
        Index = index;
    }

    public string TablePath { get; }

    public TableIndex<string, TestRecord, string> Index { get; }

    public InlineRetryScheduler RetryScheduler { get; }

    public void RequestFileReconcile(string path)
    {
        _retryScheduler.Enqueue(path, _fileReconciler.ReconcileAsync);
    }

    public void RequestDirectoryReconcile()
    {
        _retryScheduler.Enqueue(_tablePath,
            (_, cancellationToken) => _directoryReconciler.ReconcileAsync(cancellationToken));
    }

    public static async Task<ReconcilerHarness> CreateAsync(IFileStore? fileStore = null)
    {
        var recordSchema = new RecordSchemaBuilder()
            .StartWith<LegacyTestRecord>(0)
            .UpgradeTo(1, legacy => new TestRecord(legacy.Id, 1, $"migrated-{legacy.LegacyValue}"))
            .Build();
        var rootPath = Directory.CreateTempSubdirectory().FullName;
        var tablePath = Path.Combine(rootPath, "table");
        var indexPath = Path.Combine(rootPath, "index-state.json");
        Directory.CreateDirectory(tablePath);
        var codec = new RecordCodec<string, TestRecord>(recordSchema);
        var table = TestTableContext.Create<string, TestRecord, string>(
            static value => value.Value,
            static record => [record.Id],
            StringComparer.Ordinal,
            StringComparer.Ordinal,
            codec);

        var indexEngine = await RecordScopedIndexEngine<string, TestRecord, string>.StartAsync(
            indexPath,
            table,
            TestTableContext.CreateJsonIndexPersistence(
                TestsJsonContext.Default.String,
                TestsJsonContext.Default.String,
                StringComparer.Ordinal),
            autoSaveEnabled: false);
        var index = new TableIndex<string, TestRecord, string>(indexEngine, table);

        var effectiveFileStore = fileStore ?? new FileStore();
        var store = new RecordStore<string, TestRecord>(codec, effectiveFileStore);
        var retryScheduler = new InlineRetryScheduler();
        var fileReconciler = new FileReconciler<string, TestRecord, string>(
            table,
            effectiveFileStore,
            store,
            index,
            NullLogger<FileReconciler<string, TestRecord, string>>.Instance);
        var directoryReconciler = new DirectoryReconciler<string, TestRecord, string>(
            tablePath,
            index,
            fileReconciler,
            (path, minBackoff) => retryScheduler.Enqueue(path, fileReconciler.ReconcileAsync, minBackoff),
            NullLogger<DirectoryReconciler<string, TestRecord, string>>.Instance);

        return new ReconcilerHarness(
            rootPath,
            tablePath,
            retryScheduler,
            index,
            fileReconciler,
            directoryReconciler);
    }

    public async Task<string> WriteRecordAsync(string fileName, TestRecord record)
    {
        var path = Path.Combine(TablePath, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, TestsJsonContext.Default.TestRecord));
        return path;
    }

    public async Task<string> WriteLegacyRecordAsync(string fileName, LegacyTestRecord record)
    {
        var path = Path.Combine(TablePath, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record));
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        RetryScheduler.Dispose();
        await Index.DisposeAsync();
        try
        {
            Directory.Delete(_rootPath, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed record LegacyTestRecord(string Id, int SchemaVersion, string LegacyValue) : IVersionedRecord;

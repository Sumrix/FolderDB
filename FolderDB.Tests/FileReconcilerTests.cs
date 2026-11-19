using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FolderDB.Encoding;
using FolderDB.FileStorage;
using FolderDB.Indexing;
using FolderDB.Indexing.Reconciliation;
using FolderDB.Indexing.State;
using FolderDB.Retry;
using FolderDB.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderDB.Tests;

// Decision makers and executors intentionally have no duplicate unit tests. They are direct,
// elementary translations of the decision collections in docs/index-reconciliation-rulebook.md;
// reproducing those collections as test expectations would create a second implementation of the
// same rules. These tests focus on the non-obvious orchestration behavior of FileReconciler.
public class FileReconcilerTests
{
    [Fact]
    public async Task ReconcileAsync_NewReadableFile_AddsItToIndex()
    {
        await using var harness = await Harness.CreateUnversionedAsync();
        var path = await harness.WriteAsync("record.json", new TestRecord("id-1", 1, "value"));

        var result = await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        result.Should().Be(RetryDecision.Complete);
        harness.Index.Records["id-1"].CurrentFileName.Should().Be("record.json");
        harness.Index.Records["id-1"].GetCurrentFileState().SchemaVersion.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileAsync_WithoutVersioning_DoesNotRunFileUpdateLogic()
    {
        var scriptedStore = new ScriptedFileStore(new FileStore());
        await using var harness = await Harness.CreateUnversionedAsync(scriptedStore);
        var path = await harness.WriteAsync("record.json", new TestRecord("id-1", 1, "first"));
        scriptedStore.EnqueueWriteResult(
            path,
            () => throw new InvalidOperationException(
                "File update logic must not write an unversioned file."));

        await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReconcileAsync_UnexpectedException_CompletesWithoutRetry()
    {
        var scriptedStore = new ScriptedFileStore(new FileStore());
        await using var harness = await Harness.CreateUnversionedAsync(scriptedStore);
        var path = await harness.WriteAsync("record.json", new TestRecord("id-1", 1, "value"));
        scriptedStore.EnqueueFingerprintResult(
            path,
            () => throw new InvalidOperationException("Unexpected reconciliation failure."));

        var result = await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        result.Should().Be(RetryDecision.Complete);
        harness.Index.Records.Should().NotContainKey("id-1");
    }

    [Theory]
    [InlineData("id-1", "id-2")]
    [InlineData("id-2", "id-1")]
    public async Task ReconcileAsync_FileChangesId_MovesItBetweenRecords(
        string indexedId,
        string fileId)
    {
        await using var harness = await Harness.CreateUnversionedAsync();
        var path = await harness.WriteAsync("record.json", new TestRecord(indexedId, 1, "first"));
        await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        await harness.WriteAsync("record.json", new TestRecord(fileId, 1, "second"));
        var result = await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        result.Should().Be(RetryDecision.Complete);
        harness.Index.Records.Should().NotContainKey(indexedId);
        harness.Index.Records[fileId].CurrentFileName.Should().Be("record.json");
        harness.Index.Records[fileId].GetCurrentFileState().Projection.Should().Be("second");
    }

    [Fact]
    public async Task ReconcileAsync_FingerprintChangesAfterLock_UsesLatestReadResult()
    {
        var scriptedStore = new ScriptedFileStore(new FileStore());
        await using var harness = await Harness.CreateUnversionedAsync(scriptedStore);
        var path = await harness.WriteAsync("record.json", new TestRecord("id-1", 1, "first"));
        var firstFingerprint = new FileStore().GetFileFingerprint(path);
        scriptedStore.EnqueueFingerprintResult(path, firstFingerprint);
        scriptedStore.EnqueueFingerprintResult(
            path,
            () => ReplaceFile(path, new TestRecord("id-1", 1, "second")));

        var result = await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        result.Should().Be(RetryDecision.Complete);
        var state = harness.Index.Records["id-1"].GetCurrentFileState();
        state.Projection.Should().Be("second");
        state.Fingerprint.Should().Be(new FileStore().GetFileFingerprint(path));
    }

    [Fact]
    public async Task ReconcileAsync_FileIdChangesAfterLock_ReturnsMinRetryWithoutMutation()
    {
        var scriptedStore = new ScriptedFileStore(new FileStore());
        await using var harness = await Harness.CreateUnversionedAsync(scriptedStore);
        var path = await harness.WriteAsync("record.json", new TestRecord("id-1", 1, "first"));
        var firstFingerprint = new FileStore().GetFileFingerprint(path);
        scriptedStore.EnqueueFingerprintResult(path, firstFingerprint);
        scriptedStore.EnqueueFingerprintResult(
            path,
            () => ReplaceFile(path, new TestRecord("id-2", 1, "second")));

        var firstResult = await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        firstResult.Should().Be(RetryDecision.RetryWithMinBackoff);
        harness.Index.Records.Should().BeEmpty();

        var secondResult = await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        secondResult.Should().Be(RetryDecision.Complete);
        harness.Index.Records.Should().ContainSingle().Which.Key.Should().Be("id-2");
        harness.Index.Records["id-2"].GetCurrentFileState().Projection.Should().Be("second");
    }

    // The pre-read fingerprint says the file exists, the read says it does not. Feeding the stale
    // pre-read observation to the post-read decision would keep the record with an error instead of
    // deleting it.
    [Fact]
    public async Task ReconcileAsync_FileDisappearsDuringRead_UsesPostReadObservation()
    {
        var scriptedStore = new ScriptedFileStore(new FileStore());
        await using var harness = await Harness.CreateUnversionedAsync(scriptedStore);
        var path = await harness.WriteAsync("record.json", new TestRecord("id-1", 1, "value"));
        await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        scriptedStore.EnqueueFingerprintResult(
            path,
            new FileFingerprint(DateTime.UtcNow.AddMinutes(1), 123, Exists: true));
        scriptedStore.EnqueueReadAccessResult(
            path,
            FileErrorPersistence.Persistent,
            new FileFingerprint(null, null, Exists: false));
        scriptedStore.EnqueueFingerprintResult(path, new FileFingerprint(null, null, Exists: false));

        var result = await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        result.Should().Be(RetryDecision.Complete);
        harness.Index.Records.Should().NotContainKey("id-1");
    }

    [Fact]
    public async Task ReconcileAsync_MissingIndexedFile_RemovesItFromIndex()
    {
        await using var harness = await Harness.CreateUnversionedAsync();
        var path = await harness.WriteAsync("record.json", new TestRecord("id-1", 1, "value"));
        await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        File.Delete(path);
        var result = await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        result.Should().Be(RetryDecision.Complete);
        harness.Index.Records.Should().NotContainKey("id-1");
    }

    [Fact]
    public async Task ReconcileAsync_InvalidIndexedFile_StoresErrorState()
    {
        await using var harness = await Harness.CreateUnversionedAsync();
        var path = await harness.WriteAsync("record.json", new TestRecord("id-1", 1, "value"));
        await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(path, "invalid json", TestContext.Current.CancellationToken);
        var result = await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        result.Should().Be(RetryDecision.Complete);
        var state = harness.Index.Records["id-1"].GetCurrentFileState();
        state.ErrorInfo.Should().NotBeNull();
        state.ErrorInfo!.Reason.Should().Be(FileErrorReason.Invalid);
    }

    [Fact]
    public async Task ReconcileAsync_LegacyCurrentFile_UpdatesFileAndIndexedPhysicalState()
    {
        await using var harness = await Harness.CreateVersionedAsync();
        var path = await harness.WriteLegacyAsync("record.json", new LegacyTestRecord("id-1", 0, "legacy"));

        var result = await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        result.Should().Be(RetryDecision.Complete);
        var persisted = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        JsonSerializer.Deserialize(persisted, TestsJsonContext.Default.TestRecord)
            .Should().Be(new TestRecord("id-1", 1, "migrated-legacy"));
        persisted.Should().NotContain("LegacyValue");

        var state = harness.Index.Records["id-1"].GetCurrentFileState();
        state.SchemaVersion.Should().Be(1);
        state.Fingerprint.Should().Be(new FileStore().GetFileFingerprint(path));
    }

    [Fact]
    public async Task ReconcileAsync_LegacyDormantFile_DoesNotUpdateFile()
    {
        await using var harness = await Harness.CreateVersionedAsync();
        var legacyPath = await harness.WriteLegacyAsync("legacy.json", new LegacyTestRecord("id-1", 0, "legacy"));
        var currentPath = await harness.WriteAsync("current.json", new TestRecord("id-1", 1, "current"));

        await harness.Reconciler.ReconcileAsync(currentPath, TestContext.Current.CancellationToken);
        var result = await harness.Reconciler.ReconcileAsync(legacyPath, TestContext.Current.CancellationToken);

        result.Should().Be(RetryDecision.Complete);
        harness.Index.Records["id-1"].CurrentFileName.Should().Be("current.json");
        harness.Index.Records["id-1"].Files["legacy.json"].SchemaVersion.Should().Be(0);
        (await File.ReadAllTextAsync(legacyPath, TestContext.Current.CancellationToken)).Should().Contain("LegacyValue");
    }

    [Fact]
    public async Task ReconcileAsync_TransientFormatUpdateWriteFailure_ReturnsRetryAndKeepsPhysicalVersion()
    {
        var scriptedStore = new ScriptedFileStore(new FileStore());
        await using var harness = await Harness.CreateVersionedAsync(scriptedStore);
        var path = await harness.WriteLegacyAsync("record.json", new LegacyTestRecord("id-1", 0, "legacy"));
        scriptedStore.EnqueueWriteResult(
            path,
            new FileWriteResult(
                null,
                new FileError(
                    FileErrorReason.Unavailable,
                    FileErrorPersistence.Transient,
                    new IOException("transient write"))));

        var firstResult = await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        firstResult.Should().Be(RetryDecision.RetryWithBackoff);
        harness.Index.Records["id-1"].GetCurrentFileState().SchemaVersion.Should().Be(0);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Contain("LegacyValue");

        var secondResult = await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        secondResult.Should().Be(RetryDecision.Complete);
        harness.Index.Records["id-1"].GetCurrentFileState().SchemaVersion.Should().Be(1);
    }

    [Fact]
    public async Task ReconcileAsync_CurrentVersionedFileWithMissingIndexedSchema_BackfillsSchemaVersion()
    {
        await using var harness = await Harness.CreateVersionedAsync();
        var record = new TestRecord("id-1", 1, "current");
        var path = await harness.WriteAsync("record.json", record);
        var fingerprint = new FileStore().GetFileFingerprint(path);
        harness.Engine.Upsert("id-1", "record.json", fingerprint, null, record)
            .Should().Be(IndexOperationResult.Applied);
        harness.Index.Records["id-1"].GetCurrentFileState().SchemaVersion.Should().BeNull();

        var result = await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        result.Should().Be(RetryDecision.Complete);
        harness.Index.Records["id-1"].GetCurrentFileState().SchemaVersion.Should().Be(1);
    }

    [Fact]
    public async Task ContinueAfterRead_WithHeldMatchingLock_UpdatesIndex()
    {
        await using var harness = await Harness.CreateUnversionedAsync();
        var path = await harness.WriteAsync("record.json", new TestRecord("id-1", 1, "first"));
        await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        await harness.WriteAsync("record.json", new TestRecord("ID-1", 1, "second"));
        var readResult = await harness.Store.ReadAsync(path, TestContext.Current.CancellationToken);
        using var sharedScope = await harness.Index.EnterSharedScopeAsync(TestContext.Current.CancellationToken);
        using var recordScope = await sharedScope.LockRecordAsync("id-1", TestContext.Current.CancellationToken);

        var result = await harness.Reconciler.ContinueAfterReadAsync(
            path,
            sharedScope,
            recordScope,
            readResult, TestContext.Current.CancellationToken);

        result.Should().Be(RetryDecision.Complete);
        harness.Index.Records["id-1"].GetCurrentFileState().Projection.Should().Be("second");
    }

    [Fact]
    public async Task ContinueAfterRead_WithDifferentFileId_DeletesStaleIndexEntryAndRequestsRetry()
    {
        await using var harness = await Harness.CreateUnversionedAsync();
        var path = await harness.WriteAsync("record.json", new TestRecord("id-1", 1, "first"));
        await harness.Reconciler.ReconcileAsync(path, TestContext.Current.CancellationToken);

        await harness.WriteAsync("record.json", new TestRecord("id-2", 1, "second"));
        var readResult = await harness.Store.ReadAsync(path, TestContext.Current.CancellationToken);
        using var sharedScope = await harness.Index.EnterSharedScopeAsync(TestContext.Current.CancellationToken);
        using var recordScope = await sharedScope.LockRecordAsync("id-1", TestContext.Current.CancellationToken);

        var result = await harness.Reconciler.ContinueAfterReadAsync(
            path,
            sharedScope,
            recordScope,
            readResult, TestContext.Current.CancellationToken);

        result.Should().Be(RetryDecision.RetryWithMinBackoff);
        harness.Index.Records.Should().NotContainKey("id-1");
        harness.Index.Records.Should().NotContainKey("id-2");
    }

    [Fact]
    public async Task ContinueAfterRead_LegacyFile_UpdatesIndexAndRequestsRetry()
    {
        await using var harness = await Harness.CreateVersionedAsync();
        var path = await harness.WriteLegacyAsync("record.json", new LegacyTestRecord("id-1", 0, "legacy"));
        var readResult = await harness.Store.ReadAsync(path, TestContext.Current.CancellationToken);
        using var sharedScope = await harness.Index.EnterSharedScopeAsync(TestContext.Current.CancellationToken);
        using var recordScope = await sharedScope.LockRecordAsync("id-1", TestContext.Current.CancellationToken);

        var result = await harness.Reconciler.ContinueAfterReadAsync(path, sharedScope, recordScope, readResult, TestContext.Current.CancellationToken);

        result.Should().Be(RetryDecision.RetryWithMinBackoff);
        harness.Index.Records["id-1"].GetCurrentFileState().SchemaVersion.Should().Be(0);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Contain("LegacyValue");
    }

    private static FileFingerprint ReplaceFile(
        string path,
        TestRecord record)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(record, TestsJsonContext.Default.TestRecord));
        return new FileStore().GetFileFingerprint(path);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _rootPath;

        private Harness(
            string rootPath,
            string collectionPath,
            RecordScopedIndexEngine<string, TestRecord, string> engine,
            CollectionIndex<string, TestRecord, string> index,
            RecordStore<string, TestRecord> store,
            FileReconciler<string, TestRecord, string> reconciler)
        {
            _rootPath = rootPath;
            CollectionPath = collectionPath;
            Engine = engine;
            Index = index;
            Store = store;
            Reconciler = reconciler;
        }

        public string CollectionPath { get; }
        public RecordScopedIndexEngine<string, TestRecord, string> Engine { get; }
        public CollectionIndex<string, TestRecord, string> Index { get; }
        public RecordStore<string, TestRecord> Store { get; }
        public FileReconciler<string, TestRecord, string> Reconciler { get; }

        public static async Task<Harness> CreateUnversionedAsync(IFileStore? fileStore = null)
        {
            var policy = new RecordSchemaBuilder()
                .WithoutVersioning(TestsJsonContext.Default.TestRecord);
            return await CreateAsync(policy, fileStore);
        }

        public static async Task<Harness> CreateVersionedAsync(IFileStore? fileStore = null)
        {
            var policy = new RecordSchemaBuilder()
                .StartWith<LegacyTestRecord>(0)
                .UpgradeTo(1, legacy => new TestRecord(legacy.Id, 1, $"migrated-{legacy.LegacyValue}"))
                .Build();
            return await CreateAsync(policy, fileStore);
        }

        private static async Task<Harness> CreateAsync(
            RecordSchema<TestRecord> policy,
            IFileStore? fileStore)
        {
            var rootPath = Directory.CreateTempSubdirectory().FullName;
            var collectionPath = Path.Combine(rootPath, "collection");
            var indexPath = Path.Combine(rootPath, "index.json");
            Directory.CreateDirectory(collectionPath);

            var codec = new RecordCodec<string, TestRecord>(policy);
            var context = TestCollectionContext.Create<string, TestRecord, string>(
                static record => record.Value,
                static record => [record.Id],
                StringComparer.OrdinalIgnoreCase,
                StringComparer.OrdinalIgnoreCase,
                codec,
                TestsJsonContext.Default.String,
                TestsJsonContext.Default.String);
            var persistence = TestCollectionContext.CreateJsonIndexPersistence(
                TestsJsonContext.Default.String,
                TestsJsonContext.Default.String,
                StringComparer.OrdinalIgnoreCase);
            var engine = await RecordScopedIndexEngine<string, TestRecord, string>.StartAsync(
                indexPath,
                context,
                persistence,
                autoSaveEnabled: false);
            var index = new CollectionIndex<string, TestRecord, string>(engine, context);
            var effectiveFileStore = fileStore ?? new FileStore();
            var store = new RecordStore<string, TestRecord>(codec, effectiveFileStore);
            var reconciler = new FileReconciler<string, TestRecord, string>(
                context,
                effectiveFileStore,
                store,
                index,
                NullLogger<FileReconciler<string, TestRecord, string>>.Instance);

            return new(rootPath, collectionPath, engine, index, store, reconciler);
        }

        public async Task<string> WriteAsync(string fileName, TestRecord record)
        {
            var path = Path.Combine(CollectionPath, fileName);
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(record, TestsJsonContext.Default.TestRecord));
            return path;
        }

        public async Task<string> WriteLegacyAsync(string fileName, LegacyTestRecord record)
        {
            var path = Path.Combine(CollectionPath, fileName);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record));
            return path;
        }

        public async ValueTask DisposeAsync()
        {
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
}

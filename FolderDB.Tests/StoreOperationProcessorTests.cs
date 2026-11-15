using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Building;
using FolderDB.FileStorage;
using FolderDB.Indexing;
using FolderDB.Indexing.Reconciliation;
using FolderDB.Indexing.State;
using FolderDB.Infrastructure.Exceptions;
using FolderDB.Runtime;
using FolderDB.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderDB.Tests;

public class StoreOperationProcessorTests
{
    private static readonly CollectionDefinition<string, TestRecord, string> _definition = CollectionDefinitionBuilder
        .Create<string, TestRecord>()
        .WithRecordSchema(builder => builder
            .StartWith(1, TestsJsonContext.Default.TestRecord))
        .WithProjection(record => record.Value)
        .UseJsonIndexPersistence(TestsJsonContext.Default.String, TestsJsonContext.Default.String)
        .Build();

    [Fact]
    public async Task TryGetAsync_WhenRecordIsMissing_ReturnsSuccessWithNullRecord()
    {
        await using var harness = await Harness.CreateAsync();

        var result = await harness.Processor.TryGetAsync("missing", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Record);
        Assert.Null(result.FileName);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task TryGetAsync_WhenDecodeFails_ReturnsInvalidRecordAndMarksFileInvalid()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken);

        var fileName = await harness.GetCurrentFileNameAsync("id-1");
        await File.WriteAllTextAsync(Path.Combine(harness.CollectionPath, fileName), "{ invalid json", TestContext.Current.CancellationToken);

        var result = await harness.Processor.TryGetAsync("id-1", TestContext.Current.CancellationToken);

        Assert.Equal(FileErrorReason.Invalid, result.ErrorReason);
        Assert.Null(result.Record);
        Assert.Equal(fileName, result.FileName);
        Assert.NotNull(result.Error?.Exception);
        await harness.AssertHasSingleErrorInfoFileAsync("id-1", FileErrorReason.Invalid);
    }

    [Fact]
    public async Task GetAsync_WhenDecodeFails_ThrowsFileOperationExceptionKeepingOriginalCause()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken);

        var fileName = await harness.GetCurrentFileNameAsync("id-1");
        await File.WriteAllTextAsync(Path.Combine(harness.CollectionPath, fileName), "{ invalid json", TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<FileOperationException>(() => harness.Processor.GetAsync("id-1", TestContext.Current.CancellationToken));

        Assert.Equal(FileErrorReason.Invalid, exception.Reason);
        var conversion = Assert.IsType<RecordConversionException>(exception.InnerException);
        Assert.IsAssignableFrom<JsonException>(conversion.InnerException);
    }

    [Fact]
    public async Task TryGetAsync_WhenReadFailsWithTransientIo_ReturnsFileAccessFailedAndMarksFileUnavailable()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken);
        var fileName = await harness.GetCurrentFileNameAsync("id-1");

        harness.FileStore.ReadExceptionFactory = _ => CreateTransientIOException();

        var result = await harness.Processor.TryGetAsync("id-1", TestContext.Current.CancellationToken);

        Assert.Equal(FileErrorReason.Unavailable, result.ErrorReason);
        Assert.Null(result.Record);
        Assert.Equal(fileName, result.FileName);
        Assert.NotNull(result.Error?.Exception);
        Assert.Equal([(Path.Combine(harness.CollectionPath, fileName), false)], harness.ReconcileRequests);
        await harness.AssertHasSingleErrorInfoFileAsync("id-1", FileErrorReason.Unavailable);
    }

    [Fact]
    public async Task GetAsync_WhenTransientReadFailsOnce_RetriesAndReturnsRecord()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken);

        var attempts = 0;
        harness.FileStore.ReadExceptionFactory = _ => ++attempts == 1
            ? CreateTransientIOException()
            : null;

        var result = await harness.Processor.GetAsync("id-1", TestContext.Current.CancellationToken);

        Assert.Equal(new TestRecord("id-1", 1, "value"), result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task TryGetAsync_WhenFileDisappears_ReturnsSuccessNullAndRemovesFileFromIndex()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken);

        var fileName = await harness.GetCurrentFileNameAsync("id-1");
        File.Delete(Path.Combine(harness.CollectionPath, fileName));

        var result = await harness.Processor.TryGetAsync("id-1", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Record);
        Assert.Equal(fileName, result.FileName);
        Assert.Null(result.Error);
        await harness.AssertIndexEmptyAsync();
    }

    [Fact]
    public async Task UpsertAsync_WhenKnownFileWasMarkedInvalid_WritesSameFileAndRestoresCommittedState()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken);

        var fileName = await harness.GetCurrentFileNameAsync("id-1");
        var filePath = Path.Combine(harness.CollectionPath, fileName);
        await File.WriteAllTextAsync(filePath, "{ invalid json", TestContext.Current.CancellationToken);
        var getResult = await harness.Processor.TryGetAsync("id-1", TestContext.Current.CancellationToken);
        Assert.Equal(FileErrorReason.Invalid, getResult.ErrorReason);

        await harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "restored"), TestContext.Current.CancellationToken);

        Assert.Equal(fileName, await harness.GetCurrentFileNameAsync("id-1"));
        Assert.Equal(new TestRecord("id-1", 1, "restored"), await harness.Processor.GetAsync("id-1", TestContext.Current.CancellationToken));
        await harness.AssertHasSingleRecordAsync("id-1");
    }

    [Fact]
    public async Task TryGetAsync_WhenErrorInfoFileBecomesReadableAgain_ReturnsRecordAndRestoresCommittedState()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken);

        var fileName = await harness.GetCurrentFileNameAsync("id-1");
        var filePath = Path.Combine(harness.CollectionPath, fileName);
        await File.WriteAllTextAsync(filePath, "{ invalid json", TestContext.Current.CancellationToken);
        var invalidResult = await harness.Processor.TryGetAsync("id-1", TestContext.Current.CancellationToken);
        Assert.Equal(FileErrorReason.Invalid, invalidResult.ErrorReason);

        var restored = new TestRecord("id-1", 1, "restored");
        await File.WriteAllTextAsync(
            filePath,
            JsonSerializer.Serialize(restored, TestsJsonContext.Default.TestRecord), TestContext.Current.CancellationToken);

        var result = await harness.Processor.TryGetAsync("id-1", TestContext.Current.CancellationToken);
        OperationResult operationResult = result;

        Assert.True(result.IsSuccess);
        Assert.Equal(restored, result.Record);
        Assert.Equal(fileName, result.FileName);
        Assert.Equal(fileName, operationResult.FileName);
        await harness.AssertHasSingleRecordAsync("id-1");
    }

    [Fact]
    public async Task GetAsync_WhenFileIdChanged_RemovesFileFromRequestedIdIndex()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Processor.UpsertAsync(new TestRecord("id-old", 1, "value"), TestContext.Current.CancellationToken);

        var fileName = await harness.GetCurrentFileNameAsync("id-old");
        var filePath = Path.Combine(harness.CollectionPath, fileName);
        var changedRecord = new TestRecord("id-new", 1, "value-2");
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(changedRecord, TestsJsonContext.Default.TestRecord), TestContext.Current.CancellationToken);

        var result = await harness.Processor.TryGetAsync("id-old", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Record);
        Assert.Equal(fileName, result.FileName);
        Assert.Equal([(filePath, true)], harness.ReconcileRequests);
        await harness.AssertIndexEmptyAsync();
    }

    [Fact]
    public async Task UpsertAsync_WhenWriteFails_CleansReservedFileFromIndex()
    {
        await using var harness = await Harness.CreateAsync();
        harness.FileStore.WriteExceptionFactory = _ => new IOException("write failure");

        var exception = await Assert.ThrowsAsync<FileOperationException>(
            () => harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken));

        Assert.Equal(FileErrorReason.Unavailable, exception.Reason);
        Assert.IsType<IOException>(exception.InnerException);

        await harness.AssertIndexEmptyAsync();
    }

    [Fact]
    public async Task TryUpsertAsync_WhenWriteFails_ReturnsFileAccessFailedAndCleansReservedFileFromIndex()
    {
        await using var harness = await Harness.CreateAsync();
        harness.FileStore.WriteExceptionFactory = _ => new IOException("write failure");

        var result = await harness.Processor.TryUpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken);

        Assert.Equal(FileErrorReason.Unavailable, result.ErrorReason);
        Assert.NotNull(result.FileName);
        Assert.IsType<IOException>(result.Error?.Exception);
        await harness.AssertIndexEmptyAsync();
    }

    [Fact]
    public async Task TryUpsertAsync_WhenLazyFileNameGeneratorFails_ThrowsFileNameGenerationException()
    {
        var definition = CollectionDefinitionBuilder
            .Create<string, TestRecord>()
            .WithFileNaming(ThrowingFileNames)
            .WithRecordSchema(builder => builder
                .StartWith(1, TestsJsonContext.Default.TestRecord))
            .WithProjection(record => record.Value)
            .UseJsonIndexPersistence(TestsJsonContext.Default.String, TestsJsonContext.Default.String)
            .Build();
        await using var harness = await Harness.CreateAsync(definition);

        var ex = await Assert.ThrowsAsync<FileNameGenerationException>(
            () => harness.Processor.TryUpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken));

        Assert.IsType<NotSupportedException>(ex.InnerException);
        await harness.AssertIndexEmptyAsync();

        static IEnumerable<string> ThrowingFileNames(TestRecord _)
        {
            yield return Throw();
        }

        static string Throw()
        {
            throw new NotSupportedException("lazy failure");
        }
    }

    [Fact]
    public async Task UpsertAsync_WhenTransientWriteFailsOnce_RetriesAndWritesRecord()
    {
        await using var harness = await Harness.CreateAsync();

        var attempts = 0;
        harness.FileStore.WriteExceptionFactory = _ => ++attempts == 1
            ? CreateTransientIOException()
            : null;

        await harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
        await harness.AssertHasSingleRecordAsync("id-1");
    }

    [Fact]
    public async Task UpsertAsync_WhenCollectionDirectoryWasDeleted_RecreatesDirectoryAndWritesRecord()
    {
        await using var harness = await Harness.CreateAsync();
        Directory.Delete(harness.CollectionPath, recursive: true);

        await harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(harness.CollectionPath));
        var fileName = await harness.GetCurrentFileNameAsync("id-1");
        Assert.True(File.Exists(Path.Combine(harness.CollectionPath, fileName)));
        await harness.AssertHasSingleRecordAsync("id-1");
    }

    [Fact]
    public async Task DeleteAsync_WhenRecordHasMultipleFiles_DeletesAllFilesAndCleansIndex()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "base"), TestContext.Current.CancellationToken);

        var primaryFileName = await harness.GetCurrentFileNameAsync("id-1");
        var extraFileName = "id-1-extra.json";
        var extraPath = Path.Combine(harness.CollectionPath, extraFileName);
        await File.WriteAllTextAsync(
            extraPath,
            JsonSerializer.Serialize(new TestRecord("id-1", 1, "extra"), TestsJsonContext.Default.TestRecord), TestContext.Current.CancellationToken);

        using (var scope = await harness.Index.EnterSharedScopeAsync(TestContext.Current.CancellationToken))
        using (var recordScope = await scope.LockRecordAsync("id-1", TestContext.Current.CancellationToken))
        {
            var fp = new FileStore().GetFileFingerprint(extraPath);
            var upsertResult = recordScope.Upsert(extraFileName, fp, 1, new TestRecord("id-1", 1, "extra"));
            Assert.NotEqual(IndexOperationResult.BlockedByAnotherId, upsertResult);
        }

        await harness.Processor.DeleteAsync("id-1", TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(harness.CollectionPath, primaryFileName)));
        Assert.False(File.Exists(extraPath));
        await harness.AssertIndexEmptyAsync();
    }

    [Fact]
    public async Task DeleteAsync_WhenTransientDeleteFailsOnce_RetriesAndDeletesRecord()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken);

        var attempts = 0;
        harness.FileStore.DeleteExceptionFactory = _ => ++attempts == 1
            ? CreateTransientIOException()
            : null;

        await harness.Processor.DeleteAsync("id-1", TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
        await harness.AssertIndexEmptyAsync();
    }

    [Fact]
    public async Task TryDeleteAsync_WhenDeleteFails_ReturnsFileAccessFailedAndKeepsRecordInIndex()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"), TestContext.Current.CancellationToken);
        harness.FileStore.DeleteExceptionFactory = _ => new IOException("delete failure");

        var result = await harness.Processor.TryDeleteAsync("id-1", TestContext.Current.CancellationToken);

        Assert.Equal(FileErrorReason.Unavailable, result.ErrorReason);
        Assert.Equal(await harness.GetCurrentFileNameAsync("id-1"), result.FileName);
        Assert.IsType<IOException>(result.Error?.Exception);
        await harness.AssertHasSingleRecordAsync("id-1");
    }

    private static IOException CreateTransientIOException()
    {
        const int windowsSharingViolation = unchecked((int)0x80070020);
        const int unixAgain = 11;
        return OperatingSystem.IsWindows()
            ? new IOException("transient", windowsSharingViolation)
            : new IOException("transient", unixAgain);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _rootPath;

        public string CollectionPath { get; }
        public CollectionIndex<string, TestRecord, string> Index { get; }
        public DelegatingFileStore FileStore { get; }
        public StoreOperationProcessor<string, TestRecord, string> Processor { get; }
        public List<(string Path, bool MinBackoff)> ReconcileRequests { get; }

        private Harness(
            string rootPath,
            string collectionPath,
            CollectionIndex<string, TestRecord, string> index,
            DelegatingFileStore fileStore,
            StoreOperationProcessor<string, TestRecord, string> processor,
            List<(string Path, bool MinBackoff)> reconcileRequests)
        {
            _rootPath = rootPath;
            CollectionPath = collectionPath;
            Index = index;
            FileStore = fileStore;
            Processor = processor;
            ReconcileRequests = reconcileRequests;
        }

        public static async Task<Harness> CreateAsync(CollectionDefinition<string, TestRecord, string>? definition = null)
        {
            definition ??= _definition;
            var rootPath = Directory.CreateTempSubdirectory().FullName;
            var collectionPath = Path.Combine(rootPath, "collection");
            var indexPath = Path.Combine(rootPath, "index-state.json");
            Directory.CreateDirectory(collectionPath);
            var recordCodec = definition.RecordCodecFactory(new RecordCodecContext(NullLoggerFactory.Instance));
            var context = new CollectionContext<string, TestRecord, string>
            {
                Name = definition.Name,
                KeyComparer = definition.KeyComparer,
                KeyEqualityComparer = definition.KeyEqualityComparer,
                FileNameGenerator = definition.FileNameGenerator,
                CreateProjection = definition.CreateProjection,
                RecordCodec = recordCodec,
            };
            var persistence = definition.IndexPersistenceFactory!(
                new CollectionIndexPersistenceContext<string, TestRecord, string>(context, NullLoggerFactory.Instance));

            var indexEngine = await RecordScopedIndexEngine<string, TestRecord, string>.StartAsync(
                indexPath,
                context,
                persistence,
                autoSaveEnabled: false);
            var index = new CollectionIndex<string, TestRecord, string>(indexEngine, context);

            var fileStore = new DelegatingFileStore(new FileStore());
            var reconcileRequests = new List<(string Path, bool MinBackoff)>();
            var store = new RecordStore<string, TestRecord>(
                recordCodec,
                new RetryFileStore(
                    fileStore,
                    readDelays: [TimeSpan.Zero],
                    writeDelays: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]));
            var fileReconciler = new FileReconciler<string, TestRecord, string>(
                context,
                fileStore,
                store,
                index,
                NullLogger<FileReconciler<string, TestRecord, string>>.Instance);
            var processor = new StoreOperationProcessor<string, TestRecord, string>(
                collectionPath,
                context,
                index,
                store,
                maxFileNameReserveAttempts: 5,
                fileReconciler,
                (path, minBackoff) => reconcileRequests.Add((path, minBackoff)),
                logger: null);

            return new Harness(rootPath, collectionPath, index, fileStore, processor, reconcileRequests);
        }

        public async Task<string> GetCurrentFileNameAsync(string id)
        {
            using var scope = await Index.EnterSharedScopeAsync();
            using var recordScope = await scope.LockRecordAsync(id);

            Assert.True(recordScope.TryGetState(out var recordState));
            return recordState.CurrentFileName;
        }

        public async Task AssertIndexEmptyAsync()
        {
            using var scope = await Index.EnterSharedScopeAsync();
            Assert.Empty(scope.Records);
            Assert.Empty(scope.Files);
        }

        public async Task AssertHasSingleRecordAsync(string id)
        {
            using var scope = await Index.EnterSharedScopeAsync();
            var record = Assert.Single(scope.Records).Value;
            Assert.Equal(id, record.Id);
            Assert.Single(scope.Files);
            Assert.Equal(FileIndexStatus.Committed, Assert.Single(scope.Files).Value.Status);
        }

        public async Task AssertHasSingleErrorInfoFileAsync(
            string id,
            FileErrorReason errorReason)
        {
            using var scope = await Index.EnterSharedScopeAsync();
            var record = Assert.Single(scope.Records).Value;
            Assert.Equal(id, record.Id);
            Assert.Equal(Assert.Single(scope.Files).Key, record.CurrentFileName);
            var file = Assert.Single(scope.Files).Value;
            Assert.Equal(id, file.Record.Id);
            Assert.Equal(FileIndexStatus.Committed, file.Status);
            Assert.Equal(errorReason, file.ErrorInfo?.Reason);
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

    private sealed class DelegatingFileStore(FileStore inner) : IFileStore
    {
        public Func<string, Exception?>? WriteExceptionFactory { get; set; }
        public Func<string, Exception?>? ReadExceptionFactory { get; set; }
        public Func<string, Exception?>? DeleteExceptionFactory { get; set; }

        public Task<FileWriteResult> WriteAsync(
            string path,
            Func<Stream, Task> writeAction,
            CancellationToken cancellationToken)
        {
            var ex = WriteExceptionFactory?.Invoke(path);
            return ex is null
                ? inner.WriteAsync(path, writeAction, cancellationToken)
                : Task.FromResult(new FileWriteResult(null, ToFileError(ex)));
        }

        public Task<FileReadResult<T>> ReadAsync<T>(string path, Func<Stream, Task<T>> parseAction, CancellationToken cancellationToken)
        {
            var ex = ReadExceptionFactory?.Invoke(path);
            return ex is null
                ? inner.ReadAsync(path, parseAction, cancellationToken)
                : Task.FromResult(new FileReadResult<T>(
                    default,
                    inner.GetFileFingerprint(path),
                    ToFileError(ex)));
        }

        public Task<FileDeleteResult> DeleteAsync(string path, CancellationToken cancellationToken)
        {
            var ex = DeleteExceptionFactory?.Invoke(path);
            return ex is null
                ? inner.DeleteAsync(path, cancellationToken)
                : Task.FromResult(new FileDeleteResult(ToFileError(ex)));
        }

        public FileFingerprint GetFileFingerprint(string path)
            => inner.GetFileFingerprint(path);

        private static FileErrorPersistence ToPersistence(Exception ex)
        {
            return ex is IOException ioEx && IoErrorCodes.IsTransient(ioEx)
                ? FileErrorPersistence.Transient
                : FileErrorPersistence.Persistent;
        }

        private static FileError ToFileError(Exception ex)
        {
            return new FileError(FileErrorReason.Unavailable, ToPersistence(ex), ex);
        }
    }

}

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

public class DatabaseOperationProcessorTests
{
    private static readonly TableDefinition<string, TestRecord, string> _definition = TableDefinitionBuilder
        .Create<string, TestRecord>()
        .WithRecordSchema(builder => builder
            .StartWith(1, TestsJsonContext.Default.TestRecord))
        .WithProjection(record => record.Value)
        .UseJsonIndexPersistence(TestsJsonContext.Default.String, TestsJsonContext.Default.String)
        .Build();

    [Fact]
    public async Task TryGetAsync_WhenRecordIsMissing_ReturnsSuccessWithNullRecord()
    {
        await using var ctx = await TestContext.CreateAsync();

        var result = await ctx.Processor.TryGetAsync("missing");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Record);
        Assert.Null(result.FileName);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task TryGetAsync_WhenDecodeFails_ReturnsInvalidRecordAndMarksFileInvalid()
    {
        await using var ctx = await TestContext.CreateAsync();
        await ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"));

        var fileName = await ctx.GetCurrentFileNameAsync("id-1");
        await File.WriteAllTextAsync(Path.Combine(ctx.TablePath, fileName), "{ invalid json");

        var result = await ctx.Processor.TryGetAsync("id-1");

        Assert.Equal(FileErrorReason.Invalid, result.ErrorReason);
        Assert.Null(result.Record);
        Assert.Equal(fileName, result.FileName);
        Assert.NotNull(result.Error?.Exception);
        await ctx.AssertHasSingleErrorInfoFileAsync("id-1", FileErrorReason.Invalid);
    }

    [Fact]
    public async Task GetAsync_WhenDecodeFails_ThrowsOriginalException()
    {
        await using var ctx = await TestContext.CreateAsync();
        await ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"));

        var fileName = await ctx.GetCurrentFileNameAsync("id-1");
        await File.WriteAllTextAsync(Path.Combine(ctx.TablePath, fileName), "{ invalid json");

        await Assert.ThrowsAnyAsync<JsonException>(() => ctx.Processor.GetAsync("id-1"));
    }

    [Fact]
    public async Task TryGetAsync_WhenReadFailsWithTransientIo_ReturnsFileAccessFailedAndMarksFileUnavailable()
    {
        await using var ctx = await TestContext.CreateAsync();
        await ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"));
        var fileName = await ctx.GetCurrentFileNameAsync("id-1");

        ctx.FileStore.ReadExceptionFactory = _ => CreateTransientIOException();

        var result = await ctx.Processor.TryGetAsync("id-1");

        Assert.Equal(FileErrorReason.Unavailable, result.ErrorReason);
        Assert.Null(result.Record);
        Assert.Equal(fileName, result.FileName);
        Assert.NotNull(result.Error?.Exception);
        Assert.Equal([(Path.Combine(ctx.TablePath, fileName), false)], ctx.ReconcileRequests);
        await ctx.AssertHasSingleErrorInfoFileAsync("id-1", FileErrorReason.Unavailable);
    }

    [Fact]
    public async Task GetAsync_WhenTransientReadFailsOnce_RetriesAndReturnsRecord()
    {
        await using var ctx = await TestContext.CreateAsync();
        await ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"));

        var attempts = 0;
        ctx.FileStore.ReadExceptionFactory = _ => ++attempts == 1
            ? CreateTransientIOException()
            : null;

        var result = await ctx.Processor.GetAsync("id-1");

        Assert.Equal(new TestRecord("id-1", 1, "value"), result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task TryGetAsync_WhenFileDisappears_ReturnsSuccessNullAndRemovesFileFromIndex()
    {
        await using var ctx = await TestContext.CreateAsync();
        await ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"));

        var fileName = await ctx.GetCurrentFileNameAsync("id-1");
        File.Delete(Path.Combine(ctx.TablePath, fileName));

        var result = await ctx.Processor.TryGetAsync("id-1");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Record);
        Assert.Equal(fileName, result.FileName);
        Assert.Null(result.Error);
        await ctx.AssertIndexEmptyAsync();
    }

    [Fact]
    public async Task UpsertAsync_WhenKnownFileWasMarkedInvalid_WritesSameFileAndRestoresCommittedState()
    {
        await using var ctx = await TestContext.CreateAsync();
        await ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"));

        var fileName = await ctx.GetCurrentFileNameAsync("id-1");
        var filePath = Path.Combine(ctx.TablePath, fileName);
        await File.WriteAllTextAsync(filePath, "{ invalid json");
        var getResult = await ctx.Processor.TryGetAsync("id-1");
        Assert.Equal(FileErrorReason.Invalid, getResult.ErrorReason);

        await ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "restored"));

        Assert.Equal(fileName, await ctx.GetCurrentFileNameAsync("id-1"));
        Assert.Equal(new TestRecord("id-1", 1, "restored"), await ctx.Processor.GetAsync("id-1"));
        await ctx.AssertHasSingleRecordAsync("id-1");
    }

    [Fact]
    public async Task TryGetAsync_WhenErrorInfoFileBecomesReadableAgain_ReturnsRecordAndRestoresCommittedState()
    {
        await using var ctx = await TestContext.CreateAsync();
        await ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"));

        var fileName = await ctx.GetCurrentFileNameAsync("id-1");
        var filePath = Path.Combine(ctx.TablePath, fileName);
        await File.WriteAllTextAsync(filePath, "{ invalid json");
        var invalidResult = await ctx.Processor.TryGetAsync("id-1");
        Assert.Equal(FileErrorReason.Invalid, invalidResult.ErrorReason);

        var restored = new TestRecord("id-1", 1, "restored");
        await File.WriteAllTextAsync(
            filePath,
            JsonSerializer.Serialize(restored, TestsJsonContext.Default.TestRecord));

        var result = await ctx.Processor.TryGetAsync("id-1");
        OperationResult operationResult = result;

        Assert.True(result.IsSuccess);
        Assert.Equal(restored, result.Record);
        Assert.Equal(fileName, result.FileName);
        Assert.Equal(fileName, operationResult.FileName);
        await ctx.AssertHasSingleRecordAsync("id-1");
    }

    [Fact]
    public async Task GetAsync_WhenFileIdChanged_RemovesFileFromRequestedIdIndex()
    {
        await using var ctx = await TestContext.CreateAsync();
        await ctx.Processor.UpsertAsync(new TestRecord("id-old", 1, "value"));

        var fileName = await ctx.GetCurrentFileNameAsync("id-old");
        var filePath = Path.Combine(ctx.TablePath, fileName);
        var changedRecord = new TestRecord("id-new", 1, "value-2");
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(changedRecord, TestsJsonContext.Default.TestRecord));

        var result = await ctx.Processor.TryGetAsync("id-old");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Record);
        Assert.Equal(fileName, result.FileName);
        Assert.Equal([(filePath, true)], ctx.ReconcileRequests);
        await ctx.AssertIndexEmptyAsync();
    }

    [Fact]
    public async Task UpsertAsync_WhenWriteFails_CleansReservedFileFromIndex()
    {
        await using var ctx = await TestContext.CreateAsync();
        ctx.FileStore.WriteExceptionFactory = _ => new IOException("write failure");

        await Assert.ThrowsAsync<IOException>(() => ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "value")));

        await ctx.AssertIndexEmptyAsync();
    }

    [Fact]
    public async Task TryUpsertAsync_WhenWriteFails_ReturnsFileAccessFailedAndCleansReservedFileFromIndex()
    {
        await using var ctx = await TestContext.CreateAsync();
        ctx.FileStore.WriteExceptionFactory = _ => new IOException("write failure");

        var result = await ctx.Processor.TryUpsertAsync(new TestRecord("id-1", 1, "value"));

        Assert.Equal(FileErrorReason.Unavailable, result.ErrorReason);
        Assert.NotNull(result.FileName);
        Assert.IsType<IOException>(result.Error?.Exception);
        await ctx.AssertIndexEmptyAsync();
    }

    [Fact]
    public async Task TryUpsertAsync_WhenLazyFileNameGeneratorFails_ThrowsFileNameGenerationException()
    {
        var definition = TableDefinitionBuilder
            .Create<string, TestRecord>()
            .WithFileNaming(ThrowingFileNames)
            .WithRecordSchema(builder => builder
                .StartWith(1, TestsJsonContext.Default.TestRecord))
            .WithProjection(record => record.Value)
            .UseJsonIndexPersistence(TestsJsonContext.Default.String, TestsJsonContext.Default.String)
            .Build();
        await using var ctx = await TestContext.CreateAsync(definition);

        var ex = await Assert.ThrowsAsync<FileNameGenerationException>(
            () => ctx.Processor.TryUpsertAsync(new TestRecord("id-1", 1, "value")));

        Assert.IsType<NotSupportedException>(ex.InnerException);
        await ctx.AssertIndexEmptyAsync();

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
        await using var ctx = await TestContext.CreateAsync();

        var attempts = 0;
        ctx.FileStore.WriteExceptionFactory = _ => ++attempts == 1
            ? CreateTransientIOException()
            : null;

        await ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"));

        Assert.Equal(2, attempts);
        await ctx.AssertHasSingleRecordAsync("id-1");
    }

    [Fact]
    public async Task UpsertAsync_WhenTableDirectoryWasDeleted_RecreatesDirectoryAndWritesRecord()
    {
        await using var ctx = await TestContext.CreateAsync();
        Directory.Delete(ctx.TablePath, recursive: true);

        await ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"));

        Assert.True(Directory.Exists(ctx.TablePath));
        var fileName = await ctx.GetCurrentFileNameAsync("id-1");
        Assert.True(File.Exists(Path.Combine(ctx.TablePath, fileName)));
        await ctx.AssertHasSingleRecordAsync("id-1");
    }

    [Fact]
    public async Task DeleteAsync_WhenRecordHasMultipleFiles_DeletesAllFilesAndCleansIndex()
    {
        await using var ctx = await TestContext.CreateAsync();
        await ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "base"));

        var primaryFileName = await ctx.GetCurrentFileNameAsync("id-1");
        var extraFileName = "id-1-extra.json";
        var extraPath = Path.Combine(ctx.TablePath, extraFileName);
        await File.WriteAllTextAsync(
            extraPath,
            JsonSerializer.Serialize(new TestRecord("id-1", 1, "extra"), TestsJsonContext.Default.TestRecord));

        using (var scope = await ctx.Index.EnterSharedScopeAsync())
        using (var recordScope = await scope.LockRecordAsync("id-1"))
        {
            var fp = new FileStore().GetFileFingerprint(extraPath);
            var upsertResult = recordScope.Upsert(extraFileName, fp, 1, new TestRecord("id-1", 1, "extra"));
            Assert.NotEqual(IndexOperationResult.BlockedByAnotherId, upsertResult);
        }

        await ctx.Processor.DeleteAsync("id-1");

        Assert.False(File.Exists(Path.Combine(ctx.TablePath, primaryFileName)));
        Assert.False(File.Exists(extraPath));
        await ctx.AssertIndexEmptyAsync();
    }

    [Fact]
    public async Task DeleteAsync_WhenTransientDeleteFailsOnce_RetriesAndDeletesRecord()
    {
        await using var ctx = await TestContext.CreateAsync();
        await ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"));

        var attempts = 0;
        ctx.FileStore.DeleteExceptionFactory = _ => ++attempts == 1
            ? CreateTransientIOException()
            : null;

        await ctx.Processor.DeleteAsync("id-1");

        Assert.Equal(2, attempts);
        await ctx.AssertIndexEmptyAsync();
    }

    [Fact]
    public async Task TryDeleteAsync_WhenDeleteFails_ReturnsFileAccessFailedAndKeepsRecordInIndex()
    {
        await using var ctx = await TestContext.CreateAsync();
        await ctx.Processor.UpsertAsync(new TestRecord("id-1", 1, "value"));
        ctx.FileStore.DeleteExceptionFactory = _ => new IOException("delete failure");

        var result = await ctx.Processor.TryDeleteAsync("id-1");

        Assert.Equal(FileErrorReason.Unavailable, result.ErrorReason);
        Assert.Equal(await ctx.GetCurrentFileNameAsync("id-1"), result.FileName);
        Assert.IsType<IOException>(result.Error?.Exception);
        await ctx.AssertHasSingleRecordAsync("id-1");
    }

    private static IOException CreateTransientIOException()
    {
        const int windowsSharingViolation = unchecked((int)0x80070020);
        const int unixAgain = 11;
        return OperatingSystem.IsWindows()
            ? new IOException("transient", windowsSharingViolation)
            : new IOException("transient", unixAgain);
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private readonly string _rootPath;

        public string TablePath { get; }
        public TableIndex<string, TestRecord, string> Index { get; }
        public DelegatingFileStore FileStore { get; }
        public DatabaseOperationProcessor<string, TestRecord, string> Processor { get; }
        public List<(string Path, bool MinBackoff)> ReconcileRequests { get; }

        private TestContext(
            string rootPath,
            string tablePath,
            TableIndex<string, TestRecord, string> index,
            DelegatingFileStore fileStore,
            DatabaseOperationProcessor<string, TestRecord, string> processor,
            List<(string Path, bool MinBackoff)> reconcileRequests)
        {
            _rootPath = rootPath;
            TablePath = tablePath;
            Index = index;
            FileStore = fileStore;
            Processor = processor;
            ReconcileRequests = reconcileRequests;
        }

        public static async Task<TestContext> CreateAsync(TableDefinition<string, TestRecord, string>? definition = null)
        {
            definition ??= _definition;
            var rootPath = Directory.CreateTempSubdirectory().FullName;
            var tablePath = Path.Combine(rootPath, "table");
            var indexPath = Path.Combine(rootPath, "index-state.json");
            Directory.CreateDirectory(tablePath);
            var recordCodec = definition.RecordCodecFactory(new RecordCodecContext(NullLoggerFactory.Instance));
            var context = new TableContext<string, TestRecord, string>
            {
                Name = definition.Name,
                KeyComparer = definition.KeyComparer,
                KeyEqualityComparer = definition.KeyEqualityComparer,
                FileNameGenerator = definition.FileNameGenerator,
                CreateProjection = definition.CreateProjection,
                RecordCodec = recordCodec,
            };
            var persistence = definition.IndexPersistenceFactory!(
                new TableIndexPersistenceContext<string, TestRecord, string>(context, NullLoggerFactory.Instance));

            var indexEngine = await RecordScopedIndexEngine<string, TestRecord, string>.StartAsync(
                indexPath,
                context,
                persistence,
                autoSaveEnabled: false);
            var index = new TableIndex<string, TestRecord, string>(indexEngine, context);

            var fileStore = new DelegatingFileStore(new FileStore());
            var reconcileRequests = new List<(string Path, bool MinBackoff)>();
            var store = new RecordStore<string, TestRecord>(
                recordCodec,
                new RetryFileStore(
                    fileStore,
                    new RetryFileStoreOptions
                    {
                        Read = new RetryFileStoreOperationOptions { MaxAttempts = 2 },
                        Write = new RetryFileStoreOperationOptions { MaxAttempts = 5 },
                        Delete = new RetryFileStoreOperationOptions { MaxAttempts = 5 }
                    },
                    null));
            var fileReconciler = new FileReconciler<string, TestRecord, string>(
                context,
                fileStore,
                store,
                index,
                NullLogger<FileReconciler<string, TestRecord, string>>.Instance);
            var processor = new DatabaseOperationProcessor<string, TestRecord, string>(
                tablePath,
                context,
                index,
                store,
                maxFileNameReserveAttempts: 5,
                fileReconciler,
                (path, minBackoff) => reconcileRequests.Add((path, minBackoff)),
                logger: null);

            return new TestContext(rootPath, tablePath, index, fileStore, processor, reconcileRequests);
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
            CancellationToken ct)
        {
            var ex = WriteExceptionFactory?.Invoke(path);
            return ex is null
                ? inner.WriteAsync(path, writeAction, ct)
                : Task.FromResult(new FileWriteResult(null, ToFileError(ex)));
        }

        public Task<FileReadResult<T>> ReadAsync<T>(string path, Func<Stream, Task<T>> parseAction, CancellationToken ct)
        {
            var ex = ReadExceptionFactory?.Invoke(path);
            return ex is null
                ? inner.ReadAsync(path, parseAction, ct)
                : Task.FromResult(new FileReadResult<T>(
                    default,
                    inner.GetFileFingerprint(path),
                    ToFileError(ex)));
        }

        public Task<FileDeleteResult> DeleteAsync(string path, CancellationToken ct)
        {
            var ex = DeleteExceptionFactory?.Invoke(path);
            return ex is null
                ? inner.DeleteAsync(path, ct)
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

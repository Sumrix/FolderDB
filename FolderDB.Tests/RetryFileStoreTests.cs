using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.FileStorage;

namespace FolderDB.Tests;

public class RetryFileStoreTests
{
    [Fact]
    public async Task WriteAsync_WhenRetriesAreExhausted_ReturnsLastException()
    {
        var inner = new TransientWriteFileStore(failuresBeforeSuccess: 10);
        var retryStore = new RetryFileStore(
            inner,
            readDelays: [],
            writeDelays: [TimeSpan.Zero]);

        var result = await retryStore.WriteAsync("record.json", _ => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(FileErrorPersistence.Transient, result.Error?.Persistence);
        Assert.False(result.IsSuccess);
        Assert.Equal(2, inner.WriteAttempts);
        var exception = Assert.IsType<IOException>(result.Error?.Exception);
        Assert.Equal("transient write attempt 2", exception.Message);
    }

    [Fact]
    public async Task WriteAsync_TakesDelaysFromStartForEachOperation()
    {
        var inner = new TransientWriteFileStore(failuresBeforeSuccess: 10);
        var retryStore = new RetryFileStore(
            inner,
            readDelays: [],
            writeDelays: [TimeSpan.Zero, TimeSpan.Zero]);

        await retryStore.WriteAsync("first.json", _ => Task.CompletedTask, CancellationToken.None);
        await retryStore.WriteAsync("second.json", _ => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(6, inner.WriteAttempts);
    }

    // A delete has no list of its own: it follows the write delays, not the read ones.
    [Fact]
    public async Task DeleteAsync_RetriesByWriteDelays()
    {
        var inner = new TransientWriteFileStore(failuresBeforeSuccess: 10);
        var retryStore = new RetryFileStore(
            inner,
            readDelays: [TimeSpan.Zero],
            writeDelays: [TimeSpan.Zero, TimeSpan.Zero]);

        var result = await retryStore.DeleteAsync("record.json", CancellationToken.None);

        Assert.Equal(FileErrorPersistence.Transient, result.Error?.Persistence);
        Assert.Equal(3, inner.DeleteAttempts);
    }

    private sealed class TransientWriteFileStore(int failuresBeforeSuccess) : IFileStore
    {
        public int WriteAttempts { get; private set; }
        public int DeleteAttempts { get; private set; }

        public Task<FileWriteResult> WriteAsync(
            string path,
            Func<Stream, Task> writeAction,
            CancellationToken cancellationToken)
        {
            WriteAttempts++;
            return Task.FromResult(
                WriteAttempts <= failuresBeforeSuccess
                    ? new FileWriteResult(
                        null,
                        new FileError(
                            FileErrorReason.Unavailable,
                            FileErrorPersistence.Transient,
                            new IOException($"transient write attempt {WriteAttempts}")))
                    : new FileWriteResult(
                        new FileFingerprint(DateTime.UtcNow, 1, Exists: true)));
        }

        public Task<FileReadResult<T>> ReadAsync<T>(
            string path,
            Func<Stream, Task<T>> parseAction,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<FileDeleteResult> DeleteAsync(string path, CancellationToken cancellationToken)
        {
            DeleteAttempts++;
            return Task.FromResult(
                DeleteAttempts <= failuresBeforeSuccess
                    ? new FileDeleteResult(
                        new FileError(
                            FileErrorReason.Unavailable,
                            FileErrorPersistence.Transient,
                            new IOException($"transient delete attempt {DeleteAttempts}")))
                    : new FileDeleteResult());
        }

        public FileFingerprint GetFileFingerprint(string path)
        {
            return default;
        }
    }
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.FileStorage;

namespace FolderDB.Tests;

public class RetryFileStoreTests
{
    // Guards the explicit NaN check in the setter: ThrowIfLessThan lets NaN through, because every
    // comparison with it is false, and the delay computed from it throws deep inside a file operation.
    [Fact]
    public void BackoffMultiplier_WhenNaN_Throws()
    {
        var options = new RetryFileStoreOperationOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.BackoffMultiplier = double.NaN);
    }

    [Fact]
    public async Task WriteAsync_WhenRetriesAreExhausted_ReturnsLastException()
    {
        var inner = new TransientWriteFileStore(failuresBeforeSuccess: 10);
        var retryStore = new RetryFileStore(
            inner,
            new RetryFileStoreOptions
            {
                Write = new RetryFileStoreOperationOptions
                {
                    MaxAttempts = 2,
                    Delay = TimeSpan.Zero
                }
            });

        var result = await retryStore.WriteAsync("record.json", _ => Task.CompletedTask, CancellationToken.None);

        Assert.Equal(FileErrorPersistence.Transient, result.Error?.Persistence);
        Assert.False(result.IsSuccess);
        Assert.Equal(2, inner.WriteAttempts);
        var exception = Assert.IsType<IOException>(result.Error?.Exception);
        Assert.Equal("transient write attempt 2", exception.Message);
    }

    private sealed class TransientWriteFileStore(int failuresBeforeSuccess) : IFileStore
    {
        public int WriteAttempts { get; private set; }

        public Task<FileWriteResult> WriteAsync(
            string path,
            Func<Stream, Task> writeAction,
            CancellationToken ct)
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

        public Task<FileReadResult<T>> ReadAsync<T>(string path, Func<Stream, Task<T>> parseAction, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task<FileDeleteResult> DeleteAsync(string path, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public FileFingerprint GetFileFingerprint(string path)
        {
            return default;
        }
    }
}

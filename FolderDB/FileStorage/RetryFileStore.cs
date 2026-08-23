using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Infrastructure.Logging;
using FolderDB.Retry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderDB.FileStorage;

public sealed class RetryFileStore(
    IFileStore inner,
    RetryFileStoreOptions? options = null,
    ILogger<RetryFileStore>? logger = null) : IFileStore
{
    private readonly RetryFileStoreOptions _options = options ?? RetryFileStoreOptions.Default;
    private readonly ILogger<RetryFileStore> _logger = logger ?? NullLogger<RetryFileStore>.Instance;

    public async Task<FileWriteResult> WriteAsync(
        string path,
        Func<Stream, Task> writeAction,
        CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginMethodScope();

        return await ExecuteWithRetryAsync(
            path,
            _options.Write,
            () => inner.WriteAsync(path, writeAction, cancellationToken),
            cancellationToken);
    }

    public async Task<FileReadResult<T>> ReadAsync<T>(string path, Func<Stream, Task<T>> parseAction, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginMethodScope();

        return await ExecuteWithRetryAsync(
            path,
            _options.Read,
            () => inner.ReadAsync(path, parseAction, cancellationToken),
            cancellationToken);
    }

    public async Task<FileDeleteResult> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginMethodScope();

        return await ExecuteWithRetryAsync(
            path,
            _options.Delete,
            () => inner.DeleteAsync(path, cancellationToken),
            cancellationToken);
    }

    public FileFingerprint GetFileFingerprint(string path)
    {
        return inner.GetFileFingerprint(path);
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        string path,
        RetryFileStoreOperationOptions options,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
        where T : IFileOperationResult
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await action();
            if (result.Error?.Persistence != FileErrorPersistence.Transient || attempt >= options.MaxAttempts)
                return result;

            var delay = GetDelay(options, attempt);

            _logger.LogTrace(
                "Retrying: path=\"{Path}\" attempt={Attempt} maxAttempts={MaxAttempts} delayMs={DelayMs}",
                path,
                attempt + 1,
                options.MaxAttempts,
                delay.TotalMilliseconds);

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private TimeSpan GetDelay(RetryFileStoreOperationOptions options, int failedAttempt)
    {
        var multiplier = Math.Pow(options.BackoffMultiplier, failedAttempt - 1);
        var delayMs = options.Delay.TotalMilliseconds * multiplier;
        return delayMs >= RetryConsts.MaxDelay.TotalMilliseconds
            ? RetryConsts.MaxDelay
            : TimeSpan.FromMilliseconds(delayMs);
    }
}

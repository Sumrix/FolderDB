using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Infrastructure.Logging;
using FolderDB.Retry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderDB.FileStorage;

/// <summary>
/// Provides safe low-level file operations, repeating an operation after a transient failure.
/// </summary>
public sealed class RetryFileStore : IFileStore
{
    private readonly IFileStore _inner;
    private readonly RetryFileStoreOptions _options;
    private readonly ILogger<RetryFileStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetryFileStore"/> class.
    /// </summary>
    /// <param name="inner">
    /// The inner store which is responsible for performing the file operations that are retried.
    /// </param>
    /// <param name="options">Options to control the retry behavior.</param>
    /// <param name="loggerFactory">The factory used to create the store's logger.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    public RetryFileStore(
        IFileStore inner,
        RetryFileStoreOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? RetryFileStoreOptions.Default;
        _logger = loggerFactory?.CreateLogger<RetryFileStore>() ?? NullLogger<RetryFileStore>.Instance;
    }

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public async Task<FileWriteResult> WriteAsync(
        string path,
        Func<Stream, Task> writeAction,
        CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginMethodScope();

        return await ExecuteWithRetryAsync(
            path,
            _options.Write,
            () => _inner.WriteAsync(path, writeAction, cancellationToken),
            cancellationToken);
    }

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public async Task<FileReadResult<T>> ReadAsync<T>(string path, Func<Stream, Task<T>> parseAction, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginMethodScope();

        return await ExecuteWithRetryAsync(
            path,
            _options.Read,
            () => _inner.ReadAsync(path, parseAction, cancellationToken),
            cancellationToken);
    }

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public async Task<FileDeleteResult> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginMethodScope();

        return await ExecuteWithRetryAsync(
            path,
            _options.Delete,
            () => _inner.DeleteAsync(path, cancellationToken),
            cancellationToken);
    }

    public FileFingerprint GetFileFingerprint(string path)
    {
        return _inner.GetFileFingerprint(path);
    }

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
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
            if (result.Error?.Persistence != FileErrorPersistence.Transient)
                return result;

            if (attempt >= options.MaxAttempts)
            {
                _logger.LogError(
                    "Retries exhausted: path=\"{Path}\" attempts={Attempts} errorReason={ErrorReason}",
                    path,
                    attempt,
                    result.Error.Reason);
                return result;
            }

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

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Infrastructure.Helpers;
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
    private readonly IEnumerable<TimeSpan>? _readDelays;
    private readonly IEnumerable<TimeSpan>? _writeDelays;
    private readonly IEnumerable<TimeSpan>? _deleteDelays;
    private readonly ILogger<RetryFileStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetryFileStore"/> class.
    /// </summary>
    /// <param name="inner">
    /// The inner store which is responsible for performing the file operations that are retried.
    /// </param>
    /// <param name="retry">The retry delays of each operation.</param>
    /// <param name="loggerFactory">The factory used to create the store's logger.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    public RetryFileStore(
        IFileStore inner,
        Dictionary<FileOperation, IEnumerable<TimeSpan>?>? retry,
        ILoggerFactory? loggerFactory = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _readDelays = retry?.GetValueOrDefault(FileOperation.Read);
        _writeDelays = retry?.GetValueOrDefault(FileOperation.Write);
        _deleteDelays = retry?.GetValueOrDefault(FileOperation.Delete);
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
            _writeDelays,
            () => _inner.WriteAsync(path, writeAction, cancellationToken),
            cancellationToken);
    }

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public async Task<FileReadResult<T>> ReadAsync<T>(string path, Func<Stream, Task<T>> parseAction, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginMethodScope();

        return await ExecuteWithRetryAsync(
            path,
            _readDelays,
            () => _inner.ReadAsync(path, parseAction, cancellationToken),
            cancellationToken);
    }

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    public async Task<FileDeleteResult> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginMethodScope();

        return await ExecuteWithRetryAsync(
            path,
            _deleteDelays,
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
        IEnumerable<TimeSpan>? delays,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
        where T : IFileOperationResult
    {
        // Taken on the first transient failure, so an operation that succeeds allocates nothing.
        IEnumerator<TimeSpan>? delayEnumerator = null;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await action();
                if (result.Error?.Persistence != FileErrorPersistence.Transient)
                    return result;

                TimeSpan delay;
                try
                {
                    delayEnumerator ??= delays?.GetEnumerator();
                    if (delayEnumerator is null || !delayEnumerator.MoveNext())
                    {
                        _logger.LogError(
                            "Retries exhausted: path=\"{Path}\" attempts={Attempts} errorReason={ErrorReason}",
                            path,
                            attempt,
                            result.Error.Reason);
                        return result;
                    }

                    delay = delayEnumerator.Current;
                }
                catch (Exception ex)
                {
                    // A user sequence may throw; the operation then fails as if the retries were exhausted.
                    _logger.LogError(
                        ex,
                        "Retry delays failed: path=\"{Path}\" attempts={Attempts} errorReason={ErrorReason}",
                        path,
                        attempt,
                        result.Error.Reason);
                    return result;
                }

                if (delay > RetryConsts.MaxDelay)
                    delay = RetryConsts.MaxDelay;

                _logger.LogTrace(
                    "Retrying: path=\"{Path}\" attempt={Attempt} delayMs={DelayMs}",
                    path,
                    attempt + 1,
                    delay.TotalMilliseconds);

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        finally
        {
            DisposeHelper.SafeDispose(delayEnumerator);
        }
    }
}

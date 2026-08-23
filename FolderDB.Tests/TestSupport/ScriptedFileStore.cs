using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.FileStorage;
using FolderDB.Infrastructure.Helpers;

namespace FolderDB.Tests.TestSupport;

internal sealed class ScriptedFileStore : IFileStore
{
    private readonly IFileStore _inner;
    private readonly Dictionary<string, Queue<Func<FileFingerprint>>> _fingerprintResults =
        new(PathHelper.OSDependedPathComparer);
    private readonly Dictionary<string, Queue<Func<FileReadFailureResult>>> _readFailureResults =
        new(PathHelper.OSDependedPathComparer);
    private readonly Dictionary<string, Queue<Func<FileWriteResult>>> _writeResults =
        new(PathHelper.OSDependedPathComparer);

    public ScriptedFileStore(IFileStore? inner = null)
    {
        _inner = inner ?? new FileStore();
    }

    public void EnqueueFingerprintResult(string path, FileFingerprint result)
    {
        EnqueueFingerprintResult(path, () => result);
    }

    public void EnqueueFingerprintResult(string path, Func<FileFingerprint> resultFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(resultFactory);

        if (!_fingerprintResults.TryGetValue(path, out var results))
        {
            results = [];
            _fingerprintResults[path] = results;
        }

        results.Enqueue(resultFactory);
    }

    public void EnqueueWriteResult(string path, FileWriteResult result)
    {
        EnqueueWriteResult(path, () => result);
    }

    public void EnqueueReadAccessResult(
        string path,
        FileErrorPersistence persistence,
        Exception? exception = null)
    {
        EnqueueReadAccessResult(path, persistence, _inner.GetFileFingerprint(path), exception);
    }

    public void EnqueueReadAccessResult(
        string path,
        FileErrorPersistence persistence,
        FileFingerprint fingerprint,
        Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!_readFailureResults.TryGetValue(path, out var results))
        {
            results = [];
            _readFailureResults[path] = results;
        }

        results.Enqueue(() => new FileReadFailureResult(persistence, fingerprint, exception));
    }

    public void EnqueueWriteResult(string path, Func<FileWriteResult> resultFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(resultFactory);

        if (!_writeResults.TryGetValue(path, out var results))
        {
            results = [];
            _writeResults[path] = results;
        }

        results.Enqueue(resultFactory);
    }

    public Task<FileWriteResult> WriteAsync(
        string path,
        Func<Stream, Task> writeAction,
        CancellationToken cancellationToken)
    {
        if (_writeResults.TryGetValue(path, out var scriptedResults) && scriptedResults.Count > 0)
        {
            return Task.FromResult(scriptedResults.Dequeue().Invoke());
        }

        return _inner.WriteAsync(path, writeAction, cancellationToken);
    }

    public Task<FileReadResult<T>> ReadAsync<T>(
        string path,
        Func<Stream, Task<T>> parseAction,
        CancellationToken cancellationToken)
    {
        if (_readFailureResults.TryGetValue(path, out var scriptedResults) && scriptedResults.Count > 0)
        {
            var result = scriptedResults.Dequeue().Invoke();
            return Task.FromResult(new FileReadResult<T>(
                default,
                result.Fingerprint,
                CreateError(result.Persistence, result.Exception)));
        }

        return _inner.ReadAsync(path, parseAction, cancellationToken);
    }

    public Task<FileDeleteResult> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        return _inner.DeleteAsync(path, cancellationToken);
    }

    public FileFingerprint GetFileFingerprint(string path)
    {
        if (_fingerprintResults.TryGetValue(path, out var scriptedResults) && scriptedResults.Count > 0)
        {
            return scriptedResults.Dequeue().Invoke();
        }

        return _inner.GetFileFingerprint(path);
    }

    private readonly record struct FileReadFailureResult(
        FileErrorPersistence Persistence,
        FileFingerprint Fingerprint,
        Exception? Exception);

    private static FileError? CreateError(FileErrorPersistence persistence, Exception? exception)
    {
        return exception is null
            ? null
            : new FileError(FileErrorReason.Unavailable, persistence, exception);
    }
}

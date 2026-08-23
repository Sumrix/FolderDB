using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nito.AsyncEx;

namespace FolderDB.Indexing;

public class AutoSaver : IAsyncDisposable
{
    private readonly string _filePath;
    private readonly string _tmpPath;
    private readonly string _dirPath;
    private readonly TimeSpan _savingInterval;
    private readonly Func<CancellationToken, byte[]> _getSnapshot;
    private readonly ILogger<AutoSaver> _logger;

    // Incremented on every in-memory state change, triggers a save.
    private long _changeVersion;
    private long _savedVersion;

    // Signals the syncing loop about pending saves.
    private readonly AsyncAutoResetEvent _pendingSignal = new();
    // Locks write operations to disk.
    private readonly AsyncLock _writeLock = new();
    // Locks lifecycle operations (start/stop).
    private readonly AsyncLock _lifecycleLock = new();

    private Task? _syncTask;
    private CancellationTokenSource? _syncCts;

    private bool _disposed;
    private bool _syncing;

    public Action<Exception>? OnError { get; set; }

    private enum SaveResult { NoChanges, Saved, Failed }

    public AutoSaver(
        string filePath,
        TimeSpan savingInterval,
        Func<CancellationToken, byte[]> getSnapshot,
        ILogger<AutoSaver>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(getSnapshot);

        _filePath = filePath;
        _tmpPath = filePath + ".tmp";
        _dirPath = Path.GetDirectoryName(filePath) ?? "";
        _savingInterval = savingInterval;
        _getSnapshot = getSnapshot;
        _logger = logger ?? NullLogger<AutoSaver>.Instance;
    }

    public void MarkDirty()
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _changeVersion);
        _pendingSignal.Set();
    }

    public void Start()
    {
        ThrowIfDisposed();
        using var _ = _lifecycleLock.Lock();
        using var __ = BeginScope();

        if (_syncing)
        {
            return;
        }
        _syncing = true;

        _syncCts = new CancellationTokenSource();
        _syncTask = SyncLoopAsync(_syncCts.Token);

        _logger.LogDebug("Started: interval={Interval:g} file=\"{FilePath}\"", _savingInterval, _filePath);
    }

    private async Task StopAsync()
    {
        using var _ = await _lifecycleLock.LockAsync();
        using var __ = BeginScope();

        if (!_syncing)
        {
            return;
        }
        _syncing = false;

        _logger.LogTrace("Stopping");

        _syncCts?.Cancel();
        if (_syncTask != null)
        {
            await _syncTask;
        }

        _syncCts?.Dispose();
        _syncCts = null;
        _syncTask = null;

        _logger.LogDebug("Stopped: pendingVersion={changeVersion} savedVersion={saveVersion}", _changeVersion, _savedVersion);
    }

    private IDisposable? BeginScope([CallerMemberName] string m = "") =>
        _logger.BeginMethodScope(m);

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await WriteSnapshotAsync(cancellationToken);
    }

    private async Task SyncLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var saveResult = SaveResult.NoChanges;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (saveResult != SaveResult.Failed)
                {
                    await _pendingSignal.WaitAsync(cancellationToken);
                }

                await Task.Delay(_savingInterval, cancellationToken);

                saveResult = await WriteSnapshotAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<SaveResult> WriteSnapshotAsync(CancellationToken cancellationToken)
    {
        using var _ = await _writeLock.LockAsync(cancellationToken);
        using var __ = BeginScope();

        long changeVersion = Interlocked.Read(ref _changeVersion);

        // Nothing to save
        if (changeVersion == _savedVersion)
        {
            return SaveResult.NoChanges;
        }

        try
        {

            var payload = _getSnapshot(cancellationToken);

            Directory.CreateDirectory(_dirPath);

            // Atomic write: temp file, then Move
            await File.WriteAllBytesAsync(_tmpPath, payload, cancellationToken);
            File.Move(_tmpPath, _filePath, overwrite: true);

            // Update saved version
            _savedVersion = changeVersion;

            _logger.LogDebug("Saved: version={changeVersion} file=\"{FilePath}\"", changeVersion, _filePath);
            return SaveResult.Saved;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Save failed: version={changeVersion} file=\"{FilePath}\"", changeVersion, _filePath);
            TryNotifyError(ex);
            return SaveResult.Failed;
        }
    }

    private void TryNotifyError(Exception ex)
    {
        try
        {
            OnError?.Invoke(ex);
        }
        catch (Exception callbackEx)
        {
            _logger.LogError(callbackEx, "Error handler failed: file=\"{FilePath}\"", _filePath);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Interlocked.CompareExchange(ref _disposed, true, true), this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, true))
        {
            return;
        }

        try
        {
            await StopAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TryNotifyError(ex);
        }
    }
}

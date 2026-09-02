using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Indexing;

namespace FolderDB.Tests;

public class AutoSaverTests : IDisposable
{
    private static readonly TimeSpan _saveInterval = TimeSpan.FromMilliseconds(50);
    private const int _testTimeoutMs = 10_000;
    private const int _flushPeriodMs = 250;

    private readonly string _dir;
    private readonly string _path;

    public AutoSaverTests()
    {
        _dir = Directory.CreateTempSubdirectory().FullName;
        _path = Path.Combine(_dir, "state.bin");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task FlushAsync_WhenDirty_WritesSnapshotToDisk()
    {
        var snapshot = new byte[] { 1, 2, 3, 4, 5 };

        await using var saver = new AutoSaver(_path, _saveInterval, _ => snapshot);

        saver.MarkDirty();
        await saver.FlushAsync(CancellationToken.None);

        Assert.Equal(snapshot, await File.ReadAllBytesAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FlushAsync_WhenNoChanges_DoesNotRewriteFile()
    {
        var snapshot = new byte[] { 1, 2, 3, 4, 5 };

        await using var saver = new AutoSaver(_path, _saveInterval, _ => snapshot);

        saver.MarkDirty();
        await saver.FlushAsync(CancellationToken.None);
        var ts1 = File.GetLastWriteTimeUtc(_path);

        // no MarkDirty
        await saver.FlushAsync(CancellationToken.None);
        var ts2 = File.GetLastWriteTimeUtc(_path);

        Assert.Equal(snapshot, await File.ReadAllBytesAsync(_path, TestContext.Current.CancellationToken));
        Assert.Equal(ts1, ts2);
    }

    [Fact(Timeout = _testTimeoutMs)]
    public async Task Start_ThenMarkDirty_SavesInBackgroundAfterInterval()
    {
        var snapshot = new byte[] { 1, 2, 3, 4, 5 };
        using var changed = new FileChangeLatch(_path);

        await using var saver = new AutoSaver(_path, _saveInterval, _ => snapshot);

        saver.Start();
        saver.MarkDirty();

        Assert.Equal(snapshot, await changed.ReadAfterNextWriteAsync());
    }

    [Fact(Timeout = _testTimeoutMs)]
    public async Task BackgroundLoop_DebouncesManyDirtyMarks_IntoSingleWritePerInterval()
    {
        var probe = new SnapshotProbe(BitConverter.GetBytes);
        using var changed = new FileChangeLatch(_path);

        await using var saver = new AutoSaver(_path, _saveInterval, probe.Take);

        saver.Start();

        // burst #1 inside one interval
        for (int i = 0; i < 50; i++) saver.MarkDirty();

        Assert.Equal(1, BitConverter.ToInt32(await changed.ReadAfterNextWriteAsync(_flushPeriodMs), 0));
        Assert.Equal(1, probe.Count);

        // burst #2 -> the flush period above left the loop parked again, so this burst falls
        // inside one interval just like the first.
        for (int i = 0; i < 50; i++) saver.MarkDirty();

        Assert.Equal(2, BitConverter.ToInt32(await changed.ReadAfterNextWriteAsync(_flushPeriodMs), 0));
        Assert.Equal(2, probe.Count);
    }

    [Fact(Timeout = _testTimeoutMs)]
    public async Task DisposeAsync_IsIdempotent_AndStopsFurtherBackgroundWrites()
    {
        var probe = new SnapshotProbe(n => [(byte)n]);

        await using var saver = new AutoSaver(_path, _saveInterval, probe.Take);

        saver.Start();
        saver.MarkDirty();

        // Waiting for the first snapshot is what makes the assertion below mean anything:
        // without it a loop that never ran would pass too.
        await probe.WaitForCallAsync();

        await saver.DisposeAsync();
        await saver.DisposeAsync(); // idempotent

        var afterStop = probe.Count;
        await Task.Delay(_flushPeriodMs, TestContext.Current.CancellationToken);

        Assert.Equal(afterStop, probe.Count);
        Assert.Throws<ObjectDisposedException>(saver.MarkDirty);
    }

    [Fact(Timeout = _testTimeoutMs)]
    public async Task WriteFailure_DoesNotThrow_InvokesOnError_AndRetriesSucceedAfterFix()
    {
        var tmpPath = _path + ".tmp";
        var snapshot = new byte[] { 1, 2, 3, 4, 5 };
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        Directory.CreateDirectory(tmpPath);

        await using var saver = new AutoSaver(_path, _saveInterval, _ => snapshot)
        {
            OnError = ex => failed.TrySetResult(ex)
        };

        saver.Start();
        saver.MarkDirty();

        Assert.NotNull(await failed.Task.WaitAsync(TestContext.Current.CancellationToken));

        // Armed before the obstacle is cleared, so the retry that finally succeeds cannot slip by.
        using var changed = new FileChangeLatch(_path);

        Directory.Delete(tmpPath, recursive: true);
        saver.MarkDirty();

        Assert.Equal(snapshot, await changed.ReadAfterNextWriteAsync());
    }

    [Fact]
    public async Task MarkDirty_AfterDispose_ThrowsObjectDisposedException()
    {
        var saver = new AutoSaver(_path, _saveInterval, _ => [1]);
        await saver.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(saver.MarkDirty);
    }

    /// <summary>
    /// Stands in for the snapshot delegate and signals every time the loop asks for one.
    /// The call happens before the file is written, so it proves the loop ran.
    /// </summary>
    private sealed class SnapshotProbe(Func<int, byte[]> payload)
    {
        private readonly SemaphoreSlim _signal = new(0);
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public byte[] Take(CancellationToken _)
        {
            var bytes = payload(Interlocked.Increment(ref _count));
            _signal.Release();
            return bytes;
        }

        public Task WaitForCallAsync() => _signal.WaitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Waits for the next change to a single file and returns what it then holds.
    /// </summary>
    private sealed class FileChangeLatch : IDisposable
    {
        private readonly SemaphoreSlim _signal = new(0);
        private readonly FileSystemWatcher _watcher;
        private readonly string _path;

        public FileChangeLatch(string path)
        {
            _path = path;
            _watcher = new FileSystemWatcher(
                Path.GetDirectoryName(path)!,
                Path.GetFileName(path));

            _watcher.Created += Bump;
            _watcher.Changed += Bump;
            _watcher.Renamed += Bump;
            // Only what can mean new content: a deletion never does, an overflow may have hidden it.
            _watcher.Error += (_, _) => _signal.Release();
            _watcher.EnableRaisingEvents = true;
        }

        private void Bump(object sender, FileSystemEventArgs e) => _signal.Release();

        public async Task<byte[]> ReadAfterNextWriteAsync(int settleMs = 0)
        {
            await _signal.WaitAsync(TestContext.Current.CancellationToken);
            if (settleMs > 0)
            {
                await Task.Delay(settleMs, TestContext.Current.CancellationToken);
            }

            // Shared for write and delete so the read cannot block the writer's File.Move.
            await using var file = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, TestContext.Current.CancellationToken);
            return buffer.ToArray();
        }

        public void Dispose() => _watcher.Dispose();
    }
}

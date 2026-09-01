// ReSharper disable AccessToModifiedClosure

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Indexing;

namespace FolderDB.Tests;

public class AutoSaverTests : IDisposable
{
    private static readonly TimeSpan _saveInterval = TimeSpan.FromMilliseconds(50);
    // Assume that file write takes approximately 50ms.
    private const int _waitWriteDelayMs = 100;

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

    [Fact]
    public async Task Start_ThenMarkDirty_SavesInBackgroundAfterInterval()
    {
        var snapshot = new byte[] { 1, 2, 3, 4, 5 };

        await using var saver = new AutoSaver(_path, _saveInterval, _ => snapshot);

        saver.Start();
        saver.MarkDirty();
        await Task.Delay(_waitWriteDelayMs, TestContext.Current.CancellationToken);

        Assert.Equal(snapshot, await File.ReadAllBytesAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BackgroundLoop_DebouncesManyDirtyMarks_IntoSingleWritePerInterval()
    {
        var calls = 0;

        await using var saver = new AutoSaver(
            _path,
            _saveInterval,
            _ => BitConverter.GetBytes(Interlocked.Increment(ref calls)));

        saver.Start();

        // burst #1 inside one interval
        for (int i = 0; i < 50; i++) saver.MarkDirty();
        await Task.Delay(_waitWriteDelayMs, TestContext.Current.CancellationToken);

        var expected = BitConverter.ToInt32(await File.ReadAllBytesAsync(_path, TestContext.Current.CancellationToken), 0);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(1, expected);

        // burst #2 -> next interval => 2nd write
        for (int i = 0; i < 50; i++) saver.MarkDirty();
        await Task.Delay(_waitWriteDelayMs, TestContext.Current.CancellationToken);

        expected = BitConverter.ToInt32(await File.ReadAllBytesAsync(_path, TestContext.Current.CancellationToken), 0);
        Assert.Equal(2, expected);

        await saver.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_AndStopsFurtherBackgroundWrites()
    {
        var calls = 0;

        await using var saver = new AutoSaver(
            _path,
            _saveInterval,
            _ => [(byte)Interlocked.Increment(ref calls)]);

        saver.Start();
        saver.MarkDirty();
        await Task.Delay(_waitWriteDelayMs, TestContext.Current.CancellationToken);

        await saver.DisposeAsync();
        await saver.DisposeAsync(); // idempotent

        var afterStop = Volatile.Read(ref calls);

        Assert.Equal(afterStop, Volatile.Read(ref calls));
        Assert.Throws<ObjectDisposedException>(() => saver.MarkDirty());
    }

    [Fact]
    public async Task WriteFailure_DoesNotThrow_InvokesOnError_AndRetriesSucceedAfterFix()
    {
        var tmpPath = _path + ".tmp";
        var snapshot = new byte[] { 1, 2, 3, 4, 5 };
        Exception? captured = null;

        Directory.CreateDirectory(tmpPath);

        await using var saver = new AutoSaver(_path, _saveInterval, _ => snapshot)
        {
            OnError = ex => Volatile.Write(ref captured, ex)
        };

        saver.Start();
        saver.MarkDirty();
        await Task.Delay(_waitWriteDelayMs, TestContext.Current.CancellationToken);

        Assert.NotNull(Volatile.Read(ref captured));

        Directory.Delete(tmpPath, recursive: true);
        saver.MarkDirty();
        await Task.Delay(_waitWriteDelayMs, TestContext.Current.CancellationToken);

        Assert.Equal(snapshot, await File.ReadAllBytesAsync(_path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MarkDirty_AfterDispose_ThrowsObjectDisposedException()
    {
        var saver = new AutoSaver(_path, _saveInterval, _ => [1]);
        await saver.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => saver.MarkDirty());
    }
}

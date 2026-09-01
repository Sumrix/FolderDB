using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.FileStorage;

namespace FolderDB.Tests;

public class FileStoreTests
{
    [Fact]
    public async Task ReadAsync_WhenReadActionThrows_ReturnsAccessSuccessWithOperationFailure()
    {
        var rootPath = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(rootPath, "record.json");
            await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken);
            var store = new FileStore();

            var result = await store.ReadAsync<string>(
                path,
                _ => throw new InvalidOperationException("boom"),
                CancellationToken.None);

            Assert.True(result.IsFileAccessSuccessful);
            Assert.False(result.IsSuccess);
            Assert.True(result.Fingerprint.Exists);
            var exception = Assert.IsType<InvalidOperationException>(result.Error?.Exception);
            Assert.Equal("boom", exception.Message);
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task WriteAsync_WhenWriteActionThrows_ReturnsAccessSuccessWithOperationFailureAndDeletesTemporaryFile()
    {
        var rootPath = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(rootPath, "record.json");
            var store = new FileStore();

            var result = await store.WriteAsync(
                path,
                async stream =>
                {
                    await stream.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
                    throw new InvalidOperationException("boom");
                },
                CancellationToken.None);

            Assert.True(result.IsFileAccessSuccessful);
            Assert.Equal(FileErrorReason.Invalid, result.Error?.Reason);
            Assert.Equal(FileErrorPersistence.Persistent, result.Error?.Persistence);
            Assert.False(result.IsSuccess);
            Assert.Null(result.Fingerprint);
            var exception = Assert.IsType<InvalidOperationException>(result.Error?.Exception);
            Assert.Equal("boom", exception.Message);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}

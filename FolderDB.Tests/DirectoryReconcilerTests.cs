using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FolderDB.Tests.TestSupport;

namespace FolderDB.Tests;

public class DirectoryReconcilerTests
{
    [Fact]
    public async Task RequestDirectoryReconcile_WhenTableDirectoryMissing_ClearsIndex()
    {
        await using var harness = await ReconcilerHarness.CreateAsync();
        var filePath = await harness.WriteRecordAsync("alpha.json", new("id-1", 1, "value"));
        harness.RequestFileReconcile(filePath);
        await harness.RetryScheduler.RunAllAsync(TestContext.Current.CancellationToken);
        AssertNoRetry(harness);

        Directory.Delete(harness.TablePath, recursive: true);

        harness.RequestDirectoryReconcile();
        await harness.RetryScheduler.RunAllAsync(TestContext.Current.CancellationToken);
        AssertNoRetry(harness);

        using var scope = await harness.Index.EnterSharedScopeAsync(TestContext.Current.CancellationToken);
        Assert.Empty(scope.Records);
        Assert.Empty(scope.Files);
    }

    [Fact]
    public async Task RequestDirectoryReconcile_WhenIndexedFileDeleted_RemovesDeletedFileFromIndex()
    {
        await using var harness = await ReconcilerHarness.CreateAsync();
        await harness.WriteRecordAsync("a.json", new("id-a", 1, "one"));
        var bPath = await harness.WriteRecordAsync("b.json", new("id-b", 1, "two"));

        harness.RequestDirectoryReconcile();
        await harness.RetryScheduler.RunAllAsync(TestContext.Current.CancellationToken);
        AssertNoRetry(harness);

        File.Delete(bPath);

        harness.RequestDirectoryReconcile();
        await harness.RetryScheduler.RunAllAsync(TestContext.Current.CancellationToken);
        AssertNoRetry(harness);

        using var scope = await harness.Index.EnterSharedScopeAsync(TestContext.Current.CancellationToken);
        Assert.Single(scope.Records);
        Assert.Single(scope.Files);
        Assert.True(scope.Records.ContainsKey("id-a"));
        Assert.True(scope.Files.ContainsKey("a.json"));
        Assert.False(scope.Files.ContainsKey("b.json"));
    }

    [Fact]
    public async Task RequestDirectoryReconcile_WhenDirectoryHasMixedFiles_BuildsIndexAndMigratesLegacy()
    {
        await using var harness = await ReconcilerHarness.CreateAsync();
        await harness.WriteRecordAsync("a.json", new("id-a", 1, "one"));
        var legacyPath = await harness.WriteLegacyRecordAsync("legacy.json", new("id-legacy", 0, "old"));
        await File.WriteAllTextAsync(Path.Combine(harness.TablePath, "broken.json"), "{ not json", TestContext.Current.CancellationToken);

        harness.RequestDirectoryReconcile();
        await harness.RetryScheduler.RunAllAsync(TestContext.Current.CancellationToken);
        AssertNoRetry(harness);

        using (var scope = await harness.Index.EnterSharedScopeAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(2, scope.Records.Count);
            Assert.Equal(2, scope.Files.Count);
            Assert.True(scope.Records.ContainsKey("id-a"));
            Assert.True(scope.Records.ContainsKey("id-legacy"));
            Assert.True(scope.Files.ContainsKey("a.json"));
            Assert.True(scope.Files.ContainsKey("legacy.json"));
            Assert.False(scope.Files.ContainsKey("broken.json"));
            Assert.Equal("migrated-old", scope.Files["legacy.json"].Projection);
        }

        var persistedLegacy = await File.ReadAllTextAsync(legacyPath, TestContext.Current.CancellationToken);
        var persistedLegacyRecord = JsonSerializer.Deserialize(persistedLegacy, TestsJsonContext.Default.TestRecord);
        Assert.Equal(new TestRecord("id-legacy", 1, "migrated-old"), persistedLegacyRecord);
        Assert.DoesNotContain("LegacyValue", persistedLegacy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestDirectoryReconcile_WhenTwoFilesShareSameId_KeepsBothFilesAndSelectsLatestAsCurrent()
    {
        await using var harness = await ReconcilerHarness.CreateAsync();
        var aPath = await harness.WriteRecordAsync("a.json", new("shared-id", 1, "one"));
        var bPath = await harness.WriteRecordAsync("b.json", new("shared-id", 1, "two"));
        File.SetLastWriteTimeUtc(aPath, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(bPath, new DateTime(2024, 1, 1, 0, 0, 1, DateTimeKind.Utc));

        harness.RequestDirectoryReconcile();
        await harness.RetryScheduler.RunAllAsync(TestContext.Current.CancellationToken);
        AssertNoRetry(harness);

        using var scope = await harness.Index.EnterSharedScopeAsync(TestContext.Current.CancellationToken);
        var record = Assert.Single(scope.Records).Value;
        Assert.Equal("shared-id", record.Id);
        Assert.Equal(2, record.Files.Count);
        Assert.Equal("b.json", record.CurrentFileName);
        Assert.True(scope.Files.ContainsKey("a.json"));
        Assert.True(scope.Files.ContainsKey("b.json"));
        Assert.Equal("two", record.GetCurrentFileState().Projection);
    }

    private static void AssertNoRetry(ReconcilerHarness harness) => Assert.Equal(0, harness.RetryScheduler.PendingCount);
}

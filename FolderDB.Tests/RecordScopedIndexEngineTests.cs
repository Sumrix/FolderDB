using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FolderDB.Encoding;
using FolderDB.FileStorage;
using FolderDB.Indexing;
using FolderDB.Indexing.Persistence;
using FolderDB.Indexing.State;
using FolderDB.Runtime;
using FolderDB.Tests.TestSupport;

namespace FolderDB.Tests;

public class RecordScopedIndexEngineTests : IDisposable
{
    private static readonly CollectionContext<string, TestRecord, string> _stringCollection =
        TestCollectionContext.Create<string, TestRecord, string>(
            static value => value.Value,
            static record => [record.Id],
            StringComparer.Ordinal,
            StringComparer.Ordinal,
            new RecordCodec<string, TestRecord>(
                new RecordSchemaBuilder().StartWith(1, TestsJsonContext.Default.TestRecord).Build()),
            TestsJsonContext.Default.String,
            TestsJsonContext.Default.String);
    private static readonly ICollectionIndexPersistence<string, string> _stringPersistence =
        TestCollectionContext.CreateJsonIndexPersistence(
            TestsJsonContext.Default.String,
            TestsJsonContext.Default.String,
            StringComparer.Ordinal);

    private static readonly CollectionContext<string, TestRecord, NoProjection> _noProjectionCollection =
        TestCollectionContext.Create<string, TestRecord, NoProjection>(
            static _ => default,
            static record => [record.Id],
            StringComparer.Ordinal,
            StringComparer.Ordinal,
            new RecordCodec<string, TestRecord>(
                new RecordSchemaBuilder().StartWith(1, TestsJsonContext.Default.TestRecord).Build()),
            TestsJsonContext.Default.String,
            TestsJsonContext.Default.NoProjection);
    private static readonly ICollectionIndexPersistence<string, NoProjection> _noProjectionPersistence =
        TestCollectionContext.CreateJsonIndexPersistence(
            TestsJsonContext.Default.String,
            TestsJsonContext.Default.NoProjection,
            StringComparer.Ordinal);

    private readonly string _dir;
    private readonly string _path;

    public RecordScopedIndexEngineTests()
    {
        _dir = Directory.CreateTempSubdirectory().FullName;
        _path = Path.Combine(_dir, "index.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task LoadFileAsync_WhenFileMissing_StartsWithEmptyState()
    {
        await using var engine = await CreateNoProjectionEngineAsync();
        ShouldBeEmpty(engine);
    }

    [Fact]
    public async Task LoadFileAsync_WhenJsonInvalid_StartsWithEmptyState()
    {
        await File.WriteAllTextAsync(_path, "not a json", TestContext.Current.CancellationToken);
        await using var engine = await CreateNoProjectionEngineAsync();
        ShouldBeEmpty(engine);
    }

    [Fact]
    public async Task Upsert_AddsRecordAndFileMapping()
    {
        await using var engine = await CreateNoProjectionEngineAsync();
        var fingerprint = new FileFingerprint(DateTime.UnixEpoch, 10, true);

        engine.Upsert("id-1", "file-a.json", fingerprint, 1, new TestRecord("id-1", 1, "value")).Should().Be(IndexOperationResult.Applied);

        var expected = new IndexSnapshot(
            [new("id-1", "id-1", "file-a.json", [new("file-a.json", "id-1", FileIndexStatus.Committed, fingerprint)])],
            [new("file-a.json", "id-1", FileIndexStatus.Committed, fingerprint)]);

        ShouldMatchSnapshot(engine, expected);
    }

    [Fact]
    public async Task Upsert_MultipleFilesForRecord_SelectsCurrentByLastWriteThenFirstFileName()
    {
        await using var engine = await CreateNoProjectionEngineAsync();

        var t1 = new DateTime(2025, 01, 01, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2025, 01, 01, 0, 0, 1, DateTimeKind.Utc);

        engine.Upsert("id-1", "b.json", new(t1, 1, true), 1, new TestRecord("id-1", 1, "value")).Should().Be(IndexOperationResult.Applied);
        engine.Upsert("id-1", "c.json", new(t2, 1, true), 1, new TestRecord("id-1", 1, "value")).Should().Be(IndexOperationResult.Applied);
        engine.Records["id-1"].CurrentFileName.Should().Be("c.json");

        engine.Upsert("id-1", "a.json", new(t2, 1, true), 1, new TestRecord("id-1", 1, "value")).Should().Be(IndexOperationResult.Applied);
        engine.Records["id-1"].CurrentFileName.Should().Be("a.json");
    }

    [Fact]
    public async Task RecalculateCurrent_WhenCommittedAndErrorInfoFilesExist_PrefersCommittedStatus()
    {
        await using var engine = await CreateNoProjectionEngineAsync();

        var older = new FileFingerprint(new DateTime(2025, 01, 01, 0, 0, 0, DateTimeKind.Utc), 1, true);
        var newer = new FileFingerprint(new DateTime(2025, 01, 01, 0, 0, 1, DateTimeKind.Utc), 1, true);
        var errorInfo = FileErrorInfo.Create(
            FileErrorReason.Invalid,
            FileErrorPersistence.Persistent,
            new InvalidDataException("broken"));

        engine.Upsert("id-1", "committed.json", older, 1, new TestRecord("id-1", 1, "value")).Should().Be(IndexOperationResult.Applied);
        engine.Upsert("id-1", "invalid.json", newer, errorInfo).Should().Be(IndexOperationResult.Applied);

        engine.Records["id-1"].CurrentFileName.Should().Be("committed.json");

        engine.DeleteFile("id-1", "committed.json").Should().Be(IndexOperationResult.Applied);
        engine.Records["id-1"].CurrentFileName.Should().Be("invalid.json");
    }

    [Fact]
    public async Task Upsert_WhenFileBelongsToAnotherId_ReturnsBlockedAndKeepsOriginalOwner()
    {
        await using var engine = await CreateNoProjectionEngineAsync();
        var fp = new FileFingerprint(DateTime.UnixEpoch, 1, true);

        engine.Upsert("id-1", "shared.json", fp, 1, new TestRecord("id-1", 1, "value")).Should().Be(IndexOperationResult.Applied);
        var result = engine.Upsert("id-2", "shared.json", fp, 1, new TestRecord("id-2", 1, "value"));
        result.Should().Be(IndexOperationResult.BlockedByAnotherId);
        engine.Records.ContainsKey("id-2").Should().BeFalse();
    }

    [Fact]
    public async Task DeleteFile_WhenFileBelongsToAnotherId_ReturnsBlockedAndKeepsOriginalOwner()
    {
        await using var engine = await CreateNoProjectionEngineAsync();
        var fp = new FileFingerprint(DateTime.UnixEpoch, 1, true);

        engine.Upsert("id-1", "shared.json", fp, 1, new TestRecord("id-1", 1, "value")).Should().Be(IndexOperationResult.Applied);

        engine.DeleteFile("id-2", "shared.json").Should().Be(IndexOperationResult.BlockedByAnotherId);
        engine.Files["shared.json"].Record.Id.Should().Be("id-1");
        engine.Records.ContainsKey("id-2").Should().BeFalse();
    }

    [Fact]
    public async Task Delete_RemovesRecordAndAllItsFiles()
    {
        await using var engine = await CreateNoProjectionEngineAsync();
        engine.Upsert("id-1", "a.json", new(DateTime.UnixEpoch, 1, true), 1, new TestRecord("id-1", 1, "value"));
        engine.Upsert("id-1", "b.json", new(DateTime.UnixEpoch, 2, true), 1, new TestRecord("id-1", 1, "value"));
        engine.Delete("id-1");
        ShouldBeEmpty(engine);
    }

    [Fact]
    public async Task CommitReservedFileName_WhenReservedBySameId_CommitsAndUpdatesCurrent()
    {
        await using var engine = await CreateNoProjectionEngineAsync();
        const string fileName = "alpha.json";
        engine.TryReserveFileName("id-1", fileName).Should().BeTrue();
        var fp = new FileFingerprint(new DateTime(2025, 01, 01, 0, 0, 1, DateTimeKind.Utc), 123, true);
        engine.CommitReservedFileName("id-1", fileName, fp, new("id-1", 1, "value")).Should().BeTrue();
        engine.Records["id-1"].CurrentFileName.Should().Be(fileName);
        engine.Files[fileName].SchemaVersion.Should().Be(1);
    }

    [Fact]
    public async Task Upsert_WhenFingerprintUnchanged_DoesNotRebuildProjection()
    {
        await using var engine = await RecordScopedIndexEngine<string, TestRecord, string>.StartAsync(
            _path,
            _stringCollection,
            _stringPersistence,
            autoSaveEnabled: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var fingerprint = new FileFingerprint(DateTime.UnixEpoch, 10, true);
        engine.Upsert("id-1", "file-a.json", fingerprint, 1, new TestRecord("id-1", 1, "first")).Should().Be(IndexOperationResult.Applied);
        engine.Upsert("id-1", "file-a.json", fingerprint, 1, new TestRecord("id-1", 1, "second")).Should().Be(IndexOperationResult.NoChanges);

        engine.Files["file-a.json"].Projection.Should().Be("first");
    }

    [Fact]
    public async Task Upsert_SuccessfulUnversionedRead_ClearsIndexedSchemaVersion()
    {
        await using var engine = await CreateNoProjectionEngineAsync();
        var fingerprint = new FileFingerprint(DateTime.UnixEpoch, 10, true);
        var record = new TestRecord("id-1", 1, "value");
        engine.Upsert("id-1", "file-a.json", fingerprint, 1, record)
            .Should().Be(IndexOperationResult.Applied);

        engine.Upsert("id-1", "file-a.json", fingerprint, null, record)
            .Should().Be(IndexOperationResult.Applied);

        engine.Files["file-a.json"].SchemaVersion.Should().BeNull();
    }

    [Fact]
    public async Task UpsertError_WhenCommittedFileBecomesErrorInfo_KeepsLastProjection()
    {
        await using var engine = await RecordScopedIndexEngine<string, TestRecord, string>.StartAsync(
            _path,
            _stringCollection,
            _stringPersistence,
            autoSaveEnabled: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var fingerprint = new FileFingerprint(DateTime.UnixEpoch, 10, true);
        var errorInfo = FileErrorInfo.Create(
            FileErrorReason.Unavailable,
            FileErrorPersistence.Persistent,
            new IOException("locked"));

        engine.Upsert("id-1", "file-a.json", fingerprint, 1, new TestRecord("id-1", 1, "first")).Should().Be(IndexOperationResult.Applied);
        engine.Upsert("id-1", "file-a.json", fingerprint, errorInfo).Should().Be(IndexOperationResult.Applied);

        engine.Files["file-a.json"].Projection.Should().Be("first"); 
        
        var currentFile = engine.Records["id-1"].GetCurrentFileState();

        currentFile.Projection.Should().Be("first");
        currentFile.ErrorInfo.Should().Be(errorInfo);
        currentFile.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public async Task LoadFileAsync_WhenProjectionExists_RehydratesProjectionAndCurrentIndex()
    {
        var dto = new IndexDto([
            new RecordDto(
                JsonSerializer.SerializeToUtf8Bytes("id-1", TestsJsonContext.Default.String),
                new Dictionary<string, FileDto>
                {
                    ["user.json"] = new(
                        JsonSerializer.SerializeToUtf8Bytes("Harry Potter", TestsJsonContext.Default.String),
                        new FileFingerprint(DateTime.UnixEpoch, 1, true),
                        SchemaVersion: 1)
                })
        ]);

        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(dto), TestContext.Current.CancellationToken);
        await using var engine = await RecordScopedIndexEngine<string, TestRecord, string>.StartAsync(
            _path,
            _stringCollection,
            _stringPersistence,
            autoSaveEnabled: false,
            cancellationToken: TestContext.Current.CancellationToken);

        engine.Records["id-1"].GetCurrentFileState().Projection.Should().Be("Harry Potter");
        engine.Files["user.json"].Projection.Should().Be("Harry Potter");
        engine.Files["user.json"].Status.Should().Be(FileIndexStatus.Committed);
        engine.Files["user.json"].ErrorInfo.Should().BeNull();
        engine.Files["user.json"].SchemaVersion.Should().Be(1);
    }

    [Fact]
    public async Task LoadFileAsync_WhenFileErrorInfoExists_RehydratesStatusAndErrorInfo()
    {
        var errorInfo = new FileErrorInfo(
            FileErrorReason.Unavailable,
            FileErrorPersistence.Persistent,
            typeof(UnauthorizedAccessException).FullName!,
            "access denied",
            unchecked((int)0x80070005));
        var fingerprint = new FileFingerprint(DateTime.UnixEpoch, 1, true);
        var state = new CollectionIndexState<string, string>(StringComparer.Ordinal);
        var record = new RecordIndexState<string, string> { Id = "id-1" };
        var file = new FileIndexState<string, string>
        {
            Record = record,
            Status = FileIndexStatus.Committed,
            ErrorInfo = errorInfo,
            Projection = default!,
            Fingerprint = fingerprint,
            SchemaVersion = 1
        };
        state.Records["id-1"] = record;
        state.Files["user.json"] = file;
        record.Files["user.json"] = file;

        await File.WriteAllBytesAsync(_path, _stringPersistence.SerializeToBytes(state), TestContext.Current.CancellationToken);
        await using var engine = await RecordScopedIndexEngine<string, TestRecord, string>.StartAsync(
            _path,
            _stringCollection,
            _stringPersistence,
            autoSaveEnabled: false,
            cancellationToken: TestContext.Current.CancellationToken);

        engine.Files["user.json"].Status.Should().Be(FileIndexStatus.Committed);
        engine.Files["user.json"].ErrorInfo.Should().Be(errorInfo);
        engine.Files["user.json"].SchemaVersion.Should().Be(1);
        engine.Records["id-1"].CurrentFileName.Should().Be("user.json");
        engine.Records["id-1"].GetCurrentFileState().ErrorInfo.Should().Be(errorInfo);
    }

    private Task<RecordScopedIndexEngine<string, TestRecord, NoProjection>> CreateNoProjectionEngineAsync() =>
        RecordScopedIndexEngine<string, TestRecord, NoProjection>.StartAsync(
            _path,
            _noProjectionCollection,
            _noProjectionPersistence,
            autoSaveEnabled: false);

    private static void ShouldMatchSnapshot(RecordScopedIndexEngine<string, TestRecord, NoProjection> engine, IndexSnapshot expected) =>
        Snapshot(engine).Should().BeEquivalentTo(expected, o => o.WithStrictOrdering());

    private static void ShouldBeEmpty(RecordScopedIndexEngine<string, TestRecord, NoProjection> engine) =>
        Snapshot(engine).Should().BeEquivalentTo(new IndexSnapshot([], []), o => o.WithStrictOrdering());

    private static IndexSnapshot Snapshot(RecordScopedIndexEngine<string, TestRecord, NoProjection> engine)
    {
        var records = engine.Records
            .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
            .Select(static kv => new RecordSnapshot(
                kv.Key,
                kv.Value.Id,
                kv.Value.CurrentFileName,
                kv.Value.Files.OrderBy(static f => f.Key, StringComparer.Ordinal)
                    .Select(static f => new FileSnapshot(f.Key, GetOwnerId(f.Value), f.Value.Status, f.Value.Fingerprint))
                    .ToArray()))
            .ToArray();

        var files = engine.Files
            .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
            .Select(static kv => new FileSnapshot(kv.Key, GetOwnerId(kv.Value), kv.Value.Status, kv.Value.Fingerprint))
            .ToArray();

        return new(records, files);
    }

    private static string? GetOwnerId(IReadOnlyFileIndexState<string, NoProjection> file) =>
        (file as FileIndexState<string, NoProjection>)?.Record?.Id;

    private sealed record IndexSnapshot(RecordSnapshot[] Records, FileSnapshot[] Files);
    private sealed record RecordSnapshot(string Key, string Id, string CurrentFileName, FileSnapshot[] Files);
    private sealed record FileSnapshot(string FileName, string? OwnerId, FileIndexStatus Status, FileFingerprint Fingerprint);
}

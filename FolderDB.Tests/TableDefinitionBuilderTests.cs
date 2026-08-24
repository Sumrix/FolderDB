using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Building;
using FolderDB.Encoding;
using FolderDB.FileStorage;
using FolderDB.Indexing;
using FolderDB.Indexing.Persistence;
using FolderDB.Indexing.State;
using FolderDB.Infrastructure.Concurrency;
using FolderDB.Runtime;
using FolderDB.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Nito.AsyncEx;

namespace FolderDB.Tests;

public class TableDefinitionBuilderTests
{
    [Fact]
    public void Build_WithoutProjection_UsesDefaultsAndConfiguredCodec()
    {
        var codec = new FakeRecordCodec();

        var blueprint = TableDefinitionBuilder.Create<string, TestRecord>()
            .UseRecordCodec(codec)
            .WithoutProjection()
            .UseIndexEngine(_ => new FakeIndexEngine<string, TestRecord, NoProjection>())
            .Build();

        Assert.Equal(nameof(TestRecord), blueprint.Name);
        Assert.Equal(new[] { "id-1" }, blueprint.FileNameGenerator(new TestRecord("id-1", 1, "value")));
        Assert.Same(codec, blueprint.RecordCodecFactory(new RecordCodecContext(NullLoggerFactory.Instance)));
        Assert.Equal(default(NoProjection), blueprint.CreateProjection(new TestRecord("id-1", 1, "value")));
    }

    [Fact]
    public void Build_WithOverrides_AppliesConfiguredOptions()
    {
        var keyComparer = Comparer<string>.Create((x, y) => string.CompareOrdinal(y, x));
        var keyEqualityComparer = StringComparer.OrdinalIgnoreCase;

        var blueprint = TableDefinitionBuilder.Create<string, TestRecord>()
            .WithName("Users")
            .WithKeyComparer(keyComparer)
            .WithKeyEqualityComparer(keyEqualityComparer)
            .WithFileNaming(record => $"{record.Id}-{record.Value}")
            .UseRecordCodec(new FakeRecordCodec())
            .WithoutProjection()
            .UseIndexEngine(_ => new FakeIndexEngine<string, TestRecord, NoProjection>())
            .Build();

        Assert.Equal("Users", blueprint.Name);
        Assert.Same(keyComparer, blueprint.KeyComparer);
        Assert.Same(keyEqualityComparer, blueprint.KeyEqualityComparer);
        Assert.Equal(new[] { "id-1-value" }, blueprint.FileNameGenerator(new TestRecord("id-1", 1, "value")));
    }

    [Fact]
    public async Task CreateDefault_WithoutProjection_UsesJsonDefaultsForNonVersionedRecord()
    {
        var definition = TableDefinitionBuilder.CreateDefault<string, PlainTestRecord>(
            jsonOptions: TestsJsonContext.Default.Options);
        var record = new PlainTestRecord("id-1", "value");
        var codec = definition.RecordCodecFactory(new RecordCodecContext(NullLoggerFactory.Instance));
        await using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(record, TestsJsonContext.Default.PlainTestRecord));
        var decoded = await codec.DecodeAsync(stream, CancellationToken.None);

        Assert.Equal(nameof(PlainTestRecord), definition.Name);
        Assert.Equal(new[] { "id-1" }, definition.FileNameGenerator(record));
        Assert.Equal(default(NoProjection), definition.CreateProjection(record));
        Assert.NotNull(definition.IndexPersistenceFactory);
        Assert.Null(definition.IndexEngineFactory);
        Assert.IsType<RecordCodec<string, PlainTestRecord>>(codec);
        Assert.False(decoded.Upgraded);
        Assert.Null(decoded.SourceSchemaVersion);
        Assert.Null(decoded.TargetSchemaVersion);
        Assert.Equal(record, decoded.Record);
    }

    [Fact]
    public void CreateDefault_WithProjectionAndOptions_AppliesConfiguredOptions()
    {
        var keyComparer = Comparer<string>.Create((x, y) => string.CompareOrdinal(y, x));
        var keyEqualityComparer = StringComparer.OrdinalIgnoreCase;
        var jsonOptions = new JsonSerializerOptions(TestsJsonContext.Default.Options);
        var options = new TableOptions<string, PlainTestRecord>
        {
            Name = "Users",
            KeyComparer = keyComparer,
            KeyEqualityComparer = keyEqualityComparer,
            FileNameGenerator = record => new[] { $"{record.Id}-{record.Value}" }
        };

        var definition = TableDefinitionBuilder.CreateDefault<string, PlainTestRecord, string>(
            createProjection: record => record.Value,
            jsonOptions: jsonOptions,
            tableOptions: options);

        Assert.Equal("Users", definition.Name);
        Assert.Same(keyComparer, definition.KeyComparer);
        Assert.Same(keyEqualityComparer, definition.KeyEqualityComparer);
        Assert.Equal(new[] { "id-1-value" }, definition.FileNameGenerator(new PlainTestRecord("id-1", "value")));
        Assert.Equal("value", definition.CreateProjection(new PlainTestRecord("id-1", "value")));
        Assert.NotNull(definition.IndexPersistenceFactory);
        Assert.Null(definition.IndexEngineFactory);
    }

    [Fact]
    public async Task Build_WithProjectionAndIndexPersistence_UsesFactoriesFromBuilderChain()
    {
        var expectedPersistence = new FakeIndexPersistence<string, int>();

        var blueprint = TableDefinitionBuilder.Create<string, TestRecord>()
            .UseRecordCodec(new FakeRecordCodec())
            .WithProjection(record => record.Value.Length)
            .UseIndexPersistence(_ => expectedPersistence)
            .Build();

        Assert.Equal(5, blueprint.CreateProjection(new TestRecord("id-1", 1, "value")));
        var table = CreateTableContext(blueprint);
        var persistenceContext = new TableIndexPersistenceContext<string, TestRecord, int>(table, NullLoggerFactory.Instance);

        Assert.Same(expectedPersistence, blueprint.IndexPersistenceFactory!(persistenceContext));
        Assert.Null(blueprint.IndexEngineFactory);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Build_WithIndexEngine_UsesFactoryFromBuilderChain()
    {
        var expectedEngine = new FakeIndexEngine<string, TestRecord, string>();

        var blueprint = TableDefinitionBuilder.Create<string, TestRecord>()
            .UseRecordCodec(new FakeRecordCodec())
            .WithProjection(record => record.Value)
            .UseIndexEngine(_ => expectedEngine)
            .Build();

        var table = CreateTableContext(blueprint);
        var indexContext = new RecordScopedIndexEngineContext<string, TestRecord, string>
        {
            Table = table,
            DatabaseOptions = CreateDatabaseOptions(),
            IndexFilePath = "index.bin",
            CancellationToken = CancellationToken.None,
        };

        Assert.Null(blueprint.IndexPersistenceFactory);
        Assert.Same(expectedEngine, await blueprint.IndexEngineFactory!(indexContext));
    }

    private static DatabaseOptions CreateDatabaseOptions()
    {
        return new DatabaseOptions
        {
            LoggerFactory = NullLoggerFactory.Instance
        };
    }

    private static TableContext<string, TestRecord, TProjection> CreateTableContext<TProjection>(
        TableDefinition<string, TestRecord, TProjection> definition)
    {
        return new TableContext<string, TestRecord, TProjection>
        {
            Name = definition.Name,
            KeyComparer = definition.KeyComparer,
            KeyEqualityComparer = definition.KeyEqualityComparer,
            FileNameGenerator = definition.FileNameGenerator,
            CreateProjection = definition.CreateProjection,
            RecordCodec = definition.RecordCodecFactory(new RecordCodecContext(NullLoggerFactory.Instance)),
        };
    }

    private sealed class FakeRecordCodec : IRecordCodec<string, TestRecord>
    {
        public int? CurrentSchemaVersion => null;

        public Task<RecordDecodeResult<TestRecord>> DecodeAsync(Stream stream, CancellationToken cancellationToken)
        {
            throw new System.NotSupportedException();
        }

        public Task EncodeAsync(Stream stream, TestRecord record, CancellationToken cancellationToken)
        {
            throw new System.NotSupportedException();
        }
    }

    private sealed class FakeIndexPersistence<TKey, TProjection> : ITableIndexPersistence<TKey, TProjection>
        where TKey : notnull
    {
        public Task<TableIndexState<TKey, TProjection>?> LoadIfExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            throw new System.NotSupportedException();
        }

        public byte[] SerializeToBytes(TableIndexState<TKey, TProjection> state)
        {
            throw new System.NotSupportedException();
        }
    }

    private sealed class FakeIndexEngine<TKey, TRecord, TProjection> : IRecordScopedIndexEngine<TKey, TRecord, TProjection>
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        public TableContext<TKey, TRecord, TProjection> Table { get; } = new()
        {
            Name = "fake",
            KeyComparer = Comparer<TKey>.Default,
            KeyEqualityComparer = EqualityComparer<TKey>.Default,
            FileNameGenerator = _ => throw new NotSupportedException(),
            CreateProjection = _ => throw new NotSupportedException(),
            RecordCodec = null!
        };

        public IReadOnlyDictionary<TKey, IReadOnlyRecordIndexState<TKey, TProjection>> Records { get; } =
            new Dictionary<TKey, IReadOnlyRecordIndexState<TKey, TProjection>>();
        public IReadOnlyDictionary<string, IReadOnlyFileIndexState<TKey, TProjection>> Files { get; } =
            new Dictionary<string, IReadOnlyFileIndexState<TKey, TProjection>>();
        public IReadOnlyDictionary<TKey, TProjection> CurrentProjections { get; } = new Dictionary<TKey, TProjection>();
        public IReadOnlyDictionary<TKey, IndexedFile<TProjection>> IndexedFiles { get; } =
            new Dictionary<TKey, IndexedFile<TProjection>>();
        public AsyncReaderWriterLock Barrier { get; } = new();
        public StripedAsyncLock<TKey> IdLocks { get; } = new(1, EqualityComparer<TKey>.Default);

        public IndexOperationResult Upsert(
            TKey id,
            string fileName,
            FileFingerprint fingerprint,
            int? schemaVersion,
            TRecord record)
        {
            throw new NotSupportedException();
        }

        public IndexOperationResult Upsert(
            TKey id,
            string fileName,
            FileFingerprint fingerprint,
            FileErrorInfo errorInfo)
        {
            throw new NotSupportedException();
        }

        public bool TryReserveFileName(TKey id, string fileName)
        {
            throw new NotSupportedException();
        }

        public bool CommitReservedFileName(TKey id, string fileName, FileFingerprint fingerprint, TRecord record)
        {
            throw new NotSupportedException();
        }

        public bool Delete(TKey id)
        {
            throw new NotSupportedException();
        }

        public IndexOperationResult DeleteFile(TKey id, string fileName)
        {
            throw new NotSupportedException();
        }

        public void Clear()
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

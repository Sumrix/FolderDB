using System.IO;
using System.Threading.Tasks;
using FolderDB.Encoding;
using FolderDB.Tests.TestSupport;

namespace FolderDB.Tests;

public sealed class IndexedCollectionTests
{
    [Fact]
    public async Task Files_WhenRecordWritten_ExposesDetailedIndexState()
    {
        var rootPath = Directory.CreateTempSubdirectory().FullName;

        try
        {
            await using var store = await FolderStore.OpenAsync(rootPath, [CreateCollectionDefinition()], cancellationToken: TestContext.Current.CancellationToken);
            var collection = store.IndexedCollection<string, PlainTestRecord, NoProjection>();

            await collection.TryUpsertAsync(new PlainTestRecord("id-1", "value-1"), TestContext.Current.CancellationToken);

            var file = Assert.Single(collection.Files).Value;
            Assert.Null(file.ErrorInfo);
            Assert.NotNull(file.FileName);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static CollectionDefinition<string, PlainTestRecord> CreateCollectionDefinition()
    {
        return new CollectionDefinition<string, PlainTestRecord>(
            new RecordCodec<string, PlainTestRecord>(
                new RecordSchemaBuilder().WithoutVersioning(TestsJsonContext.Default.PlainTestRecord)),
            TestsJsonContext.Default.String);
    }
}

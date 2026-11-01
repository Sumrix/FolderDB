using System.IO;
using System.Threading.Tasks;
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
            await using var db = await FolderDatabase.OpenAsync(rootPath, [CreateCollectionDefinition()], cancellationToken: TestContext.Current.CancellationToken);
            var collection = db.IndexedCollection<string, PlainTestRecord, NoProjection>();

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

    private static CollectionDefinition<string, PlainTestRecord, NoProjection> CreateCollectionDefinition()
    {
        return CollectionDefinitionBuilder.CreateDefault<string, PlainTestRecord>(
            jsonOptions: TestsJsonContext.Default.Options);
    }
}

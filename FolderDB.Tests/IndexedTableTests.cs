using System.IO;
using System.Threading.Tasks;
using FolderDB.Tests.TestSupport;

namespace FolderDB.Tests;

public sealed class IndexedTableTests
{
    [Fact]
    public async Task Files_WhenRecordWritten_ExposesDetailedIndexState()
    {
        var rootPath = Directory.CreateTempSubdirectory().FullName;

        try
        {
            await using var db = await FolderDatabase.OpenAsync(rootPath, [CreateTableDefinition()], cancellationToken: TestContext.Current.CancellationToken);
            var table = db.IndexedTable<string, PlainTestRecord, NoProjection>();

            await table.TryUpsertAsync(new PlainTestRecord("id-1", "value-1"), TestContext.Current.CancellationToken);

            var file = Assert.Single(table.Files).Value;
            Assert.Null(file.ErrorInfo);
            Assert.NotNull(file.FileName);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static TableDefinition<string, PlainTestRecord, NoProjection> CreateTableDefinition()
    {
        return TableDefinitionBuilder.CreateDefault<string, PlainTestRecord>(
            jsonOptions: TestsJsonContext.Default.Options);
    }
}

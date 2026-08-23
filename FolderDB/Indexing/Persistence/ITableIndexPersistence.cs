using System.Threading;
using System.Threading.Tasks;
using FolderDB.Indexing.State;

namespace FolderDB.Indexing.Persistence;

/// <summary>
/// Defines persistence for saving and loading table index state.
/// </summary>
public interface ITableIndexPersistence<TKey, TProjection>
    where TKey : notnull
{
    Task<TableIndexState<TKey, TProjection>?> LoadIfExistsAsync(
        string path,
        CancellationToken cancellationToken = default);

    byte[] SerializeToBytes(TableIndexState<TKey, TProjection> state);
}

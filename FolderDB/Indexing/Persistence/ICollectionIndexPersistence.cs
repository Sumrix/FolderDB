using System.Threading;
using System.Threading.Tasks;
using FolderDB.Indexing.State;

namespace FolderDB.Indexing.Persistence;

/// <summary>
/// Defines persistence for saving and loading collection index state.
/// </summary>
public interface ICollectionIndexPersistence<TKey, TProjection>
    where TKey : notnull
{
    Task<CollectionIndexState<TKey, TProjection>?> LoadIfExistsAsync(
        string path,
        CancellationToken cancellationToken = default);

    byte[] SerializeToBytes(CollectionIndexState<TKey, TProjection> state);
}

namespace FolderDB;

/// <summary>
/// Represents a running file-backed store as an indexed collection.
/// </summary>
/// <typeparam name="TKey">The type of the record id.</typeparam>
/// <typeparam name="TRecord">The record type stored in the collection.</typeparam>
/// <typeparam name="TProjection">The projection type the collection was defined with.</typeparam>
public interface IFolderIndexedRecordCollection<TKey, TRecord, TProjection> :
    IFolderRecordCollection<TKey, TRecord>,
    IIndexedRecordCollection<TKey, TRecord, TProjection>
    where TRecord : IRecord<TKey>
{
}

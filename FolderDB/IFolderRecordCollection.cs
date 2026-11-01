namespace FolderDB;

/// <summary>
/// Represents a running file-backed store as a collection.
/// </summary>
/// <typeparam name="TKey">The type of the record id.</typeparam>
/// <typeparam name="TRecord">The record type stored in the collection.</typeparam>
public interface IFolderRecordCollection<TKey, TRecord> : IRecordCollection<TKey, TRecord>, IFolderStore
    where TRecord : IRecord<TKey>
{
}

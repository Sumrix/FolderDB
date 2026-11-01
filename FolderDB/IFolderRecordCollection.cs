namespace FolderDB;

/// <summary>
/// Represents a running file-backed database as a collection.
/// </summary>
/// <typeparam name="TKey">The type of the record id.</typeparam>
/// <typeparam name="TRecord">The record type stored in the collection.</typeparam>
public interface IFolderRecordCollection<TKey, TRecord> : IRecordCollection<TKey, TRecord>, IDatabase
    where TRecord : IRecord<TKey>
{
}

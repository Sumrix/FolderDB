namespace FolderDB;

/// <summary>
/// Represents a running file-backed database as a table.
/// </summary>
/// <typeparam name="TKey">The type of the record id.</typeparam>
/// <typeparam name="TRecord">The record type stored in the table.</typeparam>
public interface IFolderTable<TKey, TRecord> : ITable<TKey, TRecord>, IDatabase
    where TRecord : IRecord<TKey>
{
}

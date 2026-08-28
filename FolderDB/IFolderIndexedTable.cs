namespace FolderDB;

/// <summary>
/// Represents a running file-backed database as an indexed table.
/// </summary>
/// <typeparam name="TKey">The type of the record id.</typeparam>
/// <typeparam name="TRecord">The record type stored in the table.</typeparam>
/// <typeparam name="TProjection">The projection type the table was defined with.</typeparam>
public interface IFolderIndexedTable<TKey, TRecord, TProjection> :
    IFolderTable<TKey, TRecord>,
    IIndexedTable<TKey, TRecord, TProjection>
    where TRecord : IRecord<TKey>
{
}

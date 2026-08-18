namespace FolderDB;

public interface IFolderIndexedTable<TKey, TRecord, TProjection> :
    IFolderTable<TKey, TRecord>,
    IIndexedTable<TKey, TRecord, TProjection>
{
}

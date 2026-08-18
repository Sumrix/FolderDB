namespace FolderDB;

public interface IFolderTable<TKey, TRecord> : ITable<TKey, TRecord>, IDatabase
{
}

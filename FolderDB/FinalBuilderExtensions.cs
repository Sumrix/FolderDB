using System;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Building;

namespace FolderDB;

public static class FinalBuilderExtensions
{
    public static async Task<IFolderTable<TKey, TRecord>> OpenAsync<TKey, TRecord>(
        this FinalBuilder<TKey, TRecord, NoProjection> builder,
        string path,
        DatabaseOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);

        return await FolderDatabase.OpenTableAsync(path, builder.Build(), options, cancellationToken);
    }

    public static async Task<IFolderIndexedTable<TKey, TRecord, TProjection>> OpenIndexedAsync<TKey, TRecord, TProjection>(
        this FinalBuilder<TKey, TRecord, TProjection> builder,
        string path,
        DatabaseOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);

        return await FolderDatabase.OpenIndexedTableAsync(path, builder.Build(), options, cancellationToken);
    }
}

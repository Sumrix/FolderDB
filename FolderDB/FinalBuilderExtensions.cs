using System;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Building;

namespace FolderDB;

public static class FinalBuilderExtensions
{
    public static async Task<IFolderTable<TKey, TRecord>> StartAsync<TKey, TRecord>(
        this FinalBuilder<TKey, TRecord, NoProjection> builder,
        string path,
        DatabaseOptions? options = null,
        CancellationToken ct = default)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);

        return await FolderDatabase.StartTableAsync(path, builder.Build(), options, ct);
    }

    public static async Task<IFolderIndexedTable<TKey, TRecord, TProjection>> StartIndexedAsync<TKey, TRecord, TProjection>(
        this FinalBuilder<TKey, TRecord, TProjection> builder,
        string path,
        DatabaseOptions? options = null,
        CancellationToken ct = default)
        where TRecord : class, IRecord<TKey>
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);

        return await FolderDatabase.StartIndexedTableAsync(path, builder.Build(), options, ct);
    }
}

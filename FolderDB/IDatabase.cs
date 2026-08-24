using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderDB;

public interface IDatabase : IAsyncDisposable
{
    string RootPath { get; }

    Task FlushIndexAsync(CancellationToken cancellationToken = default);

    void RequestRescan();
}

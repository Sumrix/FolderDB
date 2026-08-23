using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderDB.Runtime;

public interface ITableEngine : IAsyncDisposable
{
    void RequestDirectoryReconcile();
    Task FlushAsync(CancellationToken cancellationToken = default);
}

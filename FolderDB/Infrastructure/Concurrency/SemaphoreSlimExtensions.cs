using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderDB.Infrastructure.Concurrency;

public static class SemaphoreSlimExtensions
{
    public static async Task<IDisposable> EnterAsync(
        this SemaphoreSlim semaphore,
        CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        return new Scope(semaphore);
    }

    private readonly struct Scope(SemaphoreSlim semaphore) : IDisposable
    {
        private readonly SemaphoreSlim? _semaphore = semaphore;
        public void Dispose() => _semaphore?.Release();
    }
}
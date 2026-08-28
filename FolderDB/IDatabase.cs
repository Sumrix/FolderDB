using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderDB;

/// <summary>
/// Represents a running file-backed database that maintains an index of the files it owns.
/// </summary>
/// <remarks>
/// Disposing does not flush the index; call <see cref="FlushIndexAsync"/> beforehand.
/// </remarks>
public interface IDatabase : IAsyncDisposable
{
    /// <summary>
    /// Gets the absolute path of the directory in which the database was opened.
    /// </summary>
    /// <returns>The absolute path of the database directory.</returns>
    string RootPath { get; }

    /// <summary>
    /// Writes every pending index change to its cache file, without waiting for
    /// <see cref="DatabaseOptions.IndexAutoSaveInterval"/>.
    /// </summary>
    /// <remarks>
    /// Record writes are already durable when their operation completes; flushing only saves work at
    /// the next open, where records missing from the index cache are read from disk again.
    /// </remarks>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that returns the asynchronous flush operation.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    Task FlushIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the database to re-read its directories and bring the index back in line with the files
    /// on disk.
    /// </summary>
    /// <remarks>
    /// The rescan is queued and runs in the background, so the call returns before it completes.
    /// It is normally unnecessary, because changes made outside the API are picked up by the
    /// directory watcher. But a watcher can stop delivering events, for example after its directory
    /// was deleted and recreated, and an application that knows files changed behind its back can
    /// ask for the rescan itself.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    void RequestRescan();
}

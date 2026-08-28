using System;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.FileStorage;
using FolderDB.Retry;
using FolderDB.Runtime;
using Microsoft.Extensions.Logging;

namespace FolderDB;

/// <summary>
/// Defines a mechanism for starting a table in a database.
/// </summary>
public interface ITableDefinition
{
    /// <summary>
    /// Gets the name of the table, which is also the name of the directory that holds it.
    /// </summary>
    /// <returns>The name of the table.</returns>
    /// <remarks>
    /// Characters that a file name cannot contain are replaced with an underscore, so two names
    /// that differ only in those characters end up colliding. The sanitized name must not equal
    /// <c>.indices</c>, which the database reserves for index files.
    /// </remarks>
    string Name { get; }

    /// <summary>
    /// Gets the type of the records the table stores.
    /// </summary>
    /// <returns>The record type.</returns>
    Type RecordType { get; }

    /// <summary>
    /// Asynchronously creates and starts the engine that runs the table.
    /// </summary>
    /// <param name="tablePath">The directory that holds the table.</param>
    /// <param name="indexFilePath">The file the table index is persisted to.</param>
    /// <param name="fileStore">The file store the table reads and writes records through.</param>
    /// <param name="retryScheduler">The scheduler that queues reconciliation retries.</param>
    /// <param name="options">Options to control the behavior of the database.</param>
    /// <param name="loggerFactory">The logger factory scoped to this table.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that returns the asynchronous start operation, which wraps the started engine.
    /// </returns>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    Task<ITableEngine> StartEngineAsync(
        string tablePath,
        string indexFilePath,
        IFileStore fileStore,
        IRetryScheduler<string> retryScheduler,
        DatabaseOptions options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default);
}

using System;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Runtime;

namespace FolderDB;

/// <summary>
/// Defines a mechanism for starting a collection in a store.
/// </summary>
public interface ICollectionDefinition
{
    /// <summary>
    /// Gets the name of the collection, which is also the name of the directory that holds it.
    /// </summary>
    /// <returns>The name of the collection.</returns>
    /// <remarks>
    /// Characters that a file name cannot contain are replaced with an underscore, so two names
    /// that differ only in those characters end up colliding. The sanitized name must not equal
    /// <c>.indices</c>, which the store reserves for index files.
    /// </remarks>
    string Name { get; }

    /// <summary>
    /// Gets the type of the records the collection stores.
    /// </summary>
    /// <returns>The record type.</returns>
    Type RecordType { get; }

    /// <summary>
    /// Asynchronously creates and starts the engine that runs the collection.
    /// </summary>
    /// <param name="collectionPath">The directory that holds the collection.</param>
    /// <param name="indexFilePath">The file the collection index is persisted to.</param>
    /// <param name="storeContext">
    /// The values and services of the store that the collection is started in.
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that returns the asynchronous start operation, which wraps the started engine.
    /// </returns>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    Task<ICollectionEngine> StartEngineAsync(
        string collectionPath,
        string indexFilePath,
        StoreContext storeContext,
        CancellationToken cancellationToken = default);
}

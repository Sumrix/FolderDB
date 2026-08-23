using System.Threading;
using System.Threading.Tasks;

namespace FolderDB;

/// <summary>
/// Represents a table-like API over file-backed records.
/// </summary>
/// <remarks>
/// Every operation comes in two forms. The plain form throws when it cannot complete the operation.
/// The <c>Try</c> form reports file errors as a result, but still throws on other kinds of error,
/// such as a failure to generate a file name.
/// </remarks>
public interface ITable<in TKey, TRecord>
{
    /// <summary>
    /// Gets a record by id, or returns null when no record is present.
    /// </summary>
    /// <exception cref="System.Exception">Throws when the record exists but cannot be read or decoded.</exception>
    Task<TRecord?> GetAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fully replaces the stored state of a record.
    /// </summary>
    /// <exception cref="System.Exception">Throws when the target file cannot be written.</exception>
    Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a record and all known files that belong to it.
    /// </summary>
    /// <exception cref="System.Exception">Throws when a known file cannot be deleted.</exception>
    Task DeleteAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a record by id and reports expected file access or decoding failures in the result.
    /// </summary>
    Task<ReadResult<TRecord>> TryGetAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fully replaces the stored state of a record and reports expected file access failures in the result.
    /// </summary>
    Task<OperationResult> TryUpsertAsync(TRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a record and all known files that belong to it, and reports expected file access failures in the result.
    /// </summary>
    Task<OperationResult> TryDeleteAsync(TKey id, CancellationToken cancellationToken = default);
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Infrastructure.Exceptions;

namespace FolderDB;

/// <summary>
/// Represents a collection-like API over file-backed records.
/// </summary>
/// <typeparam name="TKey">The type of the record id.</typeparam>
/// <typeparam name="TRecord">The record type stored in the collection.</typeparam>
public interface IRecordCollection<in TKey, TRecord>
    where TRecord : IRecord<TKey>
{
    /// <summary>
    /// Asynchronously gets the record with the specified id.
    /// </summary>
    /// <param name="id">The id of the record to get.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that returns the asynchronous get operation, which wraps the record, or
    /// <see langword="null"/> if no record is present.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileOperationException">
    /// The record file could not be read.
    ///
    /// -or-
    ///
    /// Its content could not be decoded.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    Task<TRecord?> GetAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously stores a record, fully replacing the stored state of an existing record with
    /// the same id.
    /// </summary>
    /// <param name="record">The record to store.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that returns the asynchronous upsert operation.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="record"/> is <see langword="null"/>.
    ///
    /// -or-
    ///
    /// The <see cref="IRecord{TKey}.Id"/> of the provided <paramref name="record"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="FileNameGenerationException">
    /// The configured <see cref="CollectionOptions{TKey, TRecord}.FileNameGenerator"/> threw.
    ///
    /// -or-
    ///
    /// The generator produced a candidate that is <see langword="null"/>, empty, or consists only of white-space characters,
    /// too long, or contains one or more of the invalid characters defined in <see cref="Path.GetInvalidFileNameChars"/>.
    ///
    /// -or-
    ///
    /// The generator ran out of candidates.
    ///
    /// -or-
    ///
    /// No free file name was found within
    /// <see cref="StoreOptions.MaxFileNameReserveAttempts"/> attempts.
    /// </exception>
    /// <exception cref="FileOperationException">
    /// The record could not be encoded.
    ///
    /// -or-
    ///
    /// The record file could not be written.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously deletes the record with the specified id and every known file that belongs to it.
    /// </summary>
    /// <param name="id">The id of the record to delete.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>A task that returns the asynchronous delete operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileOperationException">The record file could not be deleted.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    Task DeleteAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously gets the record with the specified id and reports expected file access and
    /// decoding failures in the result.
    /// </summary>
    /// <remarks>
    /// The <c>Try</c> prefix does not make this method exception-free: failures of other kinds are
    /// still thrown.
    /// </remarks>
    /// <param name="id">The id of the record to get.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that returns the asynchronous get operation, which wraps the record and the file
    /// error that prevented reading it, when there was one.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    Task<ReadResult<TRecord>> TryGetAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously stores a record, fully replacing the stored state of an existing record with
    /// the same id, and reports expected file access failures in the result.
    /// </summary>
    /// <remarks>
    /// The <c>Try</c> prefix does not make this method exception-free: failures of other kinds are
    /// still thrown.
    /// </remarks>
    /// <param name="record">The record to store.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that returns the asynchronous upsert operation, which wraps the file error that
    /// prevented the write, when there was one.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="record"/> is <see langword="null"/>.
    ///
    /// -or-
    ///
    /// The <see cref="IRecord{TKey}.Id"/> of the provided <paramref name="record"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="FileNameGenerationException">
    /// The configured <see cref="CollectionOptions{TKey, TRecord}.FileNameGenerator"/> threw.
    ///
    /// -or-
    ///
    /// The generator produced a candidate that is <see langword="null"/>, empty, or consists only of white-space characters,
    /// too long, or contains one or more of the invalid characters defined in <see cref="Path.GetInvalidFileNameChars"/>.
    ///
    /// -or-
    ///
    /// The generator ran out of candidates.
    ///
    /// -or-
    ///
    /// No free file name was found within
    /// <see cref="StoreOptions.MaxFileNameReserveAttempts"/> attempts.
    /// </exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    Task<OperationResult> TryUpsertAsync(TRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously deletes the record with the specified id and every known file that belongs to
    /// it, and reports expected file access failures in the result.
    /// </summary>
    /// <remarks>
    /// The <c>Try</c> prefix does not make this method exception-free: failures of other kinds are
    /// still thrown.
    /// </remarks>
    /// <param name="id">The id of the record to delete.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that returns the asynchronous delete operation, which wraps the file error that
    /// prevented a deletion, when there was one.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    Task<OperationResult> TryDeleteAsync(TKey id, CancellationToken cancellationToken = default);
}

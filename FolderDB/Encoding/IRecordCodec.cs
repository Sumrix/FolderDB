using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Infrastructure.Exceptions;

namespace FolderDB.Encoding;

/// <summary>
/// Defines a mechanism for converting between records and their stored representation.
/// </summary>
/// <typeparam name="TKey">The type of the record id.</typeparam>
/// <typeparam name="TRecord">The record type stored in the table.</typeparam>
public interface IRecordCodec<TKey, TRecord>
{
    int? CurrentSchemaVersion { get; }

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <exception cref="RecordConversionException">
    /// <paramref name="stream"/> cannot be converted to <typeparamref name="TRecord"/>.
    /// </exception>
    Task<RecordDecodeResult<TRecord>> DecodeAsync(Stream stream, CancellationToken cancellationToken);

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <exception cref="RecordConversionException">
    /// <paramref name="record"/> cannot be converted to its stored representation.
    /// </exception>
    Task EncodeAsync(Stream stream, TRecord record, CancellationToken cancellationToken);
}

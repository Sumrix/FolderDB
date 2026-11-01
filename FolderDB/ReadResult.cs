using FolderDB.FileStorage;

namespace FolderDB;

/// <summary>
/// Represents the outcome of a file read operation.
/// </summary>
/// <typeparam name="TRecord">The record type stored in the collection.</typeparam>
/// <param name="Record">
/// The record read from the file, or <see langword="null"/> if the collection holds no record for the
/// requested id, or if the operation failed.
/// </param>
/// <param name="Error">
/// The file error observed by the operation, or <see langword="null"/> if the operation succeeded.
/// </param>
/// <param name="FileName">
/// The name of the file the record was read from, in the collection directory, or
/// <see langword="null"/> if the index holds no file for the requested id.
/// </param>
/// <remarks>
/// A successful result does not imply a record: <see cref="OperationResult.IsSuccess"/> is
/// <see langword="true"/> and <see cref="Record"/> is <see langword="null"/> when the collection holds no
/// record for the requested id.
/// </remarks>
public sealed record ReadResult<TRecord>(
    TRecord? Record,
    FileError? Error = null,
    string? FileName = null)
    : OperationResult(Error, FileName);

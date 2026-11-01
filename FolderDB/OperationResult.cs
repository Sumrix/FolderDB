using System.Diagnostics.CodeAnalysis;
using FolderDB.FileStorage;

namespace FolderDB;

/// <summary>
/// Represents the outcome of a collection operation.
/// </summary>
/// <param name="Error">
/// The file error observed by the operation, or <see langword="null"/> if the operation succeeded.
/// </param>
/// <param name="FileName">
/// The name of the file in the collection directory, or <see langword="null"/> if no file is
/// associated with the result.
/// </param>
public record OperationResult(
    FileError? Error = null,
    string? FileName = null)
{
    /// <summary>
    /// Gets a value indicating whether the operation completed without a file error.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the operation completed without a file error; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    /// <summary>
    /// Gets the reason FolderDB classified the file error.
    /// </summary>
    /// <returns>
    /// The reason of <see cref="Error"/>, or <see langword="null"/> if the operation succeeded.
    /// </returns>
    public FileErrorReason? ErrorReason => Error?.Reason;

    /// <summary>
    /// Gets a successful operation result.
    /// </summary>
    /// <returns>The successful operation result.</returns>
    public static OperationResult Success { get; } = new();
}

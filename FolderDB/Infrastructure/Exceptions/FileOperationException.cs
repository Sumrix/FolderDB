using System;
using FolderDB.FileStorage;

namespace FolderDB.Infrastructure.Exceptions;

/// <summary>
/// The exception that is thrown when a file operation fails.
/// </summary>
/// <param name="message">The error message that explains the reason for the exception.</param>
/// <param name="reason">The reason FolderDB classified the file error.</param>
/// <param name="persistence">
/// A value that indicates whether FolderDB classified the file error as temporary or persistent.
/// </param>
/// <param name="inner">
/// The exception that is the cause of the current exception, or <see langword="null"/> if no inner
/// exception is specified.
/// </param>
public class FileOperationException(
    string message,
    FileErrorReason reason,
    FileErrorPersistence persistence,
    Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    /// Gets the reason FolderDB classified the file error.
    /// </summary>
    /// <returns>The reason FolderDB classified the file error.</returns>
    public FileErrorReason Reason { get; } = reason;

    /// <summary>
    /// Gets a value that indicates whether FolderDB classified the file error as temporary or
    /// persistent.
    /// </summary>
    /// <returns>
    /// A value that indicates whether FolderDB classified the file error as temporary or persistent.
    /// </returns>
    public FileErrorPersistence Persistence { get; } = persistence;
}

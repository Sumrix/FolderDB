using System;
using FolderDB.FileStorage;

namespace FolderDB.Infrastructure.Exceptions;

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
    /// Gets whether FolderDB classified the file error as temporary or persistent.
    /// </summary>
    /// <returns>Whether FolderDB classified the file error as temporary or persistent.</returns>
    public FileErrorPersistence Persistence { get; } = persistence;
}

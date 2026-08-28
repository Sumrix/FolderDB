using System;

namespace FolderDB.FileStorage;

/// <summary>
/// Represents information about a file processing error.
/// </summary>
/// <param name="Reason">
/// The reason FolderDB classified the file error.
/// </param>
/// <param name="Persistence">
/// A value that indicates whether FolderDB classified the file error as temporary or persistent.
/// </param>
/// <param name="ExceptionType">
/// The full CLR type name of the exception observed while processing the file.
/// </param>
/// <param name="Message">
/// The exception message captured while processing the file.
/// </param>
/// <param name="HResult">
/// The exception HRESULT captured while processing the file.
/// </param>
public sealed record FileErrorInfo(
    FileErrorReason Reason,
    FileErrorPersistence Persistence,
    string ExceptionType,
    string Message,
    int HResult)
{
    // TODO: We probably don't need persistence
    /// <summary>
    /// Creates a <see cref="FileErrorInfo"/> from the exception that caused a file error.
    /// </summary>
    /// <param name="reason">The reason FolderDB classified the file error.</param>
    /// <param name="persistence">
    /// A value that indicates whether FolderDB classified the file error as temporary or persistent.
    /// </param>
    /// <param name="exception">The exception observed while processing the file.</param>
    /// <returns>A new <see cref="FileErrorInfo"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static FileErrorInfo Create(
        FileErrorReason reason,
        FileErrorPersistence persistence,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new(reason,
            persistence,
            exception.GetType().FullName!,
            exception.Message,
            exception.HResult);
    }
}

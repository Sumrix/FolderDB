using System;

namespace FolderDB.Infrastructure.Exceptions;

/// <summary>
/// The exception that is thrown when a file name cannot be generated for a record.
/// </summary>
/// <param name="message">The error message that explains the reason for the exception.</param>
/// <param name="inner">
/// The exception that is the cause of the current exception, or <see langword="null"/> if no inner
/// exception is specified.
/// </param>
public class FileNameGenerationException(string message, Exception? inner = null) : Exception(message, inner);
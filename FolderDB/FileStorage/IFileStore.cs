using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderDB.FileStorage;

/// <summary>
/// Provides safe low-level file operations.
/// Implementations are expected to convert file access failures into result values instead of throwing.
/// To change file access error classification, provide a custom implementation of this interface.
/// </summary>
public interface IFileStore
{
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    Task<FileReadResult<T>> ReadAsync<T>(string path, Func<Stream, Task<T>> parseAction, CancellationToken cancellationToken);

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    Task<FileWriteResult> WriteAsync(string path, Func<Stream, Task> writeAction, CancellationToken cancellationToken);

    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    Task<FileDeleteResult> DeleteAsync(string path, CancellationToken cancellationToken);

    FileFingerprint GetFileFingerprint(string path);
}

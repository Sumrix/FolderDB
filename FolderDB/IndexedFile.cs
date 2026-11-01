using FolderDB.FileStorage;

namespace FolderDB;

/// <summary>
/// Represents the indexed state of the file currently associated with a record.
/// </summary>
/// <typeparam name="TProjection">The projection type the collection was defined with.</typeparam>
/// <remarks>
/// The index is an eventually consistent view of files on disk. This describes the last state
/// processed by FolderDB and may lag behind the current file-system state.
/// </remarks>
/// <param name="Projection">
/// The projection indexed for the file, taken from the last successful read.
/// </param>
/// <param name="ErrorInfo">
/// Diagnostic information for the last file error, or <see langword="null"/> if the file was last
/// read successfully.
/// </param>
/// <param name="FileName">
/// The name of the file in the collection directory.
/// </param>
public sealed record IndexedFile<TProjection>(
    TProjection? Projection,
    FileErrorInfo? ErrorInfo,
    string FileName);

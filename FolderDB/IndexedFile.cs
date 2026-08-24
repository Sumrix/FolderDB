using FolderDB.FileStorage;

namespace FolderDB;

/// <summary>
/// Describes the file currently selected by the index for one record id.
/// </summary>
/// <remarks>
/// The index is an eventually consistent view of files on disk. This entry describes
/// the last state processed by FolderDB and may lag behind the current file-system state.
/// </remarks>
/// <param name="Projection">
/// The indexed projection for a committed file. For non-committed files this value is <see langword="null"/>.
/// </param>
/// <param name="ErrorInfo">
/// Diagnostic information for a file error, or <see langword="null"/> when no file error is known.
/// </param>
/// <param name="FileName">
/// The selected file name relative to the table directory.
/// </param>
public sealed record IndexedFile<TProjection>(
    TProjection? Projection,
    FileErrorInfo? ErrorInfo,
    string FileName);

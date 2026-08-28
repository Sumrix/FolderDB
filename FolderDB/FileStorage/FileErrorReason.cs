namespace FolderDB.FileStorage;

/// <summary>
/// Specifies why FolderDB classified a file operation as failed.
/// </summary>
public enum FileErrorReason
{
    /// <summary>
    /// The file could not be read, written, deleted, or otherwise accessed.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The file was accessed, but its content could not be decoded or written as a valid record.
    /// </summary>
    Invalid
}

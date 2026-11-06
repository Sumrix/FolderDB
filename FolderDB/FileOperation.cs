namespace FolderDB;

/// <summary>
/// Specifies a file operation that <see cref="StoreOptions.Retry"/> chooses retry delays for.
/// </summary>
public enum FileOperation
{
    /// <summary>Reading a file.</summary>
    Read,

    /// <summary>Writing a file.</summary>
    Write,

    /// <summary>Deleting a file.</summary>
    Delete
}

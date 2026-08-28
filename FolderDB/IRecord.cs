namespace FolderDB;

/// <summary>
/// Represents a record that a table stores and identifies by its id.
/// </summary>
/// <typeparam name="TKey">The type of the record id.</typeparam>
public interface IRecord<out TKey>
{
    /// <summary>
    /// Gets the id that uniquely identifies the record within its table.
    /// </summary>
    /// <returns>The id of the record.</returns>
    /// <remarks>
    /// The id is the primary key of the table: it selects the file the record is stored in, and one
    /// record is kept per id. It must not be <see langword="null"/>, and it must stay the same for
    /// the lifetime of the record, because changing it stores a different record instead of
    /// updating this one.
    /// </remarks>
    TKey Id { get; }
}

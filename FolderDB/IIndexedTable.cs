using System.Collections.Generic;

namespace FolderDB;

/// <summary>
/// Represents a table-like API with a projection index over the records.
/// </summary>
public interface IIndexedTable<TKey, TRecord, TProjection> : ITable<TKey, TRecord>
{
    /// <summary>
    /// Gets the committed projection view. Unavailable, invalid, and reserved files are excluded.
    /// </summary>
    IReadOnlyDictionary<TKey, TProjection> Projections { get; }

    /// <summary>
    /// Gets the full index, including records whose files are currently unavailable or invalid.
    /// </summary>
    IReadOnlyDictionary<TKey, IndexedFile<TProjection>> Files { get; }
}

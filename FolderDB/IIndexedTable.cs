using System.Collections.Generic;

namespace FolderDB;

/// <summary>
/// Represents a table-like API with a projection index over file-backed records.
/// </summary>
/// <typeparam name="TKey">The type of the record id.</typeparam>
/// <typeparam name="TRecord">The record type stored in the table.</typeparam>
/// <typeparam name="TProjection">The projection type the table was defined with.</typeparam>
public interface IIndexedTable<TKey, TRecord, TProjection> : ITable<TKey, TRecord>
    where TRecord : IRecord<TKey>
{
    /// <summary>
    /// Gets a collection containing the indexed projection of each record in the table.
    /// </summary>
    /// <returns>A dictionary containing the projections, keyed by record id.</returns>
    /// <remarks>
    /// Contains the records in <see cref="Files"/> that carry no
    /// <see cref="IndexedFile{TProjection}.ErrorInfo"/>.
    /// </remarks>
    IReadOnlyDictionary<TKey, TProjection> Projections { get; }

    /// <summary>
    /// Gets a collection containing the indexed state of the file currently associated with each
    /// record in the table.
    /// </summary>
    /// <returns>
    /// A dictionary containing an <see cref="IndexedFile{TProjection}"/> for each record, keyed by
    /// record id.
    /// </returns>
    /// <remarks>
    /// Exactly one file is associated with each id. When several files on disk carry the same id,
    /// a file that reads successfully is preferred to one with a file error, then the file written
    /// last, and then the first file in lexicographic order. A file that has never been read
    /// successfully is not indexed at all.
    /// </remarks>
    IReadOnlyDictionary<TKey, IndexedFile<TProjection>> Files { get; }
}

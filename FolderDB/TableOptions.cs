using System;
using System.Collections.Generic;
using System.IO;

namespace FolderDB;

/// <summary>
/// Provides options to be used with <see cref="TableDefinitionBuilder"/>.
/// </summary>
/// <typeparam name="TKey">The type of the record id.</typeparam>
/// <typeparam name="TRecord">The record type stored in the table.</typeparam>
public class TableOptions<TKey, TRecord>
{
    private string? _name;

    /// <summary>
    /// Gets or sets the name of the table, which is also the name of the directory that holds it.
    /// </summary>
    /// <returns>
    /// The name of the table. The default is <see langword="null"/>, which indicates that the name
    /// of <typeparamref name="TRecord"/> is used.
    /// </returns>
    /// <remarks>
    /// Characters that a file name cannot contain are replaced with an underscore, so two names
    /// that differ only in those characters end up colliding.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is empty or consists only of white space.</exception>
    public string? Name
    {
        get => _name;
        set
        {
            if (value is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
            }

            _name = value;
        }
    }

    /// <summary>
    /// Gets or sets the comparer that orders record ids.
    /// </summary>
    /// <returns>
    /// The comparer that orders record ids. The default is <see langword="null"/>, which indicates
    /// that <see cref="Comparer{T}.Default"/> is used.
    /// </returns>
    public IComparer<TKey>? KeyComparer { get; set; }

    /// <summary>
    /// Gets or sets the comparer that decides whether two record ids are the same.
    /// </summary>
    /// <returns>
    /// The comparer that decides whether two record ids are the same. The default is
    /// <see langword="null"/>, which indicates that <see cref="EqualityComparer{T}.Default"/> is used.
    /// </returns>
    public IEqualityComparer<TKey>? KeyEqualityComparer { get; set; }

    /// <summary>
    /// Gets or sets the generator that proposes file names for a record, in the order they are tried.
    /// </summary>
    /// <returns>
    /// The generator that proposes file names for a record. The default is <see langword="null"/>,
    /// which indicates that the id of the record is used.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A candidate is a file name without an extension, which the table appends. It must not be
    /// <see langword="null"/>, empty, or consist only of white-space characters, must not be too
    /// long for a file name, and must not contain any of the characters returned by
    /// <see cref="Path.GetInvalidFileNameChars"/>.
    /// </para>
    /// <para>
    /// Candidates are enumerated lazily. The next one is taken when a name is already taken by
    /// another record, up to <see cref="DatabaseOptions.MaxFileNameReserveAttempts"/> times;
    /// running out of candidates fails the operation.
    /// </para>
    /// </remarks>
    public Func<TRecord, IEnumerable<string>>? FileNameGenerator { get; set; }
}

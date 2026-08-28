using FolderDB.Encoding;

namespace FolderDB;

/// <summary>
/// Represents a record that <see cref="RecordSchemaBuilder"/> can upgrade between schema versions.
/// </summary>
public interface IVersionedRecord
{
    /// <summary>
    /// Gets the schema version of the record type.
    /// </summary>
    /// <returns>The schema version.</returns>
    /// <remarks>
    /// The value is written to the record file and selects the decoder that reads the file back, so
    /// every instance of the type reports the same version. It must equal the version the type was
    /// registered with in <see cref="RecordSchemaBuilder"/>; an upgrade that produces a record
    /// reporting a different version fails.
    /// </remarks>
    int SchemaVersion { get; }
}

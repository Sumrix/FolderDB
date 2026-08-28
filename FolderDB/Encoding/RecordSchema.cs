using System;
using System.Text.Json.Serialization.Metadata;

namespace FolderDB.Encoding;

/// <summary>
/// Describes how records of one type are read and written: how to decode any known schema version
/// and upgrade it to the current one, and which serializer metadata writes it back.
/// </summary>
/// <typeparam name="TRecord">The record type produced by this schema.</typeparam>
public class RecordSchema<TRecord>
{
    /// <summary>
    /// Initializes a new <see cref="RecordSchema{TRecord}"/>.
    /// </summary>
    /// <param name="decoder">The decoder that reads a record from JSON and upgrades it to the current schema version.</param>
    /// <param name="jsonTypeInfo">The serializer metadata used to write records.</param>
    /// <param name="currentSchemaVersion">The current schema version for versioned records, or <see langword="null"/> for non-versioned records.</param>
    /// <exception cref="ArgumentNullException"><paramref name="decoder"/> or <paramref name="jsonTypeInfo"/> is <see langword="null"/>.</exception>
    public RecordSchema(
        ISchemaAwareRecordDecoder<TRecord> decoder,
        JsonTypeInfo<TRecord> jsonTypeInfo,
        int? currentSchemaVersion = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        Decoder = decoder;
        JsonTypeInfo = jsonTypeInfo;
        CurrentSchemaVersion = currentSchemaVersion;
    }

    /// <summary>
    /// Gets the decoder that reads records from JSON and upgrades them to the current schema version.
    /// </summary>
    public ISchemaAwareRecordDecoder<TRecord> Decoder { get; }

    /// <summary>
    /// Gets the serializer metadata used to write records.
    /// </summary>
    public JsonTypeInfo<TRecord> JsonTypeInfo { get; }

    /// <summary>
    /// Gets the current schema version, or <see langword="null"/> if versioning is not used.
    /// </summary>
    public int? CurrentSchemaVersion { get; }
}

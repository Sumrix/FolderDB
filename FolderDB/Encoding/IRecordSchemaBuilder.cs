using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FolderDB.Encoding;

/// <summary>
/// Represents one version of a record schema: it can add the next version or build the schema.
/// </summary>
/// <typeparam name="TCurrent">The current record type produced by this builder step.</typeparam>
public interface IRecordSchemaBuilder<TCurrent> : IRecordSchemaFinalBuilder<TCurrent>
    where TCurrent : class, IVersionedRecord
{
    /// <summary>
    /// Adds an upgrade to a newer schema version using serializer metadata resolved from the provided options.
    /// </summary>
    /// <typeparam name="TTo">The record type produced by the new schema version.</typeparam>
    /// <param name="toVersion">The schema version produced by the upgrade step.</param>
    /// <param name="upgrade">The function that converts the current record to the new record type.</param>
    /// <param name="options">The serializer options used to resolve <see cref="JsonTypeInfo{T}"/> for <typeparamref name="TTo"/>. If <see langword="null"/>, <see cref="JsonSerializerOptions.Default"/> is used.</param>
    /// <returns>A builder step that continues the upgrade chain from <typeparamref name="TTo"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="upgrade"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toVersion"/> is not greater than the current schema version.</exception>
    /// <exception cref="InvalidOperationException">Serializer metadata for <typeparamref name="TTo"/> cannot be resolved from <paramref name="options"/>.</exception>
    [RequiresUnreferencedCode("Uses reflection-based resolver via populateMissingResolver.")]
    [RequiresDynamicCode("May require runtime code generation for reflection-based serialization.")]
    public IRecordSchemaBuilder<TTo> UpgradeTo<TTo>(
        int toVersion,
        Func<TCurrent, TTo> upgrade,
        JsonSerializerOptions? options = null)
        where TTo : class, IVersionedRecord;

    /// <summary>
    /// Adds an upgrade to a newer schema version using explicit serializer metadata.
    /// </summary>
    /// <typeparam name="TTo">The record type produced by the new schema version.</typeparam>
    /// <param name="toVersion">The schema version produced by the upgrade step.</param>
    /// <param name="upgrade">The function that converts the current record to the new record type.</param>
    /// <param name="toTypeInfo">The serializer metadata used to read records of version <paramref name="toVersion"/>.</param>
    /// <returns>A builder step that continues the upgrade chain from <typeparamref name="TTo"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="upgrade"/> or <paramref name="toTypeInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="toVersion"/> is not greater than the current schema version.</exception>
    public IRecordSchemaBuilder<TTo> UpgradeTo<TTo>(
        int toVersion,
        Func<TCurrent, TTo> upgrade,
        JsonTypeInfo<TTo> toTypeInfo)
        where TTo : class, IVersionedRecord;
}

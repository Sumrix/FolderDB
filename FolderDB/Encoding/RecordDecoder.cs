using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FolderDB.Infrastructure.Exceptions;

namespace FolderDB.Encoding;

public class RecordDecoder<TFrom, TCurrent>(
    IRecordUpgrader<TFrom, TCurrent> upgrader,
    JsonTypeInfo<TFrom> typeInfo)
    : IRecordDecoder<TCurrent>
{
    /// <exception cref="RecordConversionException">
    /// <typeparamref name="TFrom"/> is not compatible with the JSON.
    ///
    /// -or-
    ///
    /// There is no compatible <see cref="JsonConverter"/> for <typeparamref name="TFrom"/> or its
    /// serializable members.
    ///
    /// -or-
    ///
    /// The upgrade to <typeparamref name="TCurrent"/> failed.
    /// </exception>
    public TCurrent Decode(JsonDocument document)
    {
        try
        {
            var record = document.Deserialize(typeInfo)
                ?? throw new RecordConversionException($"Failed to deserialize JSON to {typeof(TFrom).Name}");

            return upgrader.Upgrade(record);
        }
        catch (RecordConversionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RecordConversionException(
                $"Failed to convert record from {typeof(TFrom).Name} to {typeof(TCurrent).Name}.",
                inner: ex);
        }
    }
}

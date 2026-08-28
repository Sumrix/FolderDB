using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FolderDB.Infrastructure.Exceptions;

namespace FolderDB.Encoding;

public class DirectRecordDecoder<TRecord>(JsonTypeInfo<TRecord> jsonTypeInfo) : ISchemaAwareRecordDecoder<TRecord>
{
    /// <exception cref="RecordConversionException">
    /// <typeparamref name="TRecord"/> is not compatible with the JSON.
    ///
    /// -or-
    ///
    /// There is no compatible <see cref="JsonConverter"/> for <typeparamref name="TRecord"/> or its
    /// serializable members.
    /// </exception>
    public RecordDecodeResult<TRecord> Decode(JsonDocument document)
    {
        try
        {
            var record = document.Deserialize(jsonTypeInfo)
                ?? throw new RecordConversionException($"Failed to deserialize JSON to {typeof(TRecord).Name}.");

            return new RecordDecodeResult<TRecord>(false, null, null, record);
        }
        catch (RecordConversionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RecordConversionException(
                $"Failed to convert record to {typeof(TRecord).Name}.",
                inner: ex);
        }
    }
}

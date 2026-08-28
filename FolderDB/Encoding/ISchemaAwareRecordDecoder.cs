using System.Text.Json;
using FolderDB.Infrastructure.Exceptions;

namespace FolderDB.Encoding;

public interface ISchemaAwareRecordDecoder<TRecord>
{
    /// <exception cref="RecordConversionException">
    /// <paramref name="document"/> cannot be converted to <typeparamref name="TRecord"/>.
    /// </exception>
    RecordDecodeResult<TRecord> Decode(JsonDocument document);
}
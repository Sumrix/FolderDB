using System.Text.Json;
using FolderDB.Infrastructure.Exceptions;

namespace FolderDB.Encoding;

public interface IRecordDecoder<out TRecord>
{
    /// <exception cref="RecordConversionException">
    /// <paramref name="document"/> cannot be converted to <typeparamref name="TRecord"/>.
    /// </exception>
    TRecord Decode(JsonDocument document);
}

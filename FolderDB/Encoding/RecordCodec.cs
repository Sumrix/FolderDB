using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Infrastructure.Exceptions;

namespace FolderDB.Encoding;

public class RecordCodec<TKey, TRecord>(RecordSchema<TRecord> recordSchema)
    : IRecordCodec<TKey, TRecord>
    where TRecord : IRecord<TKey>
{
    public int? CurrentSchemaVersion => recordSchema.CurrentSchemaVersion;

    public async Task<RecordDecodeResult<TRecord>> DecodeAsync(Stream jsonStream, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);
            return recordSchema.Decoder.Decode(document);
        }
        catch (RecordConversionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RecordConversionException(
                $"Failed to decode a {typeof(TRecord).Name} record.",
                inner: ex);
        }
    }

    public async Task EncodeAsync(Stream jsonStream, TRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await JsonSerializer.SerializeAsync(jsonStream, record, recordSchema.JsonTypeInfo, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RecordConversionException(
                $"Failed to encode a {typeof(TRecord).Name} record.",
                inner: ex);
        }
    }
}

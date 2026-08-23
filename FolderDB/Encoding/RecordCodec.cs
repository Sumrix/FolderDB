using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderDB.Encoding;

public class RecordCodec<TKey, TRecord>(RecordSchema<TRecord> recordSchema)
    : IRecordCodec<TKey, TRecord>
    where TRecord : IRecord<TKey>
{
    public int? CurrentSchemaVersion => recordSchema.CurrentSchemaVersion;

    public async Task<RecordDecodeResult<TRecord>> DecodeAsync(Stream jsonStream, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);
        return recordSchema.Decoder.Decode(document);
    }

    public async Task EncodeAsync(Stream jsonStream, TRecord record, CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(jsonStream, record, recordSchema.JsonTypeInfo, cancellationToken);
    }
}

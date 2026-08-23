using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderDB.Encoding;

/// <summary>
/// Encodes records to streams and decodes records from streams for table storage.
/// </summary>
/// <typeparam name="TKey">The record key type.</typeparam>
/// <typeparam name="TRecord">The record type.</typeparam>
public interface IRecordCodec<TKey, TRecord>
{
    int? CurrentSchemaVersion { get; }

    Task<RecordDecodeResult<TRecord>> DecodeAsync(Stream stream, CancellationToken cancellationToken);

    Task EncodeAsync(Stream stream, TRecord record, CancellationToken cancellationToken);
}

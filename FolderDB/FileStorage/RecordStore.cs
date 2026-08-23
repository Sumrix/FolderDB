using System.Threading;
using System.Threading.Tasks;
using FolderDB.Encoding;

namespace FolderDB.FileStorage;

public sealed class RecordStore<TKey, TRecord>(
    IRecordCodec<TKey, TRecord> codec,
    IFileStore store)
{
    public Task<FileReadResult<RecordDecodeResult<TRecord>>> ReadAsync(string path, CancellationToken cancellationToken)
        => store.ReadAsync(path, stream => codec.DecodeAsync(stream, cancellationToken), cancellationToken);

    public Task<FileWriteResult> WriteAsync(string filePath, TRecord record, CancellationToken cancellationToken)
        => store.WriteAsync(filePath, stream => codec.EncodeAsync(stream, record, cancellationToken), cancellationToken);

    public Task<FileDeleteResult> DeleteAsync(string filePath, CancellationToken cancellationToken)
        => store.DeleteAsync(filePath, cancellationToken);
}

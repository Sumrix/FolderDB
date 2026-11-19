using System;
using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;
using FolderDB.Encoding;

namespace FolderDB.Runtime;

public sealed class CollectionContext<TKey, TRecord, TProjection>
{
    public required string Name { get; init; }
    public required IComparer<TKey> KeyComparer { get; init; }
    public required IEqualityComparer<TKey> KeyEqualityComparer { get; init; }
    public required Func<TRecord, IEnumerable<string>> FileNameGenerator { get; init; }
    public required Func<TRecord, TProjection> CreateProjection { get; init; }
    public required IRecordCodec<TKey, TRecord> RecordCodec { get; init; }
    public required JsonTypeInfo<TKey> KeyTypeInfo { get; init; }
    public required JsonTypeInfo<TProjection> ProjectionTypeInfo { get; init; }
}

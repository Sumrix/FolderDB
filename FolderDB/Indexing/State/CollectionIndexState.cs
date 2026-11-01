using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using FolderDB.Infrastructure.Helpers;

namespace FolderDB.Indexing.State;

/// <summary>
/// Stores the mutable in-memory state of the collection index.
/// </summary>
public class CollectionIndexState<TKey, TProjection>
    where TKey : notnull
{
    public ConcurrentDictionary<TKey, RecordIndexState<TKey, TProjection>> Records { get; }

    public ConcurrentDictionary<string, FileIndexState<TKey, TProjection>> Files { get; }

    public CollectionIndexState(IEqualityComparer<TKey> keyEqualityComparer)
    {
        ArgumentNullException.ThrowIfNull(keyEqualityComparer);

        Records = new(keyEqualityComparer);
        Files = new(PathHelper.OSDependedPathComparer);
    }
}

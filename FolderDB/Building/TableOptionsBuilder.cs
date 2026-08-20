using System;
using System.Collections.Generic;

namespace FolderDB.Building;

public abstract class TableOptionsBuilder<TKey, TRecord, TBuilder>
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
    where TBuilder : TableOptionsBuilder<TKey, TRecord, TBuilder>
{
    private protected TableOptions<TKey, TRecord> Options { get; }

    private protected TableOptionsBuilder(TableOptions<TKey, TRecord> options)
    {
        Options = options;
    }

    protected abstract TBuilder This { get; }

    public TBuilder WithName(string name)
    {
        Options.Name = name;
        return This;
    }

    public TBuilder WithKeyComparer(IComparer<TKey> keyComparer)
    {
        ArgumentNullException.ThrowIfNull(keyComparer);
        Options.KeyComparer = keyComparer;
        return This;
    }

    public TBuilder WithKeyEqualityComparer(IEqualityComparer<TKey> keyEqualityComparer)
    {
        ArgumentNullException.ThrowIfNull(keyEqualityComparer);
        Options.KeyEqualityComparer = keyEqualityComparer;
        return This;
    }

    public TBuilder WithFileNaming(Func<TRecord, IEnumerable<string>> fileNameGenerator)
    {
        ArgumentNullException.ThrowIfNull(fileNameGenerator);
        Options.FileNameGenerator = fileNameGenerator;
        return This;
    }

    public TBuilder WithFileNaming(Func<TRecord, string> fileNameGenerator)
    {
        ArgumentNullException.ThrowIfNull(fileNameGenerator);
        Options.FileNameGenerator = record => [fileNameGenerator(record)];
        return This;
    }
}

using System;
using System.Collections.Generic;

namespace FolderDB;

public class TableOptions<TKey, TRecord>
{
    private string? _name;

    public string? Name
    {
        get => _name;
        set
        {
            if (value is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
            }

            _name = value;
        }
    }

    public IComparer<TKey>? KeyComparer { get; set; }

    public IEqualityComparer<TKey>? KeyEqualityComparer { get; set; }

    public Func<TRecord, IEnumerable<string>>? FileNameGenerator { get; set; }
}

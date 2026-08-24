using System;
using FolderDB.Infrastructure.Exceptions;

namespace FolderDB.Encoding;

public class ChainedUpgrader<TFrom, TNext, TCurrent>(
    Func<TFrom, TNext> upgradeFunction,
    IRecordUpgrader<TNext, TCurrent> nextUpgrader,
    int expectedVersion)
    : IRecordUpgrader<TFrom, TCurrent>
    where TNext : class, IVersionedRecord
{
    public TCurrent Upgrade(TFrom record)
    {
        var next = upgradeFunction(record)
            ?? throw new RecordConversionException(
                $"Upgrade returned null: expected={expectedVersion}, type={typeof(TNext).Name}.");

        if (next.SchemaVersion != expectedVersion)
        {
            throw new RecordConversionException(
                $"Upgrade produced unexpected schema version: expected={expectedVersion}, actual={next.SchemaVersion}, type={typeof(TNext).Name}.");
        }

        return nextUpgrader.Upgrade(next);
    }
}

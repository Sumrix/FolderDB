using System;
using Microsoft.Extensions.Logging;

namespace FolderDB.Infrastructure.Logging;

internal static class CollectionLoggerFactoryExtensions
{
    public static ILoggerFactory CreateCollectionScopedLoggerFactory(this ILoggerFactory loggerFactory, string collectionName)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return new CollectionLoggerFactory(loggerFactory, collectionName);
    }
}

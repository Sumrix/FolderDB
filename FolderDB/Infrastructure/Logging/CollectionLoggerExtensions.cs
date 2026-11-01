using System;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace FolderDB.Infrastructure.Logging;

internal static class CollectionLoggerExtensions
{
    public static ILogger CreateCollectionScopedLogger(this ILogger logger, string collectionName)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return new CollectionLogger(logger, collectionName);
    }

    public static ILogger<T> CreateCollectionScopedLogger<T>(this ILogger<T> logger, string collectionName)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return new CollectionLogger<T>(logger, collectionName);
    }

    public static IDisposable? BeginMethodScope(this ILogger logger, [CallerMemberName] string method = "")
    {
        ArgumentNullException.ThrowIfNull(logger);

        return logger.BeginScope(new MethodScope(method));
    }
}

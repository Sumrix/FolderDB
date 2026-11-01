using System;
using Microsoft.Extensions.Logging;

namespace FolderDB.Infrastructure.Logging;

internal sealed class CollectionLoggerFactory(ILoggerFactory inner, string collectionName) : ILoggerFactory
{
    private readonly ILoggerFactory _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly string _collectionName = collectionName ?? throw new ArgumentNullException(nameof(collectionName));

    public ILogger CreateLogger(string categoryName) =>
        _inner.CreateLogger(categoryName).CreateCollectionScopedLogger(_collectionName);

    public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

    // This wrapper does not own the inner factory lifecycle.
    public void Dispose()
    {
    }
}

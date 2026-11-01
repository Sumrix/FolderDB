using System;
using Microsoft.Extensions.Logging;

namespace FolderDB.Infrastructure.Logging;

internal class CollectionLogger(ILogger inner, string collectionName) : ILogger
{
    private readonly ILogger _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly string _collectionName = collectionName ?? throw new ArgumentNullException(nameof(collectionName));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        if (state is MethodScope methodScope)
        {
            return _inner.BeginScope(new CollectionMethodScope(_collectionName, methodScope.Method));
        }

        return _inner.BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}

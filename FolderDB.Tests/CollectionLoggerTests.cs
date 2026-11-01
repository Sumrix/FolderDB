using System;
using System.Collections.Generic;
using FolderDB.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace FolderDB.Tests;

public class CollectionLoggerTests
{
    [Fact]
    public void BeginMethodScope_WhenLoggerCreatedViaGenericFactory_WrapsMethodScopeIntoCollectionMethodScope()
    {
        var innerFactory = new CaptureLoggerFactory();
        var loggerFactory = innerFactory.CreateCollectionScopedLoggerFactory("users");
        var logger = loggerFactory.CreateLogger<CollectionLoggerTests>();

        using var _ = logger.BeginMethodScope();

        var scope = Assert.IsType<CollectionMethodScope>(innerFactory.Logger.LastScope);

        Assert.Equal(2, scope.Count);
        Assert.Equal(new KeyValuePair<string, object?>("Collection", "users"), scope[0]);
        Assert.Equal(new KeyValuePair<string, object?>("Method", nameof(BeginMethodScope_WhenLoggerCreatedViaGenericFactory_WrapsMethodScopeIntoCollectionMethodScope)), scope[1]);
    }

    private sealed class CaptureLoggerFactory : ILoggerFactory
    {
        public CaptureLogger Logger { get; } = new();

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => Logger;

        public void Dispose()
        {
        }
    }

    private sealed class CaptureLogger : ILogger
    {
        public object? LastScope { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            LastScope = state;
            return NoopDisposable.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

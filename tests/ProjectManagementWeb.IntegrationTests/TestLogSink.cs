using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ProjectManagementWeb.IntegrationTests;

internal sealed class TestLogSink : ILoggerProvider, ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyCollection<(LogLevel Level, string Message)> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => this;

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Enqueue((logLevel, formatter(state, exception)));
    }

    public void Dispose()
    {
    }
}

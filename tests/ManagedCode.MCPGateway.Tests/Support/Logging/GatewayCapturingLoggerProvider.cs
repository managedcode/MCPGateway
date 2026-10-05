using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class GatewayCapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<GatewayCapturedLog> _entries = new();

    public IReadOnlyList<GatewayCapturedLog> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) =>
        new GatewayCapturingLogger(categoryName, _entries);

    public void Dispose() { }
}

internal sealed class GatewayCapturingLogger(
    string categoryName,
    ConcurrentQueue<GatewayCapturedLog> entries
) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) => entries.Enqueue(new GatewayCapturedLog(categoryName, formatter(state, exception), exception?.ToString()));
}

internal sealed record GatewayCapturedLog(string Category, string Message, string? ExceptionText);

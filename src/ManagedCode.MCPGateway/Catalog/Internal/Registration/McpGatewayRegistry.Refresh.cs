using System.Collections.Concurrent;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRegistry
{
    private const string RefreshConflictMessage = "The source catalog changed during refresh; retry against the current revision.";
    private const string MissingRefreshSourceMessage = "The requested source is unavailable.";
    private readonly ConcurrentDictionary<string, Lazy<Task>> _refreshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _refreshTimeout = ValidateRefreshTimeout(options.Value.SourceRefreshTimeout);

    private static TimeSpan ValidateRefreshTimeout(TimeSpan value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
        return value;
    }

    public async ValueTask RefreshSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        using (var snapshot = CreateSnapshot())
        {
            if (!snapshot.Registrations.Any(source => source.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(MissingRefreshSourceMessage, nameof(sourceId));
            }
        }
        var attempt = _refreshes.GetOrAdd(sourceId, id => new Lazy<Task>(() => RefreshOwnedAsync(id)));
        try
        { await attempt.Value.WaitAsync(cancellationToken); }
        finally
        {
            if (attempt.IsValueCreated && attempt.Value.IsCompleted)
            {
                _refreshes.TryRemove(new KeyValuePair<string, Lazy<Task>>(sourceId, attempt));
            }
        }
    }

    private async Task RefreshOwnedAsync(string sourceId)
    {
        IReadOnlyList<McpGatewayToolSourceRegistration> registrations;
        int version;
        using (var snapshot = CreateSnapshot())
        {
            version = snapshot.Version;
            registrations = snapshot.Registrations;
        }
        using var timeout = new CancellationTokenSource(_refreshTimeout);
        try
        {
            await ReconfigureCoreAsync(registrations, sourceId, version, timeout.Token);
            McpGatewayInvocationTelemetry.SourceRefresh(true);
        }
        catch
        {
            McpGatewayInvocationTelemetry.SourceRefresh(false);
            throw;
        }
        finally
        {
            // Cancellation belongs to each waiter; the shared refresh owns its completion and cleanup.
            _refreshes.TryRemove(sourceId, out _);
        }
    }
}

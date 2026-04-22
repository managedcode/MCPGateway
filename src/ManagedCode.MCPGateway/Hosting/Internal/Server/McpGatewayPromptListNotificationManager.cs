using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayPromptListNotificationManager : IAsyncDisposable
{
    private readonly IMcpGatewayCatalogSource _catalogSource;
    private readonly ILogger<McpGatewayPromptListNotificationManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDisposable _changeSubscription;
    private readonly ConcurrentDictionary<string, ModelContextProtocol.Server.McpServer>
        _downstreamServers = new();
    private readonly ConcurrentDictionary<string, UpstreamSubscription> _upstreamSubscriptions =
        new(StringComparer.Ordinal);
    private int _disposed;

    public McpGatewayPromptListNotificationManager(
        IMcpGatewayCatalogSource catalogSource,
        McpGatewayPromptChangeHub changeHub,
        ILogger<McpGatewayPromptListNotificationManager> logger,
        ILoggerFactory loggerFactory
    )
    {
        _catalogSource = catalogSource;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _changeSubscription = changeHub.Subscribe(OnPromptCatalogChanged);
    }

    public async Task RegisterDownstreamServerAsync(
        ModelContextProtocol.Server.McpServer downstreamServer,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(downstreamServer);
        cancellationToken.ThrowIfCancellationRequested();

        var sessionId = downstreamServer.SessionId ?? string.Empty;
        _downstreamServers[sessionId] = downstreamServer;
        await RefreshUpstreamSubscriptionsAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _changeSubscription.Dispose();
        _downstreamServers.Clear();

        foreach (var (_, subscription) in _upstreamSubscriptions)
        {
            await subscription.Subscription.DisposeAsync();
        }

        _upstreamSubscriptions.Clear();
    }

    private void OnPromptCatalogChanged()
    {
        _ = RefreshUpstreamSubscriptionsAsync(CancellationToken.None);
        _ = NotifyPromptListChangedAsync(CancellationToken.None);
    }

    private async Task RefreshUpstreamSubscriptionsAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var snapshot = _catalogSource.CreateSnapshot();
        var activeRegistrations = snapshot.Registrations.ToDictionary(
            static registration => registration.SourceId,
            StringComparer.Ordinal
        );

        foreach (var (sourceId, existingSubscription) in _upstreamSubscriptions.ToArray())
        {
            if (
                !activeRegistrations.TryGetValue(sourceId, out var registration)
                || !ReferenceEquals(existingSubscription.Registration, registration)
            )
            {
                if (_upstreamSubscriptions.TryRemove(sourceId, out var removedSubscription))
                {
                    await removedSubscription.Subscription.DisposeAsync();
                }
            }
        }

        foreach (var registration in activeRegistrations.Values)
        {
            if (_upstreamSubscriptions.ContainsKey(registration.SourceId))
            {
                continue;
            }

            var subscription = await registration.SubscribeToPromptListChangesAsync(
                (_, token) =>
                    new ValueTask(
                        ForwardUpstreamPromptListChangedAsync(registration.SourceId, token)
                    ),
                _loggerFactory,
                cancellationToken
            );
            if (subscription is null)
            {
                continue;
            }

            var createdSubscription = new UpstreamSubscription(registration, subscription);
            if (!_upstreamSubscriptions.TryAdd(registration.SourceId, createdSubscription))
            {
                await subscription.DisposeAsync();
            }
        }
    }

    private Task ForwardUpstreamPromptListChangedAsync(
        string sourceId,
        CancellationToken cancellationToken
    )
    {
        _logger.LogDebug(
            "Forwarding MCP prompt list changed notification from upstream source '{SourceId}'.",
            sourceId
        );

        return NotifyPromptListChangedAsync(cancellationToken);
    }

    private async Task NotifyPromptListChangedAsync(CancellationToken cancellationToken)
    {
        foreach (var (sessionId, downstreamServer) in _downstreamServers.ToArray())
        {
            try
            {
                await downstreamServer.SendNotificationAsync(
                    NotificationMethods.PromptListChangedNotification,
                    new PromptListChangedNotificationParams(),
                    McpJsonUtilities.DefaultOptions,
                    cancellationToken
                );
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    exception,
                    "Failed to send MCP prompt list changed notification to session '{SessionId}'. Removing the downstream subscription.",
                    sessionId
                );

                _downstreamServers.TryRemove(sessionId, out _);
            }
        }
    }

    private sealed record UpstreamSubscription(
        McpGatewayToolSourceRegistration Registration,
        IAsyncDisposable Subscription
    );
}

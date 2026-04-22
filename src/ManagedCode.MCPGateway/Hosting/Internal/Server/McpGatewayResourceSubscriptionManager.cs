using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayResourceSubscriptionManager(
    ILogger<McpGatewayResourceSubscriptionManager> logger,
    ILoggerFactory loggerFactory
) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<SubscriptionKey, IAsyncDisposable> _subscriptions = new();

    public async Task SubscribeAsync(
        ModelContextProtocol.Server.McpServer downstreamServer,
        McpGatewayResolvedResourceRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(downstreamServer);
        ArgumentNullException.ThrowIfNull(request);

        var key = SubscriptionKey.Create(downstreamServer, request.ExposedUri);
        var subscription =
            await request.Registration.SubscribeToResourceAsync(
                request.UpstreamUri,
                (notification, token) =>
                    ForwardUpdateAsync(key, downstreamServer, request.ExposedUri, notification, token),
                loggerFactory,
                cancellationToken
            )
            ?? throw new McpException(
                $"Resource '{request.ExposedUri}' does not support subscriptions."
            );

        if (_subscriptions.TryGetValue(key, out var existingSubscription))
        {
            _subscriptions[key] = subscription;
            await existingSubscription.DisposeAsync();
            return;
        }

        _subscriptions[key] = subscription;
    }

    public async Task UnsubscribeAsync(
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(downstreamServer);
        cancellationToken.ThrowIfCancellationRequested();

        var key = SubscriptionKey.Create(downstreamServer, exposedUri);
        if (_subscriptions.TryRemove(key, out var subscription))
        {
            await subscription.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var subscriptions = _subscriptions.ToArray();
        _subscriptions.Clear();

        foreach (var (_, subscription) in subscriptions)
        {
            await subscription.DisposeAsync();
        }
    }

    private async ValueTask ForwardUpdateAsync(
        SubscriptionKey key,
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        ResourceUpdatedNotificationParams notification,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await downstreamServer.SendNotificationAsync(
                NotificationMethods.ResourceUpdatedNotification,
                new ResourceUpdatedNotificationParams
                {
                    Uri = exposedUri,
                    Meta = notification.Meta is null ? null : (JsonObject)notification.Meta.DeepClone(),
                },
                McpJsonUtilities.DefaultOptions,
                cancellationToken
            );
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Failed to forward MCP resource update notification for subscription '{SessionId}:{Uri}'. Removing the subscription.",
                key.SessionId,
                key.ExposedUri
            );

            if (_subscriptions.TryRemove(key, out var subscription))
            {
                await subscription.DisposeAsync();
            }
        }
    }

    private sealed record SubscriptionKey(string SessionId, string ExposedUri)
    {
        public static SubscriptionKey Create(
            ModelContextProtocol.Server.McpServer server,
            string exposedUri
        ) => new(server.SessionId ?? string.Empty, exposedUri);
    }
}

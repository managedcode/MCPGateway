using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayResourceSubscriptionManager(
    McpGatewayMcpServerBindingManager bindingManager,
    IServiceProvider serviceProvider,
    ILogger<McpGatewayResourceSubscriptionManager> logger,
    ILoggerFactory loggerFactory
) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<SubscriptionKey, SubscriptionState> _subscriptions = new();

    public async Task SubscribeAsync(
        IServiceProvider? requestServices,
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(downstreamServer);
        ArgumentException.ThrowIfNullOrWhiteSpace(exposedUri);

        var key = SubscriptionKey.Create(downstreamServer, exposedUri);
        var state = _subscriptions.GetOrAdd(key, _ => new SubscriptionState(downstreamServer));
        await state.Gate.WaitAsync(cancellationToken);

        IAsyncDisposable? previousSubscription = null;
        var shouldPinBinding = false;

        try
        {
            state.DownstreamServer = downstreamServer;
            shouldPinBinding = !state.HasPinnedBinding;

            await using var bindingLease = shouldPinBinding
                ? await bindingManager.PinAsync(
                    requestServices,
                    serviceProvider,
                    downstreamServer,
                    cancellationToken
                )
                : await bindingManager.AcquireAsync(
                    requestServices,
                    serviceProvider,
                    downstreamServer,
                    cancellationToken
                );

            var resolvedRequest =
                await McpGatewayMcpServerRequestResolver.ResolveResourceAsync(
                    bindingLease.Binding,
                    exposedUri,
                    cancellationToken
                ) ?? throw new McpException($"Resource '{exposedUri}' was not found.");

            var subscription =
                await resolvedRequest.Source.SubscribeToResourceAsync(
                    resolvedRequest.UpstreamUri,
                    (notification, token) =>
                        ForwardUpdateAsync(
                            key,
                            downstreamServer,
                            resolvedRequest.ExposedUri,
                            notification,
                            token
                        ),
                    loggerFactory,
                    cancellationToken
                )
                ?? throw new McpException(
                    $"Resource '{resolvedRequest.ExposedUri}' does not support subscriptions."
                );

            previousSubscription = state.Subscription;
            state.Subscription = subscription;
            if (shouldPinBinding)
            {
                state.HasPinnedBinding = true;
            }
        }
        catch
        {
            if (shouldPinBinding && !state.HasPinnedBinding)
            {
                await bindingManager.ReleaseAsync(downstreamServer);
            }

            throw;
        }
        finally
        {
            state.Gate.Release();
        }

        if (previousSubscription is not null)
        {
            await previousSubscription.DisposeAsync();
        }
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
        if (!_subscriptions.TryGetValue(key, out var state))
        {
            return;
        }

        await state.Gate.WaitAsync(cancellationToken);

        IAsyncDisposable? subscription;
        bool releasePinnedBinding;

        try
        {
            subscription = state.Subscription;
            releasePinnedBinding = state.HasPinnedBinding;
            state.Subscription = null;
            state.HasPinnedBinding = false;
        }
        finally
        {
            state.Gate.Release();
        }

        if (subscription is not null)
        {
            await subscription.DisposeAsync();
        }

        if (releasePinnedBinding)
        {
            await bindingManager.ReleaseAsync(state.DownstreamServer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var subscriptions = _subscriptions.ToArray();
        _subscriptions.Clear();

        foreach (var (_, subscription) in subscriptions)
        {
            await subscription.Gate.WaitAsync(CancellationToken.None);
            try
            {
                if (subscription.Subscription is not null)
                {
                    await subscription.Subscription.DisposeAsync();
                    subscription.Subscription = null;
                }

                if (subscription.HasPinnedBinding)
                {
                    subscription.HasPinnedBinding = false;
                    await bindingManager.ReleaseAsync(subscription.DownstreamServer);
                }
            }
            finally
            {
                subscription.Gate.Release();
                subscription.Gate.Dispose();
            }
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

            if (_subscriptions.TryGetValue(key, out var state))
            {
                await state.Gate.WaitAsync(CancellationToken.None);
                try
                {
                    if (state.Subscription is not null)
                    {
                        await state.Subscription.DisposeAsync();
                        state.Subscription = null;
                    }

                    if (state.HasPinnedBinding)
                    {
                        state.HasPinnedBinding = false;
                        await bindingManager.ReleaseAsync(state.DownstreamServer);
                    }
                }
                finally
                {
                    state.Gate.Release();
                }
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

    private sealed class SubscriptionState(ModelContextProtocol.Server.McpServer downstreamServer)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ModelContextProtocol.Server.McpServer DownstreamServer { get; set; } =
            downstreamServer;

        public IAsyncDisposable? Subscription { get; set; }

        public bool HasPinnedBinding { get; set; }
    }
}

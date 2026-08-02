using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayPromptListNotificationManager(
    McpGatewayMcpServerBindingManager bindingManager,
    McpGatewayPromptNotificationStore store,
    IServiceProvider serviceProvider,
    ILogger<McpGatewayPromptListNotificationManager> logger,
    ILoggerFactory loggerFactory
) : IAsyncDisposable
{
    private const string PromptListChangedDeliveryKey = "prompts:list_changed";

    internal int ListenerStateCount => store.Count;

    internal Task RegisterAsync(
        IServiceProvider? requestServices,
        ModelContextProtocol.Server.McpServer downstreamServer,
        string listenerId,
        RequestId subscriptionId,
        McpGatewaySubscriptionDeliveryGate deliveryGate,
        CancellationToken cancellationToken
    ) =>
        RegisterCoreAsync(
            requestServices,
            downstreamServer,
            listenerId,
            subscriptionId,
            deliveryGate,
            cancellationToken
        );

    internal ValueTask RemoveAsync(
        ModelContextProtocol.Server.McpServer downstreamServer,
        string listenerId
    ) => store.RemoveAsync(McpGatewayPromptNotificationKey.Create(downstreamServer, listenerId));

    public ValueTask DisposeAsync() => store.DisposeAsync();

    private async Task RegisterCoreAsync(
        IServiceProvider? requestServices,
        ModelContextProtocol.Server.McpServer downstreamServer,
        string listenerId,
        RequestId subscriptionId,
        McpGatewaySubscriptionDeliveryGate deliveryGate,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(downstreamServer);
        ArgumentException.ThrowIfNullOrWhiteSpace(listenerId);
        cancellationToken.ThrowIfCancellationRequested();
        store.ThrowIfDisposed();

        var key = McpGatewayPromptNotificationKey.Create(downstreamServer, listenerId);
        if (store.TryGet(key, out var existingState))
        {
            existingState.DownstreamServer = downstreamServer;
            await RefreshUpstreamSubscriptionsAsync(existingState, cancellationToken);
            return;
        }

        await using var bindingLease = await bindingManager.PinAsync(
            requestServices,
            serviceProvider,
            downstreamServer,
            cancellationToken
        );

        var createdState = new McpGatewayPromptNotificationState(
            key,
            downstreamServer,
            bindingLease.Binding,
            bindingLease.Binding.SubscribeToPromptListChanges(
                () => _ = NotifyPromptListChangedAsync(key, CancellationToken.None)
            ),
            subscriptionId,
            deliveryGate
        );

        if (!store.TryAdd(key, createdState))
        {
            await store.DisposeStateAsync(createdState);
            return;
        }

        try
        {
            store.ThrowIfDisposed();
            await RefreshUpstreamSubscriptionsAsync(createdState, cancellationToken);
        }
        catch
        {
            if (store.TryRemove(key, createdState))
            {
                await store.DisposeStateAsync(createdState);
            }

            throw;
        }
    }

    private async Task RefreshUpstreamSubscriptionsAsync(
        McpGatewayPromptNotificationState listenerState,
        CancellationToken cancellationToken
    )
    {
        if (store.IsDisposed)
        {
            return;
        }

        var activeSources = (await listenerState.Binding.ListSourcesAsync(cancellationToken)).ToDictionary(
            static source => source.SourceId,
            StringComparer.Ordinal
        );

        foreach (var (sourceId, existingSubscription) in listenerState.UpstreamSubscriptions.ToArray())
        {
            if (
                !activeSources.TryGetValue(sourceId, out var source)
                || !ReferenceEquals(existingSubscription.Source, source)
            )
            {
                if (listenerState.UpstreamSubscriptions.TryRemove(sourceId, out var removed))
                {
                    await removed.Subscription.DisposeAsync();
                }
            }
        }

        foreach (var source in activeSources.Values)
        {
            if (listenerState.UpstreamSubscriptions.ContainsKey(source.SourceId))
            {
                continue;
            }

            var subscription = await source.ListenForPromptListChangesAsync(
                (_, token) =>
                    new ValueTask(
                        ForwardUpstreamPromptListChangedAsync(
                            listenerState.Key,
                            source.SourceId,
                            token
                        )
                    ),
                loggerFactory,
                cancellationToken
            );
            if (subscription is null)
            {
                continue;
            }

            if (!IsCurrentListener(listenerState))
            {
                await subscription.DisposeAsync();
                continue;
            }

            var createdSubscription = new McpGatewayPromptUpstreamSubscription(
                source,
                subscription
            );
            if (!listenerState.UpstreamSubscriptions.TryAdd(source.SourceId, createdSubscription))
            {
                await subscription.DisposeAsync();
                continue;
            }

            if (
                !IsCurrentListener(listenerState)
                && listenerState.UpstreamSubscriptions.TryRemove(source.SourceId, out var removed)
            )
            {
                await removed.Subscription.DisposeAsync();
            }
        }
    }

    private bool IsCurrentListener(McpGatewayPromptNotificationState listenerState) =>
        store.IsCurrent(listenerState);

    private Task ForwardUpstreamPromptListChangedAsync(
        McpGatewayPromptNotificationKey key,
        string sourceId,
        CancellationToken cancellationToken
    )
    {
        logger.LogDebug(
            "Forwarding MCP prompt list changed notification from upstream source '{SourceId}' to listener '{ServerId}:{ListenerId}'.",
            sourceId,
            key.ServerId,
            key.ListenerId
        );

        return NotifyPromptListChangedAsync(key, cancellationToken);
    }

    private async Task NotifyPromptListChangedAsync(
        McpGatewayPromptNotificationKey key,
        CancellationToken cancellationToken
    )
    {
        if (!store.TryGet(key, out var listenerState))
        {
            return;
        }

        try
        {
            var delivery = new Func<CancellationToken, ValueTask>(token =>
                new ValueTask(
                    listenerState.DownstreamServer.SendNotificationAsync(
                        NotificationMethods.PromptListChangedNotification,
                        new PromptListChangedNotificationParams
                        {
                            Meta = McpGatewaySubscriptionMetadata.Create(
                                listenerState.SubscriptionId
                            ),
                        },
                        McpJsonUtilities.DefaultOptions,
                        token
                    )
                )
            );
            await listenerState.DeliveryGate.DeliverAsync(
                PromptListChangedDeliveryKey,
                delivery,
                cancellationToken
            );
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Failed to send MCP prompt list changed notification to listener '{ServerId}:{ListenerId}'. Removing it.",
                key.ServerId,
                key.ListenerId
            );

            if (store.TryRemove(key, out var removedState))
            {
                var cleanupExceptions = new List<Exception>();
                await store.DisposeStateAsync(removedState, cleanupExceptions);
                if (cleanupExceptions.Count > 0)
                {
                    logger.LogDebug(
                        new AggregateException(cleanupExceptions),
                        "Failed to clean up MCP prompt list notification listener '{ServerId}:{ListenerId}' after forwarding failed.",
                        key.ServerId,
                        key.ListenerId
                    );
                }
            }
        }
    }

}

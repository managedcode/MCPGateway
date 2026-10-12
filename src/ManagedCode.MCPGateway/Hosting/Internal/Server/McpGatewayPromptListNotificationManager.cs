using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
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
    private const string PromptNotificationDeliveryFailedLogMessage =
        "Failed to send an MCP prompt notification. Removing its listener.";
    private const string PromptNotificationCleanupFailedLogMessage =
        "Failed to clean up an MCP prompt notification listener.";

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
        var context = await McpGatewayTrustedContext.ResolveAsync(serviceProvider, cancellationToken);
        await McpGatewayAccessBoundary.DemandAsync(serviceProvider, McpGatewayOperation.SubscribePromptChanges,
            PromptListChangedDeliveryKey, null, cancellationToken);

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
        )
        { ExecutionContext = context };
        createdState.VisiblePromptFingerprint = McpGatewayDefinitionHash.Compute(
            await bindingLease.Binding.PromptCatalog.ListPromptsAsync(cancellationToken));

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
            if (!(await McpGatewayAccessBoundary.AuthorizeAsync(serviceProvider, listenerState.ExecutionContext,
                new(McpGatewayOperation.SubscribePromptChanges, PromptListChangedDeliveryKey, source.SourceId), cancellationToken)).IsAllowed)
            { continue; }
            if (listenerState.UpstreamSubscriptions.ContainsKey(source.SourceId))
            {
                continue;
            }

            var subscription = await source.ListenForPromptListChangesAsync(
                (_, token) =>
                    new ValueTask(
                        ForwardUpstreamPromptListChangedAsync(
                            listenerState.Key,
                            source,
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
        IMcpGatewayServerSource source,
        CancellationToken cancellationToken
    )
    {
        logger.LogDebug(
            "Forwarding MCP prompt list changed notification from upstream source '{SourceId}' to listener '{ServerId}:{ListenerId}'.",
            source.SourceId,
            key.ServerId,
            key.ListenerId
        );

        return NotifyPromptListChangedAsync(key, cancellationToken, source);
    }

    private async Task NotifyPromptListChangedAsync(
        McpGatewayPromptNotificationKey key,
        CancellationToken cancellationToken,
        IMcpGatewayServerSource? source = null
    )
    {
        if (!store.TryGet(key, out var listenerState))
        {
            return;
        }
        if (cancellationToken.IsCancellationRequested && !listenerState.DeliveryGate.IsOpen)
        { return; }

        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            using var actor = serviceProvider.GetService<McpGatewayExecutionScope>()?.Push(listenerState.ExecutionContext,
                scope.ServiceProvider, null, null);
            if (!(await McpGatewayAccessBoundary.AuthorizeAsync(scope.ServiceProvider, listenerState.ExecutionContext,
                new(McpGatewayOperation.ReceivePromptChanges, PromptListChangedDeliveryKey, source?.SourceId), cancellationToken)).IsAllowed)
            { return; }
            if (source is not null && !(await listenerState.Binding.ListSourcesAsync(cancellationToken)).Any(current => ReferenceEquals(current, source)))
            { return; }
            if (source is null || scope.ServiceProvider.GetService<IMcpGatewayAccessPolicy>() is not null)
            {
                var fingerprint = McpGatewayDefinitionHash.Compute(await listenerState.Binding.PromptCatalog.ListPromptsAsync(cancellationToken));
                if (!listenerState.TryUpdateVisiblePromptFingerprint(fingerprint))
                { return; }
            }
            var delivery = new Func<CancellationToken, ValueTask>(async token =>
            {
                await using var deliveryScope = serviceProvider.CreateAsyncScope();
                using var deliveryActor = serviceProvider.GetService<McpGatewayExecutionScope>()?.Push(listenerState.ExecutionContext,
                    deliveryScope.ServiceProvider, null, null);
                if (!(await McpGatewayAccessBoundary.AuthorizeAsync(deliveryScope.ServiceProvider, listenerState.ExecutionContext,
                    new(McpGatewayOperation.ReceivePromptChanges, PromptListChangedDeliveryKey, source?.SourceId), token)).IsAllowed)
                { return; }
                await listenerState.DownstreamServer.SendNotificationAsync(
                        NotificationMethods.PromptListChangedNotification,
                        new PromptListChangedNotificationParams
                        {
                            Meta = McpGatewaySubscriptionMetadata.Create(
                                listenerState.SubscriptionId
                            ),
                        },
                        McpJsonUtilities.DefaultOptions,
                        token
                    );
            });
            await listenerState.DeliveryGate.DeliverAsync(
                PromptListChangedDeliveryKey,
                delivery,
                cancellationToken
            );
        }
        catch (Exception exception) when (McpGatewayRuntimeFailureClassifier.IsRecoverable(exception))
        {
            logger.LogDebug(PromptNotificationDeliveryFailedLogMessage);

            if (store.TryRemove(key, out var removedState))
            {
                var cleanupExceptions = new List<Exception>();
                await store.DisposeStateAsync(removedState, cleanupExceptions);
                if (cleanupExceptions.Count > 0)
                {
                    logger.LogDebug(PromptNotificationCleanupFailedLogMessage);
                }
            }
        }
    }

}

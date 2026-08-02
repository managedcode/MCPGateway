using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayResourceSubscriptionManager(
    McpGatewayMcpServerBindingManager bindingManager,
    McpGatewayResourceSubscriptionRegistry registry,
    McpGatewayResourceSubscriptionCleanup cleanup,
    McpGatewayResourceSubscriptionFactory subscriptionFactory,
    McpGatewayResourceSubscriptionLifetime lifetime,
    ILogger<McpGatewayResourceSubscriptionManager> logger
) : IAsyncDisposable
{
    internal int SubscriptionStateCount => registry.Count;

    internal Task SubscribeAsync(
        IServiceProvider? requestServices,
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        string listenerId,
        RequestId subscriptionId,
        McpGatewaySubscriptionDeliveryGate deliveryGate,
        CancellationToken cancellationToken
    ) =>
        SubscribeCoreAsync(
            requestServices,
            downstreamServer,
            exposedUri,
            listenerId,
            subscriptionId,
            deliveryGate,
            cancellationToken
        );

    internal Task UnsubscribeAsync(
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        string listenerId,
        CancellationToken cancellationToken = default
    ) => UnsubscribeCoreAsync(downstreamServer, exposedUri, listenerId, cancellationToken);

    public ValueTask DisposeAsync() => lifetime.DisposeAsync();

    private async Task SubscribeCoreAsync(
        IServiceProvider? requestServices,
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        string listenerId,
        RequestId subscriptionId,
        McpGatewaySubscriptionDeliveryGate deliveryGate,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(downstreamServer);
        ArgumentException.ThrowIfNullOrWhiteSpace(exposedUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(listenerId);

        var key = McpGatewayResourceSubscriptionKey.Create(downstreamServer, listenerId, exposedUri);
        while (!cancellationToken.IsCancellationRequested)
        {
            var state = registry.GetOrAdd(key, downstreamServer);
            var result = await SubscribeStateAsync(
                requestServices,
                downstreamServer,
                exposedUri,
                key,
                state,
                subscriptionId,
                deliveryGate,
                cancellationToken
            );
            if (result.Retry)
            {
                continue;
            }

            await CleanUpReplacedSubscriptionsAsync(result, downstreamServer);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<SubscribeResult> SubscribeStateAsync(
        IServiceProvider? requestServices,
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        McpGatewayResourceSubscriptionKey key,
        McpGatewayResourceSubscriptionState state,
        RequestId subscriptionId,
        McpGatewaySubscriptionDeliveryGate deliveryGate,
        CancellationToken cancellationToken
    )
    {
        IAsyncDisposable? previousSubscription = null;
        IAsyncDisposable? failedSubscription = null;
        var shouldPinBinding = false;
        TaskCompletionSource? previousAttempt = null;
        var attempt = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releasePinnedBindingAfterEarlyFailure = false;

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.IsRetired)
            {
                return SubscribeResult.RetryResult;
            }

            state.DownstreamServer = downstreamServer;
            shouldPinBinding = !state.HasPinnedBinding;
            previousAttempt = state.ActiveAttempt;
            state.ActiveAttempt = attempt;

            var subscription = await subscriptionFactory.CreateAsync(
                requestServices,
                downstreamServer,
                exposedUri,
                key,
                attempt,
                shouldPinBinding,
                subscriptionId,
                deliveryGate,
                cancellationToken
            );
            previousSubscription = state.Subscription;
            state.Subscription = subscription;
            state.HasPinnedBinding |= shouldPinBinding;

            if (attempt.Task.IsCompletedSuccessfully)
            {
                failedSubscription = state.Subscription;
                releasePinnedBindingAfterEarlyFailure = state.HasPinnedBinding;
                state.Subscription = null;
                state.HasPinnedBinding = false;
                state.ActiveAttempt = null;
                registry.RetireIfInactive(key, state);
            }
        }
        catch
        {
            if (ReferenceEquals(state.ActiveAttempt, attempt))
            {
                state.ActiveAttempt = previousAttempt;
            }

            if (shouldPinBinding && !state.HasPinnedBinding)
            {
                await bindingManager.ReleaseAsync(downstreamServer);
            }

            registry.RetireIfInactive(key, state);
            throw;
        }
        finally
        {
            state.Gate.Release();
            McpGatewayResourceSubscriptionRegistry.Release(state);
        }

        return new SubscribeResult(
            Retry: false,
            previousSubscription,
            failedSubscription,
            releasePinnedBindingAfterEarlyFailure
        );
    }

    private async Task UnsubscribeCoreAsync(
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        string listenerId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(downstreamServer);
        cancellationToken.ThrowIfCancellationRequested();

        var key = McpGatewayResourceSubscriptionKey.Create(downstreamServer, listenerId, exposedUri);
        if (!registry.TryGet(key, out var state))
        {
            return;
        }

        await state.Gate.WaitAsync(cancellationToken);
        IAsyncDisposable? subscription;
        var releasePinnedBinding = false;
        var retiredState = false;
        var cleanupDownstreamServer = state.DownstreamServer;

        try
        {
            if (state.IsRetired)
            {
                subscription = null;
            }
            else
            {
                subscription = state.Subscription;
                releasePinnedBinding = state.HasPinnedBinding;
                cleanupDownstreamServer = state.DownstreamServer;
                state.Subscription = null;
                state.HasPinnedBinding = false;
                state.ActiveAttempt = null;
                retiredState = registry.RetireIfInactive(key, state);
            }
        }
        finally
        {
            state.Gate.Release();
            McpGatewayResourceSubscriptionRegistry.Release(state);
        }

        if (retiredState)
        {
            logger.LogDebug(
                "Removed MCP resource listener state '{ServerId}:{ListenerId}:{Uri}' after cancellation.",
                key.ServerId,
                key.ListenerId,
                key.ExposedUri
            );
        }

        var cleanupExceptions = new List<Exception>();
        await cleanup.DisposeAsync(
            subscription,
            releasePinnedBinding,
            cleanupDownstreamServer,
            cleanupExceptions
        );
        McpGatewayResourceSubscriptionCleanup.ThrowIfFailed(cleanupExceptions);
    }

    private async Task CleanUpReplacedSubscriptionsAsync(
        SubscribeResult result,
        ModelContextProtocol.Server.McpServer downstreamServer
    )
    {
        var cleanupExceptions = new List<Exception>();
        await cleanup.DisposeAsync(
            result.FailedSubscription,
            result.ReleasePinnedBindingAfterEarlyFailure,
            downstreamServer,
            cleanupExceptions
        );
        await cleanup.DisposeAsync(
            result.PreviousSubscription,
            releasePinnedBinding: false,
            downstreamServer,
            cleanupExceptions
        );
        McpGatewayResourceSubscriptionCleanup.ThrowIfFailed(cleanupExceptions);
    }

    private sealed record SubscribeResult(
        bool Retry,
        IAsyncDisposable? PreviousSubscription,
        IAsyncDisposable? FailedSubscription,
        bool ReleasePinnedBindingAfterEarlyFailure
    )
    {
        public static SubscribeResult RetryResult { get; } = new(
            Retry: true,
            PreviousSubscription: null,
            FailedSubscription: null,
            ReleasePinnedBindingAfterEarlyFailure: false
        );
    }
}

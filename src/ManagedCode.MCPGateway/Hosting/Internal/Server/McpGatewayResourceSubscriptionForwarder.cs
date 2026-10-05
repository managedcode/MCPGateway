using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayResourceSubscriptionForwarder(
    McpGatewayResourceSubscriptionRegistry registry,
    McpGatewayResourceSubscriptionCleanup cleanup,
    ILogger<McpGatewayResourceSubscriptionManager> logger
)
{
    private const string ResourceNotificationDeliveryFailedLogMessage =
        "Failed to forward an MCP resource update. Removing its listener.";
    private const string ResourceNotificationCleanupFailedLogMessage =
        "Failed to clean up an MCP resource listener.";

    public async ValueTask ForwardUpdateAsync(
        McpGatewayResourceSubscriptionKey key,
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        ResourceUpdatedNotificationParams notification,
        TaskCompletionSource attempt,
        RequestId subscriptionId,
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
                    Meta = McpGatewaySubscriptionMetadata.Create(
                        subscriptionId,
                        notification.Meta
                    ),
                },
                McpJsonUtilities.DefaultOptions,
                cancellationToken
            );
        }
        catch (Exception)
        {
            logger.LogDebug(ResourceNotificationDeliveryFailedLogMessage);

            attempt.TrySetResult();
            _ = RemoveFailedSubscriptionAsync(key, attempt);
        }
    }

    private async Task RemoveFailedSubscriptionAsync(
        McpGatewayResourceSubscriptionKey key,
        TaskCompletionSource attempt
    )
    {
        try
        {
            if (!registry.TryGet(key, out var state))
            {
                return;
            }

            await state.Gate.WaitAsync(CancellationToken.None);
            IAsyncDisposable? subscription = null;
            var releasePinnedBinding = false;
            var downstreamServer = state.DownstreamServer;
            var shouldCleanUp = false;

            try
            {
                if (state.IsRetired || !ReferenceEquals(state.ActiveAttempt, attempt))
                {
                    return;
                }

                subscription = state.Subscription;
                releasePinnedBinding = state.HasPinnedBinding;
                downstreamServer = state.DownstreamServer;
                state.Subscription = null;
                state.HasPinnedBinding = false;
                state.ActiveAttempt = null;
                registry.RetireIfInactive(key, state);
                shouldCleanUp = true;
            }
            finally
            {
                state.Gate.Release();
                McpGatewayResourceSubscriptionRegistry.Release(state);
            }

            if (!shouldCleanUp)
            {
                return;
            }

            var cleanupExceptions = new List<Exception>();
            await cleanup.DisposeAsync(
                subscription,
                releasePinnedBinding,
                downstreamServer,
                cleanupExceptions
            );
            McpGatewayResourceSubscriptionCleanup.ThrowIfFailed(cleanupExceptions);
        }
        catch (Exception)
        {
            logger.LogDebug(ResourceNotificationCleanupFailedLogMessage);
        }
    }
}

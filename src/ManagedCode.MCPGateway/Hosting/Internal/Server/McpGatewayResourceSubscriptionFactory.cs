using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayResourceSubscriptionFactory(
    McpGatewayMcpServerBindingManager bindingManager,
    McpGatewayResourceSubscriptionForwarder forwarder,
    IServiceProvider serviceProvider,
    ILoggerFactory loggerFactory
)
{
    private const string ResourceDeliveryKeyPrefix = "resource:";

    public async Task<IAsyncDisposable> CreateAsync(
        IServiceProvider? requestServices,
        ModelContextProtocol.Server.McpServer downstreamServer,
        string exposedUri,
        McpGatewayResourceSubscriptionKey key,
        TaskCompletionSource attempt,
        bool shouldPinBinding,
        RequestId subscriptionId,
        McpGatewaySubscriptionDeliveryGate deliveryGate,
        CancellationToken cancellationToken
    )
    {
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
        var resolvedRequest = await McpGatewayMcpServerRequestResolver.ResolveResourceAsync(
            bindingLease.Binding,
            exposedUri,
            cancellationToken
        ) ?? throw new McpException($"Resource '{exposedUri}' was not found.");

        return await resolvedRequest.Source.ListenForResourceUpdatesAsync(
                resolvedRequest.UpstreamUri,
                (notification, token) =>
                    deliveryGate.DeliverAsync(
                        string.Concat(
                            ResourceDeliveryKeyPrefix,
                            resolvedRequest.ExposedUri
                        ),
                        deliveryToken =>
                            forwarder.ForwardUpdateAsync(
                                key,
                                downstreamServer,
                                resolvedRequest.ExposedUri,
                                notification,
                                attempt,
                                subscriptionId,
                                deliveryToken
                            ),
                        token
                    ),
                loggerFactory,
                cancellationToken
            )
            ?? throw new McpException(
                $"Resource '{resolvedRequest.ExposedUri}' does not support subscriptions."
            );
    }
}

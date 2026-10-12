using Microsoft.Extensions.DependencyInjection;
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

        var context = await McpGatewayTrustedContext.ResolveAsync(serviceProvider, cancellationToken);
        await McpGatewayAccessBoundary.DemandAsync(serviceProvider, McpGatewayOperation.SubscribeResource,
            resolvedRequest.ExposedUri, resolvedRequest.SourceId, cancellationToken);
        var registry = bindingLease.Binding.Registry as IMcpGatewayCatalogSource;
        using var initial = registry?.CreateSnapshot();
        var version = initial?.Version;
        return await resolvedRequest.Source.ListenForResourceUpdatesAsync(
                resolvedRequest.UpstreamUri,
                async (notification, token) =>
                {
                    if (token.IsCancellationRequested && !deliveryGate.IsOpen)
                    { return; }
                    await deliveryGate.DeliverAsync(
                        string.Concat(ResourceDeliveryKeyPrefix, resolvedRequest.ExposedUri),
                        async deliveryToken =>
                        {
                            await using var scope = serviceProvider.CreateAsyncScope();
                            using var actor = serviceProvider.GetService<McpGatewayExecutionScope>()
                                ?.Push(context, scope.ServiceProvider, null, null);
                            using var current = registry?.CreateSnapshot();
                            if (current?.Version != version
                                || !(await McpGatewayAccessBoundary.AuthorizeAsync(scope.ServiceProvider, context,
                                    new(McpGatewayOperation.ReceiveResourceUpdate, resolvedRequest.ExposedUri, resolvedRequest.SourceId), deliveryToken)).IsAllowed)
                            { return; }
                            await forwarder.ForwardUpdateAsync(key, downstreamServer, resolvedRequest.ExposedUri,
                                scope.ServiceProvider.GetService<IMcpGatewayAccessPolicy>() is null ? notification
                                    : new ResourceUpdatedNotificationParams { Uri = notification.Uri },
                                attempt, subscriptionId, deliveryToken);
                        }, token);
                },
                loggerFactory,
                cancellationToken
            )
            ?? throw new McpException(
                $"Resource '{resolvedRequest.ExposedUri}' does not support subscriptions."
            );
    }
}

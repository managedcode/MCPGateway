using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway;

public static class McpGatewayChatClientExtensions
{
    public static IChatClient UseMcpGatewayAutoDiscovery(
        this IChatClient chatClient,
        McpGatewayToolSet toolSet,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? functionInvocationServices = null,
        McpGatewayAutoDiscoveryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(toolSet);

        return new McpGatewayAutoDiscoveryChatClient(
            chatClient,
            toolSet,
            loggerFactory,
            functionInvocationServices,
            options);
    }

    public static IChatClient UseMcpGatewayAutoDiscovery(
        this IChatClient chatClient,
        IServiceProvider serviceProvider,
        Action<McpGatewayAutoDiscoveryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var options = new McpGatewayAutoDiscoveryOptions();
        configure?.Invoke(options);

        return chatClient.UseMcpGatewayAutoDiscovery(
            serviceProvider.GetRequiredService<McpGatewayToolSet>(),
            serviceProvider.GetService<ILoggerFactory>(),
            serviceProvider,
            options);
    }
}

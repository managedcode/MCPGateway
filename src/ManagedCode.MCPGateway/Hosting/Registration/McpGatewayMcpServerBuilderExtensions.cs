using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

public static class McpGatewayMcpServerBuilderExtensions
{
    public static IMcpServerBuilder WithMcpGatewayCatalog(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<McpGatewayMcpServerRequestResolver>();
        builder.Services.TryAddSingleton<McpGatewayResourceSubscriptionManager>();
        builder.Services.TryAddSingleton<McpGatewayPromptListNotificationManager>();
        builder.Services.TryAddSingleton<McpGatewayMcpServerTaskStore>();
        builder.Services.TryAddSingleton<McpGatewayMcpServerHandlers>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IPostConfigureOptions<McpServerOptions>,
                McpGatewayMcpServerOptionsSetup
            >()
        );

        return builder;
    }
}

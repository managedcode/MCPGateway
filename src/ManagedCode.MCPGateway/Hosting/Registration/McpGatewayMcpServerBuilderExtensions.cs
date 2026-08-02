using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Server;

namespace ManagedCode.MCPGateway;

#pragma warning disable MCPEXP003

public static class McpGatewayMcpServerBuilderExtensions
{
    public static IMcpServerBuilder WithMcpGatewayCatalog(this IMcpServerBuilder builder) =>
        WithMcpGatewayCatalog(builder, new McpGatewayMcpServerTaskStore());

    public static IMcpServerBuilder WithMcpGatewayCatalog(
        this IMcpServerBuilder builder,
        IMcpTaskStore taskStore
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(taskStore);

        builder.Services.TryAddSingleton<IMcpGatewayServerBindingResolver, McpGatewayDefaultServerBindingResolver>();
        builder.Services.TryAddSingleton<McpGatewayMcpServerBindingManager>();
        builder.Services.TryAddSingleton<McpGatewayResourceSubscriptionRegistry>();
        builder.Services.TryAddSingleton<McpGatewayResourceSubscriptionCleanup>();
        builder.Services.TryAddSingleton<McpGatewayResourceSubscriptionForwarder>();
        builder.Services.TryAddSingleton<McpGatewayResourceSubscriptionFactory>();
        builder.Services.TryAddSingleton<McpGatewayResourceSubscriptionLifetime>();
        builder.Services.TryAddSingleton<McpGatewayResourceSubscriptionManager>();
        builder.Services.TryAddSingleton<McpGatewayPromptNotificationStore>();
        builder.Services.TryAddSingleton<McpGatewayPromptListNotificationManager>();
        builder.Services.TryAddSingleton<McpGatewayMcpServerSubscriptionCoordinator>();
        if (taskStore is McpGatewayMcpServerTaskStore gatewayTaskStore)
        {
            builder.Services.TryAddSingleton(gatewayTaskStore);
        }

        builder.Services.AddSingleton(taskStore);
        builder.WithTasks(taskStore);
        builder.WithMcpApps();
        builder.Services.TryAddSingleton<McpGatewayMcpServerHandlers>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IPostConfigureOptions<McpServerOptions>,
                McpGatewayMcpServerOptionsSetup
            >()
        );
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IPostConfigureOptions<HttpServerTransportOptions>,
                McpGatewayHttpServerTransportOptionsSetup
            >()
        );

        return builder;
    }
}

#pragma warning restore MCPEXP003

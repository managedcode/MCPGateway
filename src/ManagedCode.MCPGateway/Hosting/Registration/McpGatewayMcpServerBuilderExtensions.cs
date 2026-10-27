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
    /// <summary>Export only compact discovery, schema inspection and invocation tools.</summary>
    public static IMcpServerBuilder WithMcpGatewayDiscovery(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Configure<McpGatewayMcpServerExportOptions>(options => options.Mode = McpGatewayMcpServerExportMode.DiscoveryOnly);
        return builder.WithMcpGatewayCatalog();
    }

    public static IMcpServerBuilder WithMcpGatewayCatalog(this IMcpServerBuilder builder) =>
        ConfigureCatalog(builder, null);

    public static IMcpServerBuilder WithMcpGatewayCatalog(
        this IMcpServerBuilder builder,
        IMcpGatewayTaskStore taskStore
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(taskStore);

        return ConfigureCatalog(builder, taskStore);
    }

    private static IMcpServerBuilder ConfigureCatalog(IMcpServerBuilder builder, IMcpGatewayTaskStore? taskStore)
    {
        ArgumentNullException.ThrowIfNull(builder);
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
        var guardedStore = new McpGatewayMcpServerTaskStore(taskStore);
        builder.Services.TryAddSingleton(guardedStore);
        builder.WithTasks(guardedStore);
        if (taskStore is not null)
        {
            builder.Services.AddSingleton(taskStore);
        }
        builder.WithMcpApps();
        builder.Services.TryAddSingleton<McpGatewayMcpServerHandlers>();
        builder.Services.TryAddSingleton<McpGatewayExecutionScope>();
        builder.Services.TryAddSingleton<McpGatewayGovernanceMessageFilter>();
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

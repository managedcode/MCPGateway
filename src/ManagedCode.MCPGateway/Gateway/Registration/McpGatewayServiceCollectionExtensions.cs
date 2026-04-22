using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ManagedCode.MCPGateway;

public static class McpGatewayServiceCollectionExtensions
{
    public static IServiceCollection AddMcpGateway(
        this IServiceCollection services,
        Action<McpGatewayOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<McpGatewayOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<IMcpGatewaySearchCache, McpGatewayNoOpSearchCache>();
        services.TryAddSingleton<McpGatewayPromptChangeHub>();
        services.TryAddSingleton<IMcpGateway, McpGateway>();
        services.TryAddSingleton<IMcpGatewayRegistry, McpGatewayRegistry>();
        services.TryAddSingleton<IMcpGatewayCatalogSource>(static serviceProvider =>
            (IMcpGatewayCatalogSource)serviceProvider.GetRequiredService<IMcpGatewayRegistry>()
        );
        services.TryAddSingleton<IMcpGatewayCatalogRuntime>(static serviceProvider =>
            (IMcpGatewayCatalogRuntime)serviceProvider.GetRequiredService<IMcpGatewayRegistry>()
        );
        services.TryAddSingleton<IMcpGatewayPromptCatalog, McpGatewayPromptCatalog>();
        services.TryAddSingleton<IMcpGatewayResourceCatalog, McpGatewayResourceCatalog>();
        services.TryAddSingleton<IMcpGatewayFactory, McpGatewayFactory>();
        services.TryAddSingleton<McpGatewayToolSet>();

        return services;
    }

    public static IServiceCollection AddMcpGatewayInMemorySearchCache(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMemoryCache();
        services.Replace(
            ServiceDescriptor.Singleton<IMcpGatewaySearchCache, McpGatewayInMemorySearchCache>()
        );
        return services;
    }

    public static IServiceCollection AddMcpGatewayInMemoryToolEmbeddingStore(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMemoryCache();
        services.TryAddSingleton<
            IMcpGatewayToolEmbeddingStore,
            McpGatewayInMemoryToolEmbeddingStore
        >();
        return services;
    }

    public static IServiceCollection AddMcpGatewayIndexWarmup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, McpGatewayIndexWarmupService>()
        );
        return services;
    }
}

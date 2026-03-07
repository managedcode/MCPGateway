using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ManagedCode.MCPGateway;

public static class McpGatewayServiceCollectionExtensions
{
    public static IServiceCollection AddManagedCodeMcpGateway(
        this IServiceCollection services,
        Action<McpGatewayOptions>? configure = null)
    {
        services.AddOptions<McpGatewayOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<McpGatewayRegistry>();
        services.TryAddSingleton<McpGateway>();
        services.TryAddSingleton<IMcpGateway>(serviceProvider => serviceProvider.GetRequiredService<McpGateway>());
        services.TryAddSingleton<IMcpGatewayRegistry>(serviceProvider => serviceProvider.GetRequiredService<McpGatewayRegistry>());
        services.TryAddSingleton<McpGatewayToolSet>();

        return services;
    }
}

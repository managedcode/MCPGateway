using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ManagedCode.MCPGateway;

public static class McpGatewayServiceCollectionExtensions
{
    public static IServiceCollection AddManagedCodeMcpGateway(
        this IServiceCollection services,
        Action<McpGatewayOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<McpGatewayOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<IMcpGateway, McpGateway>();
        services.TryAddSingleton<IMcpGatewayRegistry, McpGatewayRegistry>();
        services.TryAddSingleton<McpGatewayToolSet>();

        return services;
    }

    public static IServiceCollection AddManagedCodeMcpGatewayIndexWarmup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, McpGatewayIndexWarmupService>());
        return services;
    }
}

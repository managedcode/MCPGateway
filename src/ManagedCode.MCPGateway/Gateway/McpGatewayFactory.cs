using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayFactory(
    IServiceProvider serviceProvider,
    ILoggerFactory loggerFactory
) : IMcpGatewayFactory
{
    public IMcpGatewayInstance Create() => Create(new McpGatewayOptions());

    public IMcpGatewayInstance Create(Action<McpGatewayOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new McpGatewayOptions();
        configure(options);
        return Create(options);
    }

    public IMcpGatewayInstance Create(McpGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var gatewayOptions = Options.Create(options);
        var registry = new McpGatewayRegistry(gatewayOptions);
        var catalogRuntime = (IMcpGatewayCatalogRuntime)registry;
        var runtimeServiceProvider = new McpGatewayFactoryServiceProvider(
            serviceProvider,
            registry
        );
        var promptCatalog = new McpGatewayPromptCatalog(registry, loggerFactory);
        var gateway = new McpGateway(
            runtimeServiceProvider,
            gatewayOptions,
            loggerFactory.CreateLogger<McpGateway>(),
            loggerFactory
        );

        return new McpGatewayFactoryResult(
            static async (runtimeGateway, runtimeRegistry, runtimeCatalogRuntime) =>
            {
                await runtimeGateway.DisposeAsync();

                if (
                    !ReferenceEquals(runtimeCatalogRuntime, runtimeRegistry)
                    && runtimeCatalogRuntime is IAsyncDisposable asyncCatalogRuntime
                )
                {
                    await asyncCatalogRuntime.DisposeAsync();
                }

                if (runtimeRegistry is IAsyncDisposable asyncRegistry)
                {
                    await asyncRegistry.DisposeAsync();
                }
            },
            gateway,
            promptCatalog,
            registry,
            catalogRuntime,
            new McpGatewayToolSet(gateway)
        );
    }

    private sealed class McpGatewayFactoryServiceProvider(
        IServiceProvider rootServiceProvider,
        IMcpGatewayRegistry registry
    ) : IServiceProvider, IKeyedServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (
                serviceType == typeof(IMcpGatewayCatalogSource)
                || serviceType == typeof(IMcpGatewayRegistry)
                || serviceType == typeof(IMcpGatewayCatalogRuntime)
            )
            {
                return registry;
            }

            return rootServiceProvider.GetService(serviceType);
        }

        public object? GetKeyedService(Type serviceType, object? serviceKey) =>
            rootServiceProvider is IKeyedServiceProvider keyedServiceProvider
                ? keyedServiceProvider.GetKeyedService(serviceType, serviceKey)
                : null;

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
            GetKeyedService(serviceType, serviceKey)
            ?? throw new InvalidOperationException(
                $"No keyed service '{serviceType}' is registered for key '{serviceKey}'."
            );
    }
}

using System.Runtime.ExceptionServices;
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
        var promptChangeHub = new McpGatewayPromptChangeHub();
        var registry = new McpGatewayRegistry(gatewayOptions, promptChangeHub);
        var catalogRuntime = (IMcpGatewayCatalogRuntime)registry;
        var runtimeServiceProvider = new McpGatewayFactoryServiceProvider(
            serviceProvider,
            registry,
            gatewayOptions
        );
        var promptCatalog = new McpGatewayPromptCatalog(
            registry,
            runtimeServiceProvider,
            loggerFactory
        );
        var resourceCatalog = new McpGatewayResourceCatalog(registry, loggerFactory, runtimeServiceProvider);
        var gateway = new McpGateway(
            runtimeServiceProvider,
            gatewayOptions,
            loggerFactory.CreateLogger<McpGateway>(),
            loggerFactory
        );

        return new McpGatewayFactoryResult(
            (gatewayToRelease, registryToRelease, catalogToRelease) => ReleaseAsync(gatewayToRelease, registryToRelease, catalogToRelease, runtimeServiceProvider),
            gateway,
            promptCatalog,
            resourceCatalog,
            registry,
            catalogRuntime,
            new McpGatewayToolSet(gateway),
            new McpGatewayDiscoveryService(gateway, runtimeServiceProvider, options.MaximumToolSchemaBytes),
            new McpGatewayCatalogPublicationService(gateway, runtimeServiceProvider),
            serviceProvider.GetRequiredService<IServiceProviderIsService>().IsService(typeof(IMcpGatewayResultStore))
                ? new McpGatewayResultReader(runtimeServiceProvider, gatewayOptions) : null
        );
    }

    private static async ValueTask ReleaseAsync(
        IMcpGateway runtimeGateway,
        IMcpGatewayRegistry runtimeRegistry,
        IMcpGatewayCatalogRuntime runtimeCatalogRuntime,
        McpGatewayFactoryServiceProvider runtimeServices
    )
    {
        var cleanupExceptions = new List<Exception>();
        await DisposeAsync(runtimeGateway, cleanupExceptions);
        if (runtimeServices.OwnedAdmission is { } ownedAdmission)
        { await DisposeAsync(ownedAdmission, cleanupExceptions); }

        if (
            !ReferenceEquals(runtimeCatalogRuntime, runtimeRegistry)
            && runtimeCatalogRuntime is IAsyncDisposable asyncCatalogRuntime
        )
        {
            await DisposeAsync(asyncCatalogRuntime, cleanupExceptions);
        }

        if (runtimeRegistry is IAsyncDisposable asyncRegistry)
        {
            await DisposeAsync(asyncRegistry, cleanupExceptions);
        }

        ThrowIfCleanupFailed(cleanupExceptions);
    }

    private static async ValueTask DisposeAsync(
        IAsyncDisposable disposable,
        List<Exception> cleanupExceptions
    )
    {
        try
        {
            await disposable.DisposeAsync();
        }
        catch (Exception exception)
        {
            cleanupExceptions.Add(exception);
        }
    }

    private static void ThrowIfCleanupFailed(List<Exception> cleanupExceptions)
    {
        switch (cleanupExceptions.Count)
        {
            case 0:
                return;
            case 1:
                ExceptionDispatchInfo.Capture(cleanupExceptions[0]).Throw();
                break;
            default:
                throw new AggregateException(cleanupExceptions);
        }
    }

    private sealed class McpGatewayFactoryServiceProvider(
        IServiceProvider rootServiceProvider,
        IMcpGatewayRegistry registry,
        IOptions<McpGatewayOptions> instanceOptions
    ) : IServiceProvider, IKeyedServiceProvider, IMcpGatewayAdmissionProvider
    {
        private readonly Lazy<McpGatewaySourceAdmission> _admission = new(() => new(instanceOptions,
            rootServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System));
        public IAsyncDisposable? OwnedAdmission => _admission.IsValueCreated ? _admission.Value : null;
        public IMcpGatewaySourceAdmission Admission
        {
            get
            {
                var configured = McpGatewayTrustedContext.OperationServices(rootServiceProvider).GetRequiredService<IMcpGatewaySourceAdmission>();
                return configured is McpGatewaySourceAdmission ? _admission.Value : configured;
            }
        }
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

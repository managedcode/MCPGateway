using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayFactoryResult(
    Func<IMcpGateway, IMcpGatewayRegistry, IMcpGatewayCatalogRuntime, ValueTask> releaseAsync,
    IMcpGateway gateway,
    IMcpGatewayPromptCatalog promptCatalog,
    IMcpGatewayResourceCatalog resourceCatalog,
    IMcpGatewayRegistry registry,
    IMcpGatewayCatalogRuntime catalogRuntime,
    McpGatewayToolSet toolSet,
    IMcpGatewayDiscovery discovery,
    IMcpGatewayCatalogPublication publication,
    IMcpGatewayResultReader? resultReader
) : IMcpGatewayInstance
{
    public IMcpGateway Gateway { get; } = gateway;

    public IMcpGatewayPromptCatalog PromptCatalog { get; } = promptCatalog;

    public IMcpGatewayResourceCatalog ResourceCatalog { get; } = resourceCatalog;

    public IMcpGatewayRegistry Registry { get; } = registry;

    public IMcpGatewayCatalogRuntime CatalogRuntime { get; } = catalogRuntime;

    public McpGatewayToolSet ToolSet { get; } = toolSet;
    public IMcpGatewayDiscovery Discovery { get; } = discovery;
    public IMcpGatewayCatalogPublication Publication { get; } = publication;
    public IMcpGatewayResultReader? ResultReader { get; } = resultReader;

    public ValueTask DisposeAsync() => releaseAsync(Gateway, Registry, CatalogRuntime);
}

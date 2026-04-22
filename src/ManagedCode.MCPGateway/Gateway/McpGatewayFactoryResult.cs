using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayFactoryResult(
    Func<IMcpGateway, IMcpGatewayRegistry, IMcpGatewayCatalogRuntime, ValueTask> releaseAsync,
    IMcpGateway gateway,
    IMcpGatewayPromptCatalog promptCatalog,
    IMcpGatewayResourceCatalog resourceCatalog,
    IMcpGatewayRegistry registry,
    IMcpGatewayCatalogRuntime catalogRuntime,
    McpGatewayToolSet toolSet
) : IMcpGatewayInstance
{
    public IMcpGateway Gateway { get; } = gateway;

    public IMcpGatewayPromptCatalog PromptCatalog { get; } = promptCatalog;

    public IMcpGatewayResourceCatalog ResourceCatalog { get; } = resourceCatalog;

    public IMcpGatewayRegistry Registry { get; } = registry;

    public IMcpGatewayCatalogRuntime CatalogRuntime { get; } = catalogRuntime;

    public McpGatewayToolSet ToolSet { get; } = toolSet;

    public ValueTask DisposeAsync() => releaseAsync(Gateway, Registry, CatalogRuntime);
}

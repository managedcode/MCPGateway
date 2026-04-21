using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayFactoryResult(
    Func<IMcpGateway, IMcpGatewayRegistry, IMcpGatewayCatalogRuntime, ValueTask> releaseAsync,
    IMcpGateway gateway,
    IMcpGatewayPromptCatalog promptCatalog,
    IMcpGatewayRegistry registry,
    IMcpGatewayCatalogRuntime catalogRuntime,
    McpGatewayToolSet toolSet
) : IMcpGatewayInstance
{
    public IMcpGateway Gateway { get; } = gateway;

    public IMcpGatewayPromptCatalog PromptCatalog { get; } = promptCatalog;

    public IMcpGatewayRegistry Registry { get; } = registry;

    public IMcpGatewayCatalogRuntime CatalogRuntime { get; } = catalogRuntime;

    public McpGatewayToolSet ToolSet { get; } = toolSet;

    public ValueTask DisposeAsync() => releaseAsync(Gateway, Registry, CatalogRuntime);
}

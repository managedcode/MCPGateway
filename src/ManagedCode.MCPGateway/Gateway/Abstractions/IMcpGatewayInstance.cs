namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayInstance : IAsyncDisposable
{
    IMcpGateway Gateway { get; }

    IMcpGatewayPromptCatalog PromptCatalog { get; }

    IMcpGatewayResourceCatalog ResourceCatalog { get; }

    IMcpGatewayRegistry Registry { get; }

    IMcpGatewayCatalogRuntime CatalogRuntime { get; }

    McpGatewayToolSet ToolSet { get; }
    IMcpGatewayDiscovery Discovery => new McpGatewayDiscoveryService(Gateway);
    IMcpGatewayCatalogPublication? Publication => null;
    IMcpGatewayResultReader? ResultReader => null;
}

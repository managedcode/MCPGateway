namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayInstance : IAsyncDisposable
{
    IMcpGateway Gateway { get; }

    IMcpGatewayPromptCatalog PromptCatalog { get; }

    IMcpGatewayRegistry Registry { get; }

    IMcpGatewayCatalogRuntime CatalogRuntime { get; }

    McpGatewayToolSet ToolSet { get; }
}

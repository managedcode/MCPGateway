namespace ManagedCode.MCPGateway;

internal interface IMcpGatewayCatalogSource
{
    McpGatewayCatalogSourceSnapshot CreateSnapshot();
}

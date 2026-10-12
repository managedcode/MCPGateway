namespace ManagedCode.MCPGateway;

public enum McpGatewayMcpServerExportMode
{
    Catalog,
    DiscoveryOnly,
}

public sealed class McpGatewayMcpServerExportOptions
{
    public McpGatewayMcpServerExportMode Mode { get; set; } = McpGatewayMcpServerExportMode.Catalog;
}

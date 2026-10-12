namespace ManagedCode.MCPGateway;

public interface IMcpGatewayDiscovery
{
    Task<McpGatewayDiscoveryResult> DiscoverAsync(McpGatewayDiscoveryRequest request, CancellationToken cancellationToken = default);
    Task<McpGatewayToolSchemaResult?> GetToolSchemaAsync(string toolId, CancellationToken cancellationToken = default);
}

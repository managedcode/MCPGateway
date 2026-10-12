namespace ManagedCode.MCPGateway;

internal interface IMcpGatewayToolSchemaLookup
{
    Task<McpGatewayToolDescriptor?> GetAuthorizedSchemaToolAsync(string toolId, CancellationToken cancellationToken);
}

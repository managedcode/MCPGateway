namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayFactory
{
    IMcpGatewayInstance Create();

    IMcpGatewayInstance Create(Action<McpGatewayOptions> configure);

    IMcpGatewayInstance Create(McpGatewayOptions options);
}

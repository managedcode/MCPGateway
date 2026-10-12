namespace ManagedCode.MCPGateway;

/// <summary>Resolved per operation; singleton gateways must not capture scoped user identity.</summary>
public interface IMcpGatewayExecutionContextProvider
{
    ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default);
}

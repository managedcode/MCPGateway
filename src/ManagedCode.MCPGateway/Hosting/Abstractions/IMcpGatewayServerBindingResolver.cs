namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayServerBindingResolver
{
    ValueTask<IMcpGatewayServerBinding> ResolveAsync(
        IServiceProvider? requestServices,
        IServiceProvider serverServices,
        ModelContextProtocol.Server.McpServer server,
        CancellationToken cancellationToken = default
    );
}

namespace ManagedCode.MCPGateway;

public interface IMcpGatewaySourceAdmission
{
    ValueTask<IMcpGatewayExecutionLease?> TryEnterAsync(McpGatewayExecutionContext context, string sourceId, CancellationToken cancellationToken = default);
}

public interface IMcpGatewayExecutionLease : IAsyncDisposable
{
    void Complete(bool upstreamUnavailable);
}

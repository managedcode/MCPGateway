using System.Net;

namespace ManagedCode.MCPGateway;

public sealed record McpGatewayEgressRequest(string SourceId, Uri Destination)
{
    public IPAddress? ResolvedAddress { get; init; }
}

/// <summary>Explicit outbound destination and resolved-address approval, including intentional private-network exceptions.</summary>
public interface IMcpGatewayEgressPolicy
{
    ValueTask<bool> IsAllowedAsync(McpGatewayExecutionContext context, McpGatewayEgressRequest request,
        CancellationToken cancellationToken = default);
}

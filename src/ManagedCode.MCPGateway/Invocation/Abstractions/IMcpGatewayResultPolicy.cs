using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

/// <summary>Host-owned privacy boundary. Returned content must be safe in every representation.</summary>
public interface IMcpGatewayResultPolicy
{
    ValueTask<McpGatewayApprovedResult> ApproveAsync(McpGatewayExecutionContext context, McpGatewayToolDescriptor tool, JsonElement? output,
        CallToolResult? protocolResult, CancellationToken cancellationToken = default);
}

public sealed record McpGatewayApprovedResult(JsonElement? Output, CallToolResult? ProtocolResult = null)
{
    public bool IsRejected { get; init; }
}

using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

public sealed record McpGatewayInvokeResult(
    bool IsSuccess,
    string ToolId,
    string SourceId,
    string ToolName,
    object? Output,
    string? Error = null
)
{
    /// <summary>Business outcome, independent of successful protocol completion.</summary>
    public McpGatewayInvocationOutcome Outcome { get; init; } = IsSuccess
        ? McpGatewayInvocationOutcome.Succeeded : McpGatewayInvocationOutcome.UpstreamUnavailable;
    public string? ErrorCode { get; init; }
    public string? DefinitionHash { get; init; }
    public int? DeliveredBytes { get; init; }
    public string? ResultHandle { get; init; }
    /// <summary>The approved outward MCP result, when the invoked source is MCP.</summary>
    [JsonIgnore]
    public CallToolResult? McpResult { get; init; }
}

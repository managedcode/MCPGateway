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
    /// <summary>The complete upstream MCP result, when the invoked source is MCP.</summary>
    [JsonIgnore]
    public CallToolResult? McpResult { get; init; }
}

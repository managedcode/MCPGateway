using System.Text.Json;

namespace ManagedCode.MCPGateway;

public sealed record McpGatewayInvokeRequest(
    string? ToolId = null,
    string? ToolName = null,
    string? SourceId = null,
    IReadOnlyDictionary<string, JsonElement>? Arguments = null,
    string? Query = null,
    IReadOnlyDictionary<string, JsonElement>? Context = null,
    string? ContextSummary = null
)
{
    public McpGatewayResponseOptions? ResponseOptions { get; init; }
}

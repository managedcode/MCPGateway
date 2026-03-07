namespace ManagedCode.MCPGateway;

public sealed record McpGatewayInvokeRequest(
    string? ToolId = null,
    string? ToolName = null,
    string? SourceId = null,
    IReadOnlyDictionary<string, object?>? Arguments = null,
    string? Query = null,
    IReadOnlyDictionary<string, object?>? Context = null,
    string? ContextSummary = null);

namespace ManagedCode.MCPGateway;

public sealed record McpGatewaySearchRequest(
    string? Query = null,
    int? MaxResults = null,
    IReadOnlyDictionary<string, object?>? Context = null,
    string? ContextSummary = null);

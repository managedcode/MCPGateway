using System.Text.Json;

namespace ManagedCode.MCPGateway;

public sealed record McpGatewaySearchRequest(
    string? Query = null,
    int? MaxResults = null,
    IReadOnlyDictionary<string, JsonElement>? Context = null,
    string? ContextSummary = null,
    bool IncludeDisabledTools = false
)
{
    public string? SourceId { get; init; }
    public string? Category { get; init; }
}

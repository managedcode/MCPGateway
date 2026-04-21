namespace ManagedCode.MCPGateway;

public sealed record McpGatewayToolRouteRequest(
    string? Query = null,
    int? MaxCategories = null,
    int? MaxToolsPerCategory = null,
    IReadOnlyDictionary<string, object?>? Context = null,
    string? ContextSummary = null,
    bool? PreferReadOnly = null,
    bool IncludeDisabledTools = false
);

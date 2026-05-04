namespace ManagedCode.MCPGateway;

public sealed record McpGatewaySearchResult(
    IReadOnlyList<McpGatewaySearchMatch> Matches,
    IReadOnlyList<McpGatewayDiagnostic> Diagnostics,
    string RankingMode
)
{
    public IReadOnlyList<McpGatewaySearchMatch> RelatedMatches { get; init; } = [];

    public IReadOnlyList<McpGatewaySearchMatch> NextStepMatches { get; init; } = [];

    public int FocusedGraphNodeCount { get; init; }

    public int FocusedGraphEdgeCount { get; init; }

    public bool UsedSchemaSearch { get; init; }

    public bool UsedSchemaFallback { get; init; }
}

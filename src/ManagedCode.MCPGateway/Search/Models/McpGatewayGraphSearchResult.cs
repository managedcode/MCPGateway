namespace ManagedCode.MCPGateway;

public sealed record McpGatewayGraphSearchResult(
    IReadOnlyList<McpGatewayGraphSearchMatch> Matches,
    IReadOnlyList<McpGatewayGraphSearchMatch> RelatedMatches,
    IReadOnlyList<McpGatewayGraphSearchMatch> NextStepMatches,
    IReadOnlyList<McpGatewayDiagnostic> Diagnostics,
    bool IsFederated
)
{
    public string? GeneratedSparql { get; init; }

    public string? GeneratedExpansionSparql { get; init; }

    public IReadOnlyList<string> ServiceEndpointSpecifiers { get; init; } = [];

    public int FocusedGraphNodeCount { get; init; }

    public int FocusedGraphEdgeCount { get; init; }
}

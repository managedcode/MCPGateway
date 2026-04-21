namespace ManagedCode.MCPGateway;

public sealed record McpGatewayToolRouteResult(
    IReadOnlyList<McpGatewayToolRouteCategory> Categories,
    IReadOnlyList<McpGatewaySearchMatch> SuggestedMatches,
    IReadOnlyList<McpGatewayDiagnostic> Diagnostics,
    string RankingMode
);

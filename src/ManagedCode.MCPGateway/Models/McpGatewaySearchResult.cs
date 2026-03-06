namespace ManagedCode.MCPGateway;

public sealed record McpGatewaySearchResult(
    IReadOnlyList<McpGatewaySearchMatch> Matches,
    IReadOnlyList<McpGatewayDiagnostic> Diagnostics,
    string RankingMode);

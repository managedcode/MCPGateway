namespace ManagedCode.MCPGateway;

public sealed record McpGatewayToolRouteCategory(
    string Category,
    double Score,
    IReadOnlyList<McpGatewaySearchMatch> Tools
);

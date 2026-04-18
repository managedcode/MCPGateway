namespace ManagedCode.MCPGateway;

public sealed record McpGatewaySearchCachedResult(
    McpGatewaySearchResult Result,
    bool QueryNormalized);

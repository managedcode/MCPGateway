namespace ManagedCode.MCPGateway;

public sealed record McpGatewayGraphSearchRequest(string Query)
{
    public int? MaxResults { get; init; }

    public bool UseFederation { get; init; }

    public bool IncludeLocalGatewayGraph { get; init; } = true;

    public IReadOnlyList<string> ServiceEndpoints { get; init; } = [];
}

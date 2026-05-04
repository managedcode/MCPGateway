namespace ManagedCode.MCPGateway;

public sealed record McpGatewayGraphSearchMatch(
    string NodeId,
    string? Label,
    string Role,
    double Score,
    IReadOnlyList<string> Types,
    IReadOnlyList<McpGatewayGraphSearchEvidence> Evidence
)
{
    public string? Description { get; init; }

    public string? SourceNodeId { get; init; }

    public string? ViaPredicateId { get; init; }

    public McpGatewaySearchMatch? ToolMatch { get; init; }
}

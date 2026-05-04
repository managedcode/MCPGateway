namespace ManagedCode.MCPGateway;

public sealed record McpGatewayGraphSearchEvidence(
    string? PredicateId,
    string? MatchedText,
    string Kind,
    double Score
)
{
    public string? RelatedNodeId { get; init; }

    public string? RelatedNodeLabel { get; init; }

    public string? ViaPredicateId { get; init; }

    public string? ServiceEndpoint { get; init; }

    public IReadOnlyList<McpGatewayGraphSearchSourceContext> SourceContexts { get; init; } = [];
}

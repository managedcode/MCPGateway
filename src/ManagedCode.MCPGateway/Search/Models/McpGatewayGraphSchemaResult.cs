namespace ManagedCode.MCPGateway;

public sealed record McpGatewayGraphSchemaResult(
    McpGatewaySearchStrategy SearchStrategy,
    McpGatewayMarkdownLdGraphSearchMode GraphSearchMode,
    McpGatewayMarkdownLdGraphSource GraphSource,
    bool IsGraphAvailable,
    bool CanSearchByTokenDistance,
    int GraphNodeCount,
    int GraphEdgeCount,
    int DefaultSearchLimit,
    int MaxSearchResults,
    IReadOnlyDictionary<string, string> Prefixes,
    IReadOnlyList<McpGatewayGraphSchemaTextPredicate> TextPredicates,
    IReadOnlyList<McpGatewayGraphSchemaRelationshipPredicate> RelationshipPredicates,
    IReadOnlyList<McpGatewayGraphSchemaExpansionPredicate> ExpansionPredicates,
    IReadOnlyList<string> TypeFilters,
    string TermMode,
    int MaxPrimaryResults,
    int MaxRelatedResults,
    int MaxNextStepResults,
    IReadOnlyList<string> ConfiguredFederatedServiceEndpoints,
    IReadOnlyList<McpGatewayDiagnostic> Diagnostics
)
{
    public IReadOnlyList<string> ExcludedTypes { get; init; } = [];

    public IReadOnlyList<McpGatewayGraphSchemaFacetFilter> FacetFilters { get; init; } = [];

    /// <summary>Configured absolute primary subject IRIs. Null applies no caller pool; the default tool profile may still select candidates. Empty matches none. Expansion is not restricted.</summary>
    public IReadOnlyList<string>? CandidateNodeIds { get; init; }
}

public sealed record McpGatewayGraphSchemaTextPredicate(string PredicateId, double Weight);

public sealed record McpGatewayGraphSchemaRelationshipPredicate(
    string PredicateId,
    IReadOnlyList<string> EvidencePredicateIds,
    double Weight,
    string Direction
)
{
    public IReadOnlyList<string> PredicatePath { get; init; } = [];
}

public sealed record McpGatewayGraphSchemaFacetFilter(string PredicateId, string ObjectId);

public sealed record McpGatewayGraphSchemaExpansionPredicate(
    string PredicateId,
    string Role,
    double Weight
);

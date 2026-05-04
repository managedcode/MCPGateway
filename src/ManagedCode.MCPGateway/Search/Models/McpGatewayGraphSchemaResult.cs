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
);

public sealed record McpGatewayGraphSchemaTextPredicate(string PredicateId, double Weight);

public sealed record McpGatewayGraphSchemaRelationshipPredicate(
    string PredicateId,
    IReadOnlyList<string> EvidencePredicateIds,
    double Weight,
    string Direction
);

public sealed record McpGatewayGraphSchemaExpansionPredicate(
    string PredicateId,
    string Role,
    double Weight
);

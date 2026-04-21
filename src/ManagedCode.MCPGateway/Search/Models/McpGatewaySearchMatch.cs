namespace ManagedCode.MCPGateway;

public sealed record McpGatewaySearchMatch(
    string ToolId,
    string SourceId,
    McpGatewaySourceKind SourceKind,
    string ToolName,
    string? DisplayName,
    string Description,
    IReadOnlyList<string> RequiredArguments,
    string? InputSchemaJson,
    double Score
)
{
    public IReadOnlyList<string> SearchAliases { get; init; } = [];

    public IReadOnlyList<string> SearchKeywords { get; init; } = [];

    public IReadOnlyList<string> Categories { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<string> DataSources { get; init; } = [];

    public IReadOnlyList<McpGatewayToolExample> UsageExamples { get; init; } = [];

    public bool? IsReadOnly { get; init; }

    public bool? IsIdempotent { get; init; }

    public bool? IsDestructive { get; init; }

    public bool? IsOpenWorld { get; init; }

    public McpGatewayToolCostTier? CostTier { get; init; }

    public McpGatewayToolLatencyTier? LatencyTier { get; init; }

    public bool IsEnabledByDefault { get; init; } = true;
}

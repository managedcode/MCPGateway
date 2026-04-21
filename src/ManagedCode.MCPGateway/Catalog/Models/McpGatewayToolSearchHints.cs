namespace ManagedCode.MCPGateway;

public sealed record McpGatewayToolSearchHints(
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? Keywords = null,
    IReadOnlyList<string>? Categories = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? DataSources = null,
    IReadOnlyList<McpGatewayToolExample>? UsageExamples = null,
    bool? ReadOnly = null,
    bool? Idempotent = null,
    bool? Destructive = null,
    bool? OpenWorld = null,
    McpGatewayToolCostTier? CostTier = null,
    McpGatewayToolLatencyTier? LatencyTier = null,
    bool? EnabledByDefault = null
)
{
    public static McpGatewayToolSearchHints Empty { get; } =
        new([], [], [], [], [], [], null, null, null, null, null, null, null);
}

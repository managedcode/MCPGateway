namespace ManagedCode.MCPGateway;

public sealed record McpGatewayIndexBuildResult(
    int ToolCount,
    int VectorizedToolCount,
    bool IsVectorSearchEnabled,
    IReadOnlyList<McpGatewayDiagnostic> Diagnostics
)
{
    public bool IsGraphSearchEnabled { get; init; }

    /// <summary>
    /// True when this build reused a generated tool graph that another gateway instance in the process built, or
    /// was building, for an identical catalog instead of constructing the graph again.
    /// </summary>
    public bool IsGraphIndexReused { get; init; }

    public int GraphNodeCount { get; init; }

    public int GraphEdgeCount { get; init; }
}

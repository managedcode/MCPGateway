namespace ManagedCode.MCPGateway;

public sealed record McpGatewayIndexBuildResult(
    int ToolCount,
    int VectorizedToolCount,
    bool IsVectorSearchEnabled,
    IReadOnlyList<McpGatewayDiagnostic> Diagnostics)
{
    public bool IsGraphSearchEnabled { get; init; }

    public int GraphNodeCount { get; init; }

    public int GraphEdgeCount { get; init; }
}

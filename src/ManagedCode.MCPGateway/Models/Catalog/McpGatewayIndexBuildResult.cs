namespace ManagedCode.MCPGateway;

public sealed record McpGatewayIndexBuildResult(
    int ToolCount,
    int VectorizedToolCount,
    bool IsVectorSearchEnabled,
    IReadOnlyList<McpGatewayDiagnostic> Diagnostics);

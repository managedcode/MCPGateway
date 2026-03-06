namespace ManagedCode.MCPGateway;

public sealed record McpGatewayInvokeResult(
    bool IsSuccess,
    string ToolId,
    string SourceId,
    string ToolName,
    object? Output,
    string? Error = null);

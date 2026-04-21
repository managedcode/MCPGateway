namespace ManagedCode.MCPGateway;

public sealed record McpGatewayPromptResult(
    string PromptId,
    string SourceId,
    McpGatewaySourceKind SourceKind,
    string PromptName,
    string Description,
    IReadOnlyList<McpGatewayPromptMessage> Messages
);

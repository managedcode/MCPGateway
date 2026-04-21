namespace ManagedCode.MCPGateway;

public sealed record McpGatewayPromptRequest(
    string SourceId,
    string PromptName,
    IReadOnlyDictionary<string, object?>? Arguments = null
);

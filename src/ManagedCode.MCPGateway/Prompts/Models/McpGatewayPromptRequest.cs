using System.Text.Json;

namespace ManagedCode.MCPGateway;

public sealed record McpGatewayPromptRequest(
    string SourceId,
    string PromptName,
    IReadOnlyDictionary<string, JsonElement>? Arguments = null
);

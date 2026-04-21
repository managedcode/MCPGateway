using System.Text.Json.Nodes;

namespace ManagedCode.MCPGateway;

public sealed record McpGatewayPromptMessage(string Role, JsonNode? Content, string? Text = null);

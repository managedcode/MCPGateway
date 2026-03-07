namespace ManagedCode.MCPGateway;

public sealed record McpGatewayToolEmbeddingLookup(
    string ToolId,
    string DocumentHash,
    string? EmbeddingGeneratorFingerprint = null);

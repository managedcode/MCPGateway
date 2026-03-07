namespace ManagedCode.MCPGateway;

public sealed record McpGatewayToolEmbedding(
    string ToolId,
    string SourceId,
    string ToolName,
    string DocumentHash,
    string? EmbeddingGeneratorFingerprint,
    float[] Vector);

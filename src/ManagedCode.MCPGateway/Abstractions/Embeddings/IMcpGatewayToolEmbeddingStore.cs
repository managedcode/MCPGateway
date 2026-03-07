namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayToolEmbeddingStore
{
    Task<IReadOnlyList<McpGatewayToolEmbedding>> GetAsync(
        IReadOnlyList<McpGatewayToolEmbeddingLookup> lookups,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        IReadOnlyList<McpGatewayToolEmbedding> embeddings,
        CancellationToken cancellationToken = default);
}

using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayInMemoryToolEmbeddingStore : IMcpGatewayToolEmbeddingStore
{
    private readonly McpGatewayToolEmbeddingStoreIndex _index = new();

    public Task<IReadOnlyList<McpGatewayToolEmbedding>> GetAsync(
        IReadOnlyList<McpGatewayToolEmbeddingLookup> lookups,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_index.Get(lookups, cancellationToken));

    public Task UpsertAsync(
        IReadOnlyList<McpGatewayToolEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        _index.Upsert(embeddings, cancellationToken);
        return Task.CompletedTask;
    }
}

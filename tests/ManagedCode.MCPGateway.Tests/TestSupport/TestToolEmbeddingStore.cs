using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class TestToolEmbeddingStore : IMcpGatewayToolEmbeddingStore
{
    private readonly McpGatewayToolEmbeddingStoreIndex _index = new();

    public List<IReadOnlyList<McpGatewayToolEmbeddingLookup>> GetCalls { get; } = [];

    public List<IReadOnlyList<McpGatewayToolEmbedding>> UpsertCalls { get; } = [];

    public Task<IReadOnlyList<McpGatewayToolEmbedding>> GetAsync(
        IReadOnlyList<McpGatewayToolEmbeddingLookup> lookups,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GetCalls.Add(lookups.ToList());
        return Task.FromResult(_index.Get(lookups, cancellationToken));
    }

    public Task UpsertAsync(
        IReadOnlyList<McpGatewayToolEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        var clonedBatch = _index.Upsert(embeddings, cancellationToken);
        UpsertCalls.Add(clonedBatch);

        return Task.CompletedTask;
    }

    public void Remove(string toolId) => _index.Remove(toolId);
}

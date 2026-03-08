using System.Collections.Concurrent;
using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class TestToolEmbeddingStore : IMcpGatewayToolEmbeddingStore
{
    private readonly ConcurrentDictionary<StoreKey, McpGatewayToolEmbedding> _embeddings = new();

    public List<IReadOnlyList<McpGatewayToolEmbeddingLookup>> GetCalls { get; } = [];

    public List<IReadOnlyList<McpGatewayToolEmbedding>> UpsertCalls { get; } = [];

    public Task<IReadOnlyList<McpGatewayToolEmbedding>> GetAsync(
        IReadOnlyList<McpGatewayToolEmbeddingLookup> lookups,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GetCalls.Add(lookups.ToList());
        var results = new List<McpGatewayToolEmbedding>(lookups.Count);
        foreach (var lookup in lookups)
        {
            if (TryGetEmbedding(lookup, out var embedding))
            {
                results.Add(Clone(embedding));
            }
        }

        return Task.FromResult<IReadOnlyList<McpGatewayToolEmbedding>>(results);
    }

    public Task UpsertAsync(
        IReadOnlyList<McpGatewayToolEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var clonedBatch = embeddings
            .Select(Clone)
            .ToList();

        foreach (var embedding in clonedBatch)
        {
            _embeddings[StoreKey.FromEmbedding(embedding)] = embedding;
        }

        UpsertCalls.Add(clonedBatch);

        return Task.CompletedTask;
    }

    public void Remove(string toolId)
    {
        var normalizedToolId = NormalizeToolId(toolId);
        var keys = _embeddings.Keys
            .Where(key => string.Equals(key.NormalizedToolId, normalizedToolId, StringComparison.Ordinal))
            .ToList();

        foreach (var key in keys)
        {
            _embeddings.TryRemove(key, out _);
        }
    }

    private bool TryGetEmbedding(
        McpGatewayToolEmbeddingLookup lookup,
        out McpGatewayToolEmbedding embedding)
    {
        var storeKey = StoreKey.FromLookup(lookup);
        if (lookup.EmbeddingGeneratorFingerprint is not null)
        {
            return _embeddings.TryGetValue(storeKey, out embedding!);
        }

        foreach (var pair in _embeddings)
        {
            if (pair.Key.Matches(storeKey))
            {
                embedding = pair.Value;
                return true;
            }
        }

        embedding = default!;
        return false;
    }

    private static McpGatewayToolEmbedding Clone(McpGatewayToolEmbedding embedding)
        => embedding with
        {
            Vector = [.. embedding.Vector]
        };

    private static string NormalizeToolId(string toolId) => toolId.ToUpperInvariant();

    private readonly record struct StoreKey(
        string NormalizedToolId,
        string DocumentHash,
        string? EmbeddingGeneratorFingerprint)
    {
        public static StoreKey FromLookup(McpGatewayToolEmbeddingLookup lookup)
            => new(
                NormalizeToolId(lookup.ToolId),
                lookup.DocumentHash,
                lookup.EmbeddingGeneratorFingerprint);

        public static StoreKey FromEmbedding(McpGatewayToolEmbedding embedding)
            => new(
                NormalizeToolId(embedding.ToolId),
                embedding.DocumentHash,
                embedding.EmbeddingGeneratorFingerprint);

        public bool Matches(StoreKey other)
            => string.Equals(NormalizedToolId, other.NormalizedToolId, StringComparison.Ordinal)
                && string.Equals(DocumentHash, other.DocumentHash, StringComparison.Ordinal)
                && (other.EmbeddingGeneratorFingerprint is null
                    || string.Equals(
                        EmbeddingGeneratorFingerprint,
                        other.EmbeddingGeneratorFingerprint,
                        StringComparison.Ordinal));
    }
}

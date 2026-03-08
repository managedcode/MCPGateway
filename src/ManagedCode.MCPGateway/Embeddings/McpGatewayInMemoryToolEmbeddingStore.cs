using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayInMemoryToolEmbeddingStore : IMcpGatewayToolEmbeddingStore, IDisposable
{
    private const long CacheEntrySize = 1;
    private readonly IMemoryCache _cache;
    private readonly IDisposable? _ownedCache;

    public McpGatewayInMemoryToolEmbeddingStore()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        _cache = cache;
        _ownedCache = cache;
    }

    public McpGatewayInMemoryToolEmbeddingStore(IMemoryCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public Task<IReadOnlyList<McpGatewayToolEmbedding>> GetAsync(
        IReadOnlyList<McpGatewayToolEmbeddingLookup> lookups,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        foreach (var embedding in embeddings)
        {
            var clonedEmbedding = Clone(embedding);
            SetCacheEntry(ExactCacheKey.FromEmbedding(clonedEmbedding), clonedEmbedding);
            SetCacheEntry(FallbackCacheKey.FromEmbedding(clonedEmbedding), clonedEmbedding);
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _ownedCache?.Dispose();

    private bool TryGetEmbedding(
        McpGatewayToolEmbeddingLookup lookup,
        out McpGatewayToolEmbedding embedding)
    {
        if (lookup.EmbeddingGeneratorFingerprint is not null)
        {
            return _cache.TryGetValue(ExactCacheKey.FromLookup(lookup), out embedding!);
        }

        return _cache.TryGetValue(FallbackCacheKey.FromLookup(lookup), out embedding!);
    }

    private static McpGatewayToolEmbedding Clone(McpGatewayToolEmbedding embedding)
        => embedding with
        {
            Vector = [.. embedding.Vector]
        };

    private static string NormalizeToolId(string toolId) => toolId.ToUpperInvariant();

    private void SetCacheEntry(object key, McpGatewayToolEmbedding embedding)
    {
        using var entry = _cache.CreateEntry(key);
        entry.Size = CacheEntrySize;
        entry.Value = embedding;
    }

    private readonly record struct ExactCacheKey(
        string NormalizedToolId,
        string DocumentHash,
        string? EmbeddingGeneratorFingerprint)
    {
        public static ExactCacheKey FromLookup(McpGatewayToolEmbeddingLookup lookup)
            => new(
                NormalizeToolId(lookup.ToolId),
                lookup.DocumentHash,
                lookup.EmbeddingGeneratorFingerprint);

        public static ExactCacheKey FromEmbedding(McpGatewayToolEmbedding embedding)
            => new(
                NormalizeToolId(embedding.ToolId),
                embedding.DocumentHash,
                embedding.EmbeddingGeneratorFingerprint);
    }

    private readonly record struct FallbackCacheKey(
        string NormalizedToolId,
        string DocumentHash)
    {
        public static FallbackCacheKey FromLookup(McpGatewayToolEmbeddingLookup lookup)
            => new(
                NormalizeToolId(lookup.ToolId),
                lookup.DocumentHash);

        public static FallbackCacheKey FromEmbedding(McpGatewayToolEmbedding embedding)
            => new(
                NormalizeToolId(embedding.ToolId),
                embedding.DocumentHash);
    }
}

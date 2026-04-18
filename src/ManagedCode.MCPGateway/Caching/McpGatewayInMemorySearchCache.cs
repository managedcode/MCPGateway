using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayInMemorySearchCache : IMcpGatewaySearchCache, IDisposable
{
    private static readonly TimeSpan NormalizedQueryCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan QueryEmbeddingCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SearchResultCacheTtl = TimeSpan.FromMinutes(5);
    private readonly IMemoryCache _cache;
    private readonly IDisposable? _ownedCache;

    public McpGatewayInMemorySearchCache()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        _cache = cache;
        _ownedCache = cache;
    }

    public McpGatewayInMemorySearchCache(IMemoryCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public ValueTask<(bool found, string? normalizedQuery)> TryGetNormalizedQueryAsync(
        McpGatewaySearchQueryNormalization normalization,
        string query,
        string? chatClientFingerprint,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (
            _cache.TryGetValue(
                new NormalizedQueryCacheKey(normalization, query, chatClientFingerprint),
                out CachedNormalizedQuery? entry
            )
        )
        {
            return ValueTask.FromResult((true, entry!.NormalizedQuery));
        }

        return ValueTask.FromResult<(bool found, string? normalizedQuery)>((false, null));
    }

    public ValueTask SetNormalizedQueryAsync(
        McpGatewaySearchQueryNormalization normalization,
        string query,
        string? chatClientFingerprint,
        string? normalizedQuery,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        _cache.Set(
            new NormalizedQueryCacheKey(normalization, query, chatClientFingerprint),
            new CachedNormalizedQuery(normalizedQuery),
            CreateEntryOptions(NormalizedQueryCacheTtl)
        );
        return ValueTask.CompletedTask;
    }

    public ValueTask<(bool found, McpGatewayQueryEmbedding? embedding)> TryGetQueryEmbeddingAsync(
        string query,
        string? embeddingGeneratorFingerprint,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (
            _cache.TryGetValue(
                new QueryEmbeddingCacheKey(query, embeddingGeneratorFingerprint),
                out McpGatewayQueryEmbedding? entry
            )
        )
        {
            return ValueTask.FromResult<(bool found, McpGatewayQueryEmbedding? embedding)>(
                (true, CloneQueryEmbedding(entry!))
            );
        }

        return ValueTask.FromResult<(bool found, McpGatewayQueryEmbedding? embedding)>(
            (false, null)
        );
    }

    public ValueTask SetQueryEmbeddingAsync(
        string query,
        string? embeddingGeneratorFingerprint,
        McpGatewayQueryEmbedding embedding,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(embedding);

        _cache.Set(
            new QueryEmbeddingCacheKey(query, embeddingGeneratorFingerprint),
            CloneQueryEmbedding(embedding),
            CreateEntryOptions(QueryEmbeddingCacheTtl)
        );
        return ValueTask.CompletedTask;
    }

    public ValueTask<(bool found, McpGatewaySearchCachedResult? result)> TryGetSearchResultAsync(
        int snapshotVersion,
        McpGatewaySearchStrategy strategy,
        McpGatewaySearchQueryNormalization normalization,
        string? query,
        string? contextSummary,
        string? flattenedContext,
        int limit,
        string? chatClientFingerprint,
        string? embeddingGeneratorFingerprint,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (
            _cache.TryGetValue(
                new SearchResultCacheKey(
                    snapshotVersion,
                    strategy,
                    normalization,
                    query,
                    contextSummary,
                    flattenedContext,
                    limit,
                    chatClientFingerprint,
                    embeddingGeneratorFingerprint
                ),
                out McpGatewaySearchCachedResult? entry
            )
        )
        {
            return ValueTask.FromResult<(bool found, McpGatewaySearchCachedResult? result)>(
                (true, CloneSearchResult(entry!))
            );
        }

        return ValueTask.FromResult<(bool found, McpGatewaySearchCachedResult? result)>(
            (false, null)
        );
    }

    public ValueTask SetSearchResultAsync(
        int snapshotVersion,
        McpGatewaySearchStrategy strategy,
        McpGatewaySearchQueryNormalization normalization,
        string? query,
        string? contextSummary,
        string? flattenedContext,
        int limit,
        string? chatClientFingerprint,
        string? embeddingGeneratorFingerprint,
        McpGatewaySearchCachedResult result,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(result);

        _cache.Set(
            new SearchResultCacheKey(
                snapshotVersion,
                strategy,
                normalization,
                query,
                contextSummary,
                flattenedContext,
                limit,
                chatClientFingerprint,
                embeddingGeneratorFingerprint
            ),
            CloneSearchResult(result),
            CreateEntryOptions(SearchResultCacheTtl)
        );
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _ownedCache?.Dispose();

    private static MemoryCacheEntryOptions CreateEntryOptions(TimeSpan slidingExpiration) =>
        new() { SlidingExpiration = slidingExpiration };

    private static McpGatewayQueryEmbedding CloneQueryEmbedding(
        McpGatewayQueryEmbedding embedding
    ) => embedding with { Vector = [.. embedding.Vector] };

    private static McpGatewaySearchCachedResult CloneSearchResult(
        McpGatewaySearchCachedResult result
    ) =>
        result with
        {
            Result = result.Result with
            {
                Matches = [.. result.Result.Matches],
                Diagnostics = [.. result.Result.Diagnostics],
                RelatedMatches = [.. result.Result.RelatedMatches],
                NextStepMatches = [.. result.Result.NextStepMatches],
            },
        };

    private sealed record CachedNormalizedQuery(string? NormalizedQuery);

    private readonly record struct NormalizedQueryCacheKey(
        McpGatewaySearchQueryNormalization Normalization,
        string Query,
        string? ChatClientFingerprint
    );

    private readonly record struct QueryEmbeddingCacheKey(
        string Query,
        string? EmbeddingGeneratorFingerprint
    );

    private readonly record struct SearchResultCacheKey(
        int SnapshotVersion,
        McpGatewaySearchStrategy Strategy,
        McpGatewaySearchQueryNormalization Normalization,
        string? Query,
        string? ContextSummary,
        string? FlattenedContext,
        int Limit,
        string? ChatClientFingerprint,
        string? EmbeddingGeneratorFingerprint
    );
}

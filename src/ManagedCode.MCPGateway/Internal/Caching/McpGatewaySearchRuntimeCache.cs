using Microsoft.Extensions.Caching.Memory;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewaySearchRuntimeCache(IMemoryCache cache)
{
    private static readonly TimeSpan NormalizedQueryCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan QueryEmbeddingCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SearchResultCacheTtl = TimeSpan.FromMinutes(5);
    private readonly IMemoryCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    public bool TryGetNormalizedQuery(
        McpGatewaySearchQueryNormalization normalization,
        string query,
        out string? normalizedQuery)
    {
        if (_cache.TryGetValue(new NormalizedQueryCacheKey(normalization, query), out CachedNormalizedQuery? entry))
        {
            normalizedQuery = entry!.NormalizedQuery;
            return true;
        }

        normalizedQuery = null;
        return false;
    }

    public void SetNormalizedQuery(
        McpGatewaySearchQueryNormalization normalization,
        string query,
        string? normalizedQuery)
        => _cache.Set(
            new NormalizedQueryCacheKey(normalization, query),
            new CachedNormalizedQuery(normalizedQuery),
            CreateEntryOptions(NormalizedQueryCacheTtl));

    public bool TryGetQueryEmbedding(
        string query,
        string? embeddingGeneratorFingerprint,
        out CachedQueryEmbedding embedding)
    {
        if (_cache.TryGetValue(new QueryEmbeddingCacheKey(query, embeddingGeneratorFingerprint), out CachedQueryEmbedding? entry))
        {
            embedding = entry!;
            return true;
        }

        embedding = default!;
        return false;
    }

    public void SetQueryEmbedding(
        string query,
        string? embeddingGeneratorFingerprint,
        float[] vector,
        double magnitude)
        => _cache.Set(
            new QueryEmbeddingCacheKey(query, embeddingGeneratorFingerprint),
            new CachedQueryEmbedding([.. vector], magnitude),
            CreateEntryOptions(QueryEmbeddingCacheTtl));

    public bool TryGetSearchResult(
        int snapshotVersion,
        McpGatewaySearchStrategy strategy,
        string? query,
        string? contextSummary,
        string? flattenedContext,
        int limit,
        out CachedSearchResult result)
    {
        if (_cache.TryGetValue(
                new SearchResultCacheKey(
                    snapshotVersion,
                    strategy,
                    query,
                    contextSummary,
                    flattenedContext,
                    limit),
                out CachedSearchResult? entry))
        {
            result = CloneCachedSearchResult(entry!);
            return true;
        }

        result = default!;
        return false;
    }

    public void SetSearchResult(
        int snapshotVersion,
        McpGatewaySearchStrategy strategy,
        string? query,
        string? contextSummary,
        string? flattenedContext,
        int limit,
        McpGatewaySearchResult result,
        RankedSearchMetrics? metrics,
        bool queryNormalized)
        => _cache.Set(
            new SearchResultCacheKey(
                snapshotVersion,
                strategy,
                query,
                contextSummary,
                flattenedContext,
                limit),
            new CachedSearchResult(CloneSearchResult(result), metrics, queryNormalized),
            CreateEntryOptions(SearchResultCacheTtl));

    private static MemoryCacheEntryOptions CreateEntryOptions(TimeSpan slidingExpiration)
        => new()
        {
            SlidingExpiration = slidingExpiration
        };

    private static McpGatewaySearchResult CloneSearchResult(McpGatewaySearchResult result)
        => result with
        {
            Matches = [.. result.Matches],
            Diagnostics = [.. result.Diagnostics],
            RelatedMatches = [.. result.RelatedMatches],
            NextStepMatches = [.. result.NextStepMatches]
        };

    private static CachedSearchResult CloneCachedSearchResult(CachedSearchResult result)
        => result with
        {
            Result = CloneSearchResult(result.Result)
        };

    internal sealed record CachedQueryEmbedding(float[] Vector, double Magnitude);

    internal sealed record CachedSearchResult(
        McpGatewaySearchResult Result,
        RankedSearchMetrics? Metrics,
        bool QueryNormalized);

    private sealed record CachedNormalizedQuery(string? NormalizedQuery);

    private readonly record struct NormalizedQueryCacheKey(
        McpGatewaySearchQueryNormalization Normalization,
        string Query);

    private readonly record struct QueryEmbeddingCacheKey(
        string Query,
        string? EmbeddingGeneratorFingerprint);

    private readonly record struct SearchResultCacheKey(
        int SnapshotVersion,
        McpGatewaySearchStrategy Strategy,
        string? Query,
        string? ContextSummary,
        string? FlattenedContext,
        int Limit);
}

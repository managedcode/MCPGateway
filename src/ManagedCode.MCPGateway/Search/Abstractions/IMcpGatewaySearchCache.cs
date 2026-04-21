namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewaySearchCache
{
    ValueTask<(bool found, string? normalizedQuery)> TryGetNormalizedQueryAsync(
        McpGatewaySearchQueryNormalization normalization,
        string query,
        string? chatClientFingerprint,
        CancellationToken cancellationToken = default
    );

    ValueTask SetNormalizedQueryAsync(
        McpGatewaySearchQueryNormalization normalization,
        string query,
        string? chatClientFingerprint,
        string? normalizedQuery,
        CancellationToken cancellationToken = default
    );

    ValueTask<(bool found, McpGatewayQueryEmbedding? embedding)> TryGetQueryEmbeddingAsync(
        string query,
        string? embeddingGeneratorFingerprint,
        CancellationToken cancellationToken = default
    );

    ValueTask SetQueryEmbeddingAsync(
        string query,
        string? embeddingGeneratorFingerprint,
        McpGatewayQueryEmbedding embedding,
        CancellationToken cancellationToken = default
    );

    ValueTask<(bool found, McpGatewaySearchCachedResult? result)> TryGetSearchResultAsync(
        int snapshotVersion,
        McpGatewaySearchStrategy strategy,
        McpGatewaySearchQueryNormalization normalization,
        string? query,
        string? contextSummary,
        string? flattenedContext,
        bool includeDisabledTools,
        int limit,
        string? chatClientFingerprint,
        string? embeddingGeneratorFingerprint,
        CancellationToken cancellationToken = default
    );

    ValueTask SetSearchResultAsync(
        int snapshotVersion,
        McpGatewaySearchStrategy strategy,
        McpGatewaySearchQueryNormalization normalization,
        string? query,
        string? contextSummary,
        string? flattenedContext,
        bool includeDisabledTools,
        int limit,
        string? chatClientFingerprint,
        string? embeddingGeneratorFingerprint,
        McpGatewaySearchCachedResult result,
        CancellationToken cancellationToken = default
    );
}

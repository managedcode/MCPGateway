using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayNoOpSearchCache : IMcpGatewaySearchCache
{
    public ValueTask<(bool found, string? normalizedQuery)> TryGetNormalizedQueryAsync(
        McpGatewaySearchQueryNormalization normalization,
        string query,
        string? chatClientFingerprint,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        return ValueTask.CompletedTask;
    }

    public ValueTask<(bool found, McpGatewayQueryEmbedding? embedding)> TryGetQueryEmbeddingAsync(
        string query,
        string? embeddingGeneratorFingerprint,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        return ValueTask.CompletedTask;
    }
}

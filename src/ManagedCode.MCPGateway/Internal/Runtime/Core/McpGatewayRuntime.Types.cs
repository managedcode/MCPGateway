using ManagedCode.MarkdownLd.Kb.Pipeline;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private sealed record InvocationResolution(bool IsSuccess, ToolCatalogEntry? Entry, string? Error)
    {
        public static InvocationResolution Success(ToolCatalogEntry entry) => new(true, entry, null);

        public static InvocationResolution Fail(string error) => new(false, null, error);
    }

    private sealed record ToolEmbeddingCandidate(
        int Index,
        McpGatewayToolEmbeddingLookup Lookup,
        string SourceId,
        string ToolName);

    private sealed record ScoredToolEntry(ToolCatalogEntry Entry, double Score);

    private sealed record ToolCatalogEntry(
        McpGatewayToolDescriptor Descriptor,
        AITool Tool,
        string Document,
        float[]? Vector = null,
        double Magnitude = 0d);

    private sealed record ToolCatalogSnapshot(
        IReadOnlyList<ToolCatalogEntry> Entries,
        bool HasVectors,
        ToolGraphSearchIndex? GraphIndex,
        int Version)
    {
        public static ToolCatalogSnapshot Empty { get; } = new([], false, null, -1);
    }

    private sealed record ToolGraphSearchIndex(
        KnowledgeGraph Graph,
        IReadOnlyDictionary<string, ToolCatalogEntry> EntriesByNodeId,
        int NodeCount,
        int EdgeCount)
    {
        public bool CanSearch => Graph.CanSearchByTokenDistance && EntriesByNodeId.Count > 0;
    }

    private sealed record ToolGraphDocumentSource(
        McpGatewayToolDescriptor Descriptor,
        string Document,
        Uri DocumentUri,
        string SourcePath,
        IReadOnlySet<string> Groups,
        string Operation);

    private sealed record RuntimeState(
        ToolCatalogSnapshot Snapshot,
        int SnapshotVersion,
        bool IsDisposed)
    {
        public static RuntimeState Empty { get; } = new(ToolCatalogSnapshot.Empty, -1, false);

        public static RuntimeState Disposed { get; } = new(ToolCatalogSnapshot.Empty, -1, true);
    }

    private sealed record SearchInput(
        string? OriginalQuery,
        string? NormalizedQuery,
        string? ContextSummary,
        string? FlattenedContext)
    {
        private const string SearchInputSegmentSeparator = " | ";
        private const string SearchInputNormalizedQueryLabel = "english query: ";

        public string VectorQuery
            => BuildEffectiveQuery(
                BuildVectorBaseQuery(OriginalQuery, NormalizedQuery),
                ContextSummary,
                FlattenedContext);

        public string GraphQuery
            => BuildEffectiveQuery(
                NormalizedQuery ?? OriginalQuery,
                ContextSummary,
                FlattenedContext);

        public string BoostQuery
            => NormalizedQuery ?? OriginalQuery ?? VectorQuery;

        public bool HasTerms
            => !string.IsNullOrWhiteSpace(VectorQuery) || !string.IsNullOrWhiteSpace(GraphQuery);

        private static string? BuildVectorBaseQuery(string? originalQuery, string? normalizedQuery)
        {
            if (string.IsNullOrWhiteSpace(normalizedQuery) ||
                string.Equals(originalQuery, normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                return originalQuery;
            }

            if (string.IsNullOrWhiteSpace(originalQuery))
            {
                return normalizedQuery;
            }

            return string.Concat(
                originalQuery,
                SearchInputSegmentSeparator,
                SearchInputNormalizedQueryLabel,
                normalizedQuery);
        }

        private static string BuildEffectiveQuery(
            string? query,
            string? contextSummary,
            string? flattenedContext)
        {
            if (query is null)
            {
                if (contextSummary is null)
                {
                    return flattenedContext is null
                        ? string.Empty
                        : string.Concat(ContextPrefix, flattenedContext);
                }

                if (flattenedContext is null)
                {
                    return string.Concat(ContextSummaryPrefix, contextSummary);
                }

                return string.Concat(
                    ContextSummaryPrefix,
                    contextSummary,
                    SearchInputSegmentSeparator,
                    ContextPrefix,
                    flattenedContext);
            }

            if (contextSummary is null)
            {
                return flattenedContext is null
                    ? query
                    : string.Concat(
                        query,
                        SearchInputSegmentSeparator,
                        ContextPrefix,
                        flattenedContext);
            }

            if (flattenedContext is null)
            {
                return string.Concat(
                    query,
                    SearchInputSegmentSeparator,
                    ContextSummaryPrefix,
                    contextSummary);
            }

            return string.Concat(
                query,
                SearchInputSegmentSeparator,
                ContextSummaryPrefix,
                contextSummary,
                SearchInputSegmentSeparator,
                ContextPrefix,
                flattenedContext);
        }
    }

    private sealed class EmbeddingGeneratorLease(
        IEmbeddingGenerator<string, Embedding<float>>? generator,
        AsyncServiceScope? scope = null)
        : IAsyncDisposable
    {
        public IEmbeddingGenerator<string, Embedding<float>>? Generator { get; } = generator;

        public ValueTask DisposeAsync() => scope?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class ToolEmbeddingStoreLease(
        IMcpGatewayToolEmbeddingStore? store,
        AsyncServiceScope? scope = null)
        : IAsyncDisposable
    {
        public IMcpGatewayToolEmbeddingStore? Store { get; } = store;

        public ValueTask DisposeAsync() => scope?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class ChatClientLease(
        IChatClient? client,
        string? fingerprint,
        AsyncServiceScope? scope = null)
        : IAsyncDisposable
    {
        public IChatClient? Client { get; } = client;

        public string? Fingerprint { get; } = fingerprint;

        public ValueTask DisposeAsync() => scope?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}

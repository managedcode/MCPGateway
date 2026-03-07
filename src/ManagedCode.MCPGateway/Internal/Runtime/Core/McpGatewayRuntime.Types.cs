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

    private sealed record RetrievalCandidate(
        ToolCatalogEntry Entry,
        double Bm25Score,
        double TokenSimilarity,
        double CharacterNGramSimilarity,
        double LegacyLexicalScore);

    private sealed record ToolCatalogEntry(
        McpGatewayToolDescriptor Descriptor,
        AITool Tool,
        string Document,
        IReadOnlyList<TokenizedSearchField> SearchFields,
        IReadOnlySet<string> LexicalTerms,
        TokenSearchProfile TokenProfile,
        TokenSearchProfile CharacterNGramProfile,
        float[]? Vector = null,
        double Magnitude = 0d);

    private sealed record ToolCatalogSnapshot(
        IReadOnlyList<ToolCatalogEntry> Entries,
        bool HasVectors,
        IReadOnlyDictionary<string, double> TokenInverseDocumentFrequencies,
        IReadOnlyDictionary<string, double> CharacterNGramInverseDocumentFrequencies,
        double AverageSearchFieldLength)
    {
        public static ToolCatalogSnapshot Empty { get; } = new([], false, EmptyTokenWeights, EmptyTokenWeights, 1d);
    }

    private sealed record RuntimeState(
        ToolCatalogSnapshot Snapshot,
        int SnapshotVersion,
        bool IsDisposed)
    {
        public static RuntimeState Empty { get; } = new(ToolCatalogSnapshot.Empty, -1, false);

        public static RuntimeState Disposed { get; } = new(ToolCatalogSnapshot.Empty, -1, true);
    }

    private sealed record WeightedTextSegment(string Text, double Weight);

    private sealed record TokenizedSearchField(
        double Weight,
        int Length,
        IReadOnlyDictionary<string, double> TermWeights,
        IReadOnlyDictionary<string, double> CharacterNGramWeights);

    private sealed record TokenSearchProfile(
        IReadOnlyDictionary<string, double> TermWeights,
        IReadOnlySet<string> Terms,
        double Magnitude,
        double TotalWeight)
    {
        public static TokenSearchProfile Empty { get; } = new(
            EmptyTokenWeights,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            0d,
            0d);
    }

    private sealed record SearchInput(
        string? OriginalQuery,
        string? NormalizedQuery,
        string? ContextSummary,
        string? FlattenedContext)
    {
        private const string SearchInputSegmentSeparator = " | ";

        public string EffectiveQuery
            => BuildEffectiveQuery(
                NormalizedQuery ?? OriginalQuery,
                ContextSummary,
                FlattenedContext);

        public string BoostQuery
            => NormalizedQuery ?? OriginalQuery ?? EffectiveQuery;

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
        AsyncServiceScope? scope = null)
        : IAsyncDisposable
    {
        public IChatClient? Client { get; } = client;

        public ValueTask DisposeAsync() => scope?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}

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
        IReadOnlyList<WeightedTextSegment> TokenSearchSegments,
        IReadOnlySet<string> LexicalTerms,
        TokenSearchProfile TokenProfile,
        float[]? Vector = null,
        double Magnitude = 0d);

    private sealed record ToolCatalogSnapshot(
        IReadOnlyList<ToolCatalogEntry> Entries,
        bool HasVectors,
        IReadOnlyDictionary<string, double> TokenInverseDocumentFrequencies)
    {
        public static ToolCatalogSnapshot Empty { get; } = new([], false, EmptyTokenWeights);
    }

    private sealed record WeightedTextSegment(string Text, double Weight);

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
}

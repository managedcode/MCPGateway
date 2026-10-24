using ManagedCode.MarkdownLd.Kb.Pipeline;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private sealed record InvocationResolution(
        bool IsSuccess,
        ToolCatalogEntry? Entry,
        string? Error
    )
    {
        public static InvocationResolution Success(ToolCatalogEntry entry) =>
            new(true, entry, null);

        public static InvocationResolution Fail(string error) => new(false, null, error);
    }

    private readonly record struct ToolEmbeddingCandidate(
        int Index,
        McpGatewayToolEmbeddingLookup Lookup,
        string SourceId,
        string ToolName
    );

    private readonly record struct ScoredToolEntry(ToolCatalogEntry Entry, double Score);

    private sealed record ToolCatalogEntry(
        McpGatewayToolDescriptor Descriptor,
        AITool Tool,
        string Document,
        IReadOnlySet<string> SearchBoostTerms,
        IReadOnlyList<GraphConfidenceDescriptorTerm> ConfidenceTerms,
        McpClient? Client,
        float[]? Vector = null,
        double Magnitude = ToolEmbeddingDefaultMagnitude
    )
    {
        // Hashes canonicalize full JSON definitions; they are computed when a search result first reports them.
        private readonly Lazy<string> _schemaHash = CreateSchemaHash(Descriptor);
        private readonly Lazy<string> _technicalDefinitionHash = CreateTechnicalDefinitionHash(Descriptor);

        public McpGatewaySchemaValidator Validator { get; } = new(Descriptor);
        public string SchemaHash => _schemaHash.Value;
        public string TechnicalDefinitionHash => _technicalDefinitionHash.Value;

        private static Lazy<string> CreateSchemaHash(McpGatewayToolDescriptor descriptor) =>
            new(() => McpGatewayDefinitionHash.Compute(new { descriptor.InputSchema, descriptor.OutputSchema }));

        private static Lazy<string> CreateTechnicalDefinitionHash(McpGatewayToolDescriptor descriptor) =>
            new(() => McpGatewayDefinitionHash.Create(descriptor, descriptor.BindingFingerprint ?? string.Empty).DefinitionHash);
    }

    private readonly record struct ToolSearchTermIndex(
        IReadOnlySet<string> SearchBoostTerms,
        IReadOnlyList<GraphConfidenceDescriptorTerm> ConfidenceTerms
    );

    private sealed record SearchScoreContext(
        IReadOnlySet<string> BoostTerms,
        IReadOnlyList<GraphConfidenceQueryTerm> ConfidenceTerms
    )
    {
        public static SearchScoreContext Empty { get; } = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            []
        );

        public bool HasBoostTerms => BoostTerms.Count > 0;

        public bool HasConfidenceTerms => ConfidenceTerms.Count > 0;
    }

    private readonly record struct GraphConfidenceDescriptorTerm(
        string Value,
        IReadOnlySet<string> Bigrams
    );

    private readonly record struct GraphConfidenceQueryTerm(
        string Value,
        double Weight,
        IReadOnlySet<string> Bigrams
    );

    private readonly record struct GraphSchemaQueryTerm(
        string Value,
        int OriginalIndex,
        double Weight
    );

    private sealed record ToolCatalogSnapshot(
        IReadOnlyList<ToolCatalogEntry> Entries,
        bool HasVectors,
        ToolGraphSearchIndex? GraphIndex,
        int Version
    )
    {
        public SnapshotOwnership Ownership { get; init; } = new(null);
        public IReadOnlyDictionary<McpGatewayToolSourceRegistration, IReadOnlyList<McpGatewayLoadedTool>> LoadedTools { get; init; } =
            new Dictionary<McpGatewayToolSourceRegistration, IReadOnlyList<McpGatewayLoadedTool>>();
        public object? SourceOwner { get; init; }
        public string? VisibilityFingerprint { get; init; }
        public static ToolCatalogSnapshot Empty { get; } = new([], false, null, -1);
    }

    /// <summary>
    /// One runtime's view of a tool graph artifact: the shared immutable graph data bound to this runtime's own
    /// catalog entries. Pooled views release their lease; other views own and dispose their artifact.
    /// </summary>
    private sealed record ToolGraphSearchIndex(
        ToolGraphArtifact Artifact,
        IReadOnlyDictionary<string, ToolCatalogEntry> EntriesByNodeId,
        IReadOnlyList<McpGatewayDiagnostic> SchemaDiagnostics,
        McpGatewaySharedIndexPool<ToolGraphArtifact>.Lease? Lease
    )
    {
        public KnowledgeGraph Graph => Artifact.Graph;

        public KnowledgeGraphSnapshot Snapshot => Artifact.Snapshot;

        public IReadOnlySet<string> SearchableNodeIds => Artifact.SearchableNodeIds;

        public IReadOnlyDictionary<string, KnowledgeGraphNode> NodesById => Artifact.NodesById;

        public GraphCandidateSearchIndex CandidateSearch => Artifact.CandidateSearch;

        public GraphNavigationIndex Navigation => Artifact.Navigation;

        public int NodeCount => Artifact.NodeCount;

        public int EdgeCount => Artifact.EdgeCount;

        public Lazy<Task<KnowledgeGraphFullTextIndex>> FullText => Artifact.FullText;

        public bool IsReused => Lease?.IsReused ?? false;

        public bool CanSearch => Graph.TripleCount > 0 && EntriesByNodeId.Count > 0;

        public bool CanSearchByTokenDistance => Graph.CanSearchByTokenDistance;

        public ValueTask ReleaseAsync(bool keepForReuse) =>
            Lease is { } lease ? lease.ReleaseAsync(keepForReuse) : Artifact.DisposeAsync();
    }

    internal sealed record GraphCandidateSearchIndex(
        IReadOnlyList<GraphCandidateSearchEntry> Entries,
        IReadOnlyDictionary<string, double> InverseDocumentFrequency
    )
    {
        public static GraphCandidateSearchIndex Empty { get; } = new(
            [],
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        );
    }

    internal readonly record struct GraphCandidateSearchEntry(
        string NodeId,
        string Label,
        string? Description,
        IReadOnlySet<string> Terms
    );

    internal sealed record GraphNavigationIndex(
        IReadOnlyDictionary<string, IReadOnlyList<KnowledgeGraphEdge>> EdgesBySubject,
        IReadOnlyDictionary<string, IReadOnlyList<GraphNavigationLink>> RelatedBySource,
        IReadOnlyDictionary<string, IReadOnlyList<GraphNavigationLink>> NextStepsBySource,
        IReadOnlyDictionary<string, IReadOnlyList<string>> GroupIdsBySource,
        IReadOnlyDictionary<string, IReadOnlyList<string>> MembersByGroupId
    )
    {
        public static GraphNavigationIndex Empty { get; } = new(
            new Dictionary<string, IReadOnlyList<KnowledgeGraphEdge>>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<GraphNavigationLink>>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<GraphNavigationLink>>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        );
    }

    internal readonly record struct GraphNavigationLink(
        string NodeId,
        string SourceNodeId,
        string ViaPredicateLabel,
        double Score
    );

    private readonly record struct ToolGraphAffinityCandidate(
        ToolGraphDocumentSource Document,
        int Affinity
    );

    private sealed record ToolGraphDocumentSource(
        McpGatewayToolDescriptor Descriptor,
        string Document,
        Uri DocumentUri,
        string SourcePath,
        IReadOnlySet<string> Groups,
        string Operation
    );

    private sealed record RuntimeState(
        ToolCatalogSnapshot Snapshot,
        int SnapshotVersion,
        bool IsDisposed
    )
    {
        public static RuntimeState Empty { get; } = new(ToolCatalogSnapshot.Empty, -1, false);

        public static RuntimeState Disposed { get; } = new(ToolCatalogSnapshot.Empty, -1, true);
    }

    private sealed record SearchInput(
        string? OriginalQuery,
        string? NormalizedQuery,
        string? ContextSummary,
        string? FlattenedContext
    )
    {
        private const string SearchInputSegmentSeparator = " | ";
        private const string SearchInputNormalizedQueryLabel = "english query: ";

        public string VectorQuery =>
            BuildEffectiveQuery(
                BuildVectorBaseQuery(OriginalQuery, NormalizedQuery),
                ContextSummary,
                FlattenedContext
            );

        public string GraphQuery =>
            BuildEffectiveQuery(NormalizedQuery ?? OriginalQuery, ContextSummary, FlattenedContext);

        public string BoostQuery => NormalizedQuery ?? OriginalQuery ?? VectorQuery;

        public bool HasTerms =>
            !string.IsNullOrWhiteSpace(VectorQuery) || !string.IsNullOrWhiteSpace(GraphQuery);

        private static string? BuildVectorBaseQuery(string? originalQuery, string? normalizedQuery)
        {
            if (
                string.IsNullOrWhiteSpace(normalizedQuery)
                || string.Equals(originalQuery, normalizedQuery, StringComparison.OrdinalIgnoreCase)
            )
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
                normalizedQuery
            );
        }

        private static string BuildEffectiveQuery(
            string? query,
            string? contextSummary,
            string? flattenedContext
        )
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
                    flattenedContext
                );
            }

            if (contextSummary is null)
            {
                return flattenedContext is null
                    ? query
                    : string.Concat(
                        query,
                        SearchInputSegmentSeparator,
                        ContextPrefix,
                        flattenedContext
                    );
            }

            if (flattenedContext is null)
            {
                return string.Concat(
                    query,
                    SearchInputSegmentSeparator,
                    ContextSummaryPrefix,
                    contextSummary
                );
            }

            return string.Concat(
                query,
                SearchInputSegmentSeparator,
                ContextSummaryPrefix,
                contextSummary,
                SearchInputSegmentSeparator,
                ContextPrefix,
                flattenedContext
            );
        }

    }

    private sealed class EmbeddingGeneratorLease(
        IEmbeddingGenerator<string, Embedding<float>>? generator,
        AsyncServiceScope? scope = null
    ) : IAsyncDisposable
    {
        public IEmbeddingGenerator<string, Embedding<float>>? Generator { get; } = generator;

        public ValueTask DisposeAsync() => scope?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class ToolEmbeddingStoreLease(
        IMcpGatewayToolEmbeddingStore? store,
        AsyncServiceScope? scope = null
    ) : IAsyncDisposable
    {
        public IMcpGatewayToolEmbeddingStore? Store { get; } = store;

        public ValueTask DisposeAsync() => scope?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class ChatClientLease(
        IChatClient? client,
        string? fingerprint,
        AsyncServiceScope? scope = null
    ) : IAsyncDisposable
    {
        public IChatClient? Client { get; } = client;

        public string? Fingerprint { get; } = fingerprint;

        public ValueTask DisposeAsync() => scope?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class BuildOperation(
        Task<McpGatewayIndexBuildResult> task,
        CancellationTokenSource cancellationSource,
        CancellationToken cancellationToken
    ) : IDisposable
    {
        private int _disposed;

        public Task<McpGatewayIndexBuildResult> Task { get; } = task;

        public CancellationTokenSource CancellationSource { get; } = cancellationSource;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public static BuildOperation Create(
            Task<McpGatewayIndexBuildResult> task,
            CancellationToken cancellationToken
        )
        {
            var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            return new BuildOperation(task, cancellationSource, cancellationSource.Token);
        }

        public Task CancelAsync()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            try
            {
                return CancellationSource.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                CancellationSource.Dispose();
            }
        }
    }

    private sealed record VectorizationOutcome(
        int VectorizedToolCount,
        bool IsVectorSearchEnabled,
        VectorTokenUsage VectorTokenUsage
    )
    {
        public static VectorizationOutcome Empty { get; } = new(0, false, VectorTokenUsage.Zero);
    }

    private sealed record EmbeddingGenerationOutcome(
        int VectorizedToolCount,
        VectorTokenUsage VectorTokenUsage
    )
    {
        public static EmbeddingGenerationOutcome Empty { get; } = new(0, VectorTokenUsage.Zero);
    }

    private sealed record GeneratedEmbeddingsResult(
        IReadOnlyList<McpGatewayToolEmbedding> GeneratedEmbeddings,
        int VectorizedToolCount
    );
}

internal readonly record struct VectorTokenUsage(long InputTokenCount, long TotalTokenCount)
{
    public static VectorTokenUsage Zero { get; } = new(0, 0);
}

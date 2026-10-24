using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ManagedCode.MarkdownLd.Kb.Pipeline;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private const string ToolGraphFingerprintScheme = "managedcode.mcpgateway.generated-tool-graph";
    private const int ToolGraphFingerprintNullLength = -1;
    private static readonly string ToolGraphFingerprintIdentity = CreateToolGraphFingerprintIdentity();

    /// <summary>
    /// Immutable graph-derived search data for one tool catalog. It holds no tool invokers, clients or
    /// per-instance services; <see cref="EntrySlots"/> binds graph nodes to catalog positions instead.
    /// </summary>
    internal sealed class ToolGraphArtifact : IAsyncDisposable
    {
        public ToolGraphArtifact(
            KnowledgeGraph graph,
            KnowledgeGraphSnapshot snapshot,
            IReadOnlyList<KeyValuePair<string, int>> entrySlots,
            IReadOnlySet<string> searchableNodeIds,
            IReadOnlyDictionary<string, KnowledgeGraphNode> nodesById,
            GraphCandidateSearchIndex candidateSearch,
            GraphNavigationIndex navigation
        )
        {
            Graph = graph;
            Snapshot = snapshot;
            EntrySlots = entrySlots;
            SearchableNodeIds = searchableNodeIds;
            NodesById = nodesById;
            CandidateSearch = candidateSearch;
            Navigation = navigation;
            FullText = new(() => Graph.BuildFullTextIndexAsync());
            DefaultProfileSchemaDiagnostics = new(() => CreateSchemaProfileDiagnostics(
                Graph,
                CreateDefaultToolGraphSchemaSearchProfile(
                    McpGatewayOptions.DefaultSearchLimitValue,
                    GraphFocusedRelatedResultsLimit,
                    GraphFocusedNextStepResultsLimit
                )
            ));
        }

        public KnowledgeGraph Graph { get; }

        public KnowledgeGraphSnapshot Snapshot { get; }

        /// <summary>Graph node id to catalog entry position, in the order of the original node binding.</summary>
        public IReadOnlyList<KeyValuePair<string, int>> EntrySlots { get; }

        public IReadOnlySet<string> SearchableNodeIds { get; }

        public IReadOnlyDictionary<string, KnowledgeGraphNode> NodesById { get; }

        public GraphCandidateSearchIndex CandidateSearch { get; }

        public GraphNavigationIndex Navigation { get; }

        public int NodeCount => Snapshot.Nodes.Count;

        public int EdgeCount => Snapshot.Edges.Count;

        /// <summary>Full-text index built on first full-text use and disposed with the artifact.</summary>
        public Lazy<Task<KnowledgeGraphFullTextIndex>> FullText { get; }

        /// <summary>
        /// Validation of the gateway-owned schema profile. Validation reads only the profile vocabulary, which the
        /// default profile fixes, so every runtime without a configured profile shares this result.
        /// </summary>
        public Lazy<IReadOnlyList<McpGatewayDiagnostic>> DefaultProfileSchemaDiagnostics { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (FullText.IsValueCreated)
                {
                    (await FullText.Value.ConfigureAwait(false)).Dispose();
                }
            }
            finally
            {
                Graph.Dispose();
            }
        }
    }

    private async Task<ToolGraphSearchIndex> AcquireGeneratedToolGraphSearchIndexAsync(
        IReadOnlyList<ToolCatalogEntry> entries,
        CancellationToken cancellationToken
    )
    {
        var documents = CreateGeneratedMarkdownSourceDocuments(entries);
        var fingerprint = ComputeToolGraphFingerprint(entries, documents);
        var lease = await _toolGraphPool
            .AcquireAsync(
                fingerprint,
                () => BuildGeneratedToolGraphArtifactAsync(entries, documents),
                cancellationToken
            )
            .ConfigureAwait(false);
        try
        {
            return CreateToolGraphSearchIndex(lease.Index, BindEntrySlots(lease.Index, entries), lease);
        }
        catch
        {
            await lease.ReleaseAsync(keepForReuse: false).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ToolGraphArtifact> BuildGeneratedToolGraphArtifactAsync(
        IReadOnlyList<ToolCatalogEntry> entries,
        IReadOnlyList<MarkdownSourceDocument> documents
    )
    {
        // The pool owns this build for every caller waiting on the fingerprint, so no single caller's token stops it.
        var result = await CreateToolGraphPipeline().BuildAsync(documents).ConfigureAwait(false);
        var entriesByNodeId = BindGraphDocuments(result, entries);
        return CreateToolGraphArtifact(result.Graph, entriesByNodeId, CreateEntrySlots(entries, entriesByNodeId));
    }

    /// <summary>Binds built graph documents to catalog entries; the graph is disposed when the binding is invalid.</summary>
    private IReadOnlyDictionary<string, ToolCatalogEntry> BindGraphDocuments(
        MarkdownKnowledgeBuildResult result,
        IReadOnlyList<ToolCatalogEntry> entries
    )
    {
        try
        {
            return CreateEntriesByGraphNodeId(entries, result.Documents);
        }
        catch
        {
            result.Graph.Dispose();
            throw;
        }
    }

    private static KeyValuePair<string, int>[] CreateEntrySlots(
        IReadOnlyList<ToolCatalogEntry> entries,
        IReadOnlyDictionary<string, ToolCatalogEntry> entriesByNodeId
    )
    {
        var positions = new Dictionary<ToolCatalogEntry, int>(entries.Count, ReferenceEqualityComparer.Instance);
        for (var index = 0; index < entries.Count; index++)
        {
            positions.TryAdd(entries[index], index);
        }

        var slots = new KeyValuePair<string, int>[entriesByNodeId.Count];
        var slot = 0;
        foreach (var (nodeId, entry) in entriesByNodeId)
        {
            slots[slot++] = new(nodeId, positions[entry]);
        }

        return slots;
    }

    private static Dictionary<string, ToolCatalogEntry> BindEntrySlots(
        ToolGraphArtifact artifact,
        IReadOnlyList<ToolCatalogEntry> entries
    )
    {
        var entriesByNodeId = new Dictionary<string, ToolCatalogEntry>(artifact.EntrySlots.Count, StringComparer.Ordinal);
        foreach (var (nodeId, position) in artifact.EntrySlots)
        {
            entriesByNodeId.Add(nodeId, entries[position]);
        }

        return entriesByNodeId;
    }

    /// <summary>
    /// Fingerprints exactly the inputs a generated tool graph artifact is derived from: the generated Markdown-LD
    /// documents, the descriptor text that feeds candidate terms, catalog order, the graph pipeline settings and
    /// the gateway and Markdown-LD library versions.
    /// </summary>
    private static string ComputeToolGraphFingerprint(
        IReadOnlyList<ToolCatalogEntry> entries,
        IReadOnlyList<MarkdownSourceDocument> documents
    )
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFingerprintText(hash, ToolGraphFingerprintIdentity);
        AppendFingerprintNumber(hash, entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var document = documents[index];
            AppendFingerprintText(hash, document.Path);
            AppendFingerprintText(hash, document.CanonicalUri?.AbsoluteUri);
            AppendFingerprintText(hash, document.Content);
            AppendFingerprintText(hash, entry.Document);
            AppendFingerprintText(hash, entry.Descriptor.ToolName);
            AppendFingerprintText(hash, entry.Descriptor.DisplayName);
            AppendFingerprintText(hash, entry.Descriptor.Description);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendFingerprintText(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            AppendFingerprintNumber(hash, ToolGraphFingerprintNullLength);
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        AppendFingerprintNumber(hash, byteCount);
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(value, buffer);
            hash.AppendData(buffer, 0, written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void AppendFingerprintNumber(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static string CreateToolGraphFingerprintIdentity() =>
        string.Join(
            FingerprintComponentSeparator,
            ToolGraphFingerprintScheme,
            ReadAssemblyVersion(typeof(McpGatewayRuntime).Assembly),
            ReadAssemblyVersion(typeof(KnowledgeGraph).Assembly),
            GraphKnowledgeBaseUriText,
            nameof(MarkdownKnowledgeExtractionMode.Tiktoken),
            GraphMaxRelatedTokenSegments.ToString(CultureInfo.InvariantCulture)
        );

    private static string ReadAssemblyVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? FingerprintUnknownComponent;
}

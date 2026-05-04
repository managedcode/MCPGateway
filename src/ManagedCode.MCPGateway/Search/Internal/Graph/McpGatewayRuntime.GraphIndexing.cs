using ManagedCode.MarkdownLd.Kb.Pipeline;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private static bool IsGraphSearchStrategy(McpGatewaySearchStrategy strategy) =>
        strategy
            is McpGatewaySearchStrategy.Auto
                or McpGatewaySearchStrategy.Graph
                or McpGatewaySearchStrategy.Embeddings;

    private bool ShouldBuildGraphSearchIndex() => IsGraphSearchStrategy(_searchStrategy);

    internal static IReadOnlyList<McpGatewayMarkdownLdGraphDocument> CreateMarkdownLdGraphFileDocuments(
        IReadOnlyList<McpGatewayToolDescriptor> descriptors,
        int maxDescriptorLength
    )
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var graphDocuments = CreateToolGraphDocumentSources(
            descriptors
                .Select(descriptor =>
                    (
                        Descriptor: descriptor,
                        Document: BuildDescriptorDocument(descriptor, maxDescriptorLength)
                    )
                )
                .ToArray()
        );
        return CreateMarkdownLdGraphFileDocuments(graphDocuments);
    }

    private async Task<ToolGraphSearchIndex?> BuildToolGraphSearchIndexAsync(
        IReadOnlyList<ToolCatalogEntry> entries,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        var documents = _markdownLdGraphSource switch
        {
            McpGatewayMarkdownLdGraphSource.FileSystem =>
                await LoadFileSystemMarkdownLdGraphDocumentsAsync(diagnostics, cancellationToken)
                    .ConfigureAwait(false),
            McpGatewayMarkdownLdGraphSource.CustomDocuments =>
                await LoadCustomMarkdownLdGraphDocumentsAsync(
                        entries,
                        diagnostics,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
            _ => CreateGeneratedMarkdownSourceDocuments(entries),
        };

        if (documents.Count == 0)
        {
            return null;
        }

        var pipeline = CreateToolGraphPipeline();
        var result = await pipeline.BuildAsync(documents, cancellationToken).ConfigureAwait(false);
        var snapshot = result.Graph.ToSnapshot();
        var entriesByNodeId = CreateEntriesByGraphNodeId(entries, result.Documents);
        var searchableNodeIds = entriesByNodeId.Keys.ToHashSet(StringComparer.Ordinal);
        var nodesById = CreateRankedGraphNodesById(snapshot.Nodes);
        var navigation = CreateGraphNavigationIndex(snapshot, searchableNodeIds);
        var schemaDiagnostics = CreateSchemaProfileDiagnostics(
            result.Graph,
            CreateToolGraphSchemaSearchProfile(
                _defaultSearchLimit,
                GraphFocusedRelatedResultsLimit,
                GraphFocusedNextStepResultsLimit
            )
        );
        return new ToolGraphSearchIndex(
            result.Graph,
            snapshot,
            entriesByNodeId,
            searchableNodeIds,
            nodesById,
            CreateGraphCandidateSearchIndex(entriesByNodeId, nodesById, navigation),
            navigation,
            snapshot.Nodes.Count,
            snapshot.Edges.Count,
            schemaDiagnostics
        );
    }

    private async Task<
        IReadOnlyList<MarkdownSourceDocument>
    > LoadCustomMarkdownLdGraphDocumentsAsync(
        IReadOnlyList<ToolCatalogEntry> entries,
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        if (_markdownLdGraphDocumentFactory is null)
        {
            diagnostics.Add(
                new McpGatewayDiagnostic(
                    MarkdownLdGraphDocumentFactoryMissingDiagnosticCode,
                    MarkdownLdGraphDocumentFactoryMissingMessage
                )
            );
            return [];
        }

        var descriptors = entries.Select(static entry => entry.Descriptor).ToArray();
        var documents = await _markdownLdGraphDocumentFactory(descriptors, cancellationToken)
            .ConfigureAwait(false);
        return McpGatewayMarkdownLdGraphFile.ToMarkdownSourceDocuments(documents);
    }

    private async Task<
        IReadOnlyList<MarkdownSourceDocument>
    > LoadFileSystemMarkdownLdGraphDocumentsAsync(
        IList<McpGatewayDiagnostic> diagnostics,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(_markdownLdGraphPath))
        {
            diagnostics.Add(
                new McpGatewayDiagnostic(
                    MarkdownLdGraphPathMissingDiagnosticCode,
                    MarkdownLdGraphPathMissingMessage
                )
            );
            return [];
        }

        if (Directory.Exists(_markdownLdGraphPath))
        {
            return await LoadMarkdownLdGraphDirectoryAsync(_markdownLdGraphPath, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!File.Exists(_markdownLdGraphPath))
        {
            throw new FileNotFoundException(
                MarkdownLdGraphPathMissingMessage,
                _markdownLdGraphPath
            );
        }

        if (IsGraphBundleFile(_markdownLdGraphPath))
        {
            var documents = await McpGatewayMarkdownLdGraphFile
                .ReadAsync(_markdownLdGraphPath, cancellationToken)
                .ConfigureAwait(false);
            return McpGatewayMarkdownLdGraphFile.ToMarkdownSourceDocuments(documents);
        }

        var converter = new KnowledgeSourceDocumentConverter();
        var source = await converter
            .ConvertFileAsync(_markdownLdGraphPath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return [source.ToMarkdownSourceDocument()];
    }

    private static async Task<
        IReadOnlyList<MarkdownSourceDocument>
    > LoadMarkdownLdGraphDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        var converter = new KnowledgeSourceDocumentConverter();
        var documents = new List<MarkdownSourceDocument>();
        await foreach (
            var source in converter
                .ConvertDirectoryAsync(directoryPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false)
        )
        {
            documents.Add(source.ToMarkdownSourceDocument());
        }

        return documents;
    }

    private static IReadOnlyList<MarkdownSourceDocument> CreateGeneratedMarkdownSourceDocuments(
        IReadOnlyList<ToolCatalogEntry> entries
    )
    {
        var graphDocuments = CreateToolGraphDocumentSources(
            entries.Select(static entry => (entry.Descriptor, entry.Document)).ToArray()
        );
        return CreateMarkdownLdGraphFileDocuments(graphDocuments)
            .Select(static document => new MarkdownSourceDocument(
                document.Path,
                document.Content,
                new Uri(document.CanonicalUri!, UriKind.Absolute)
            ))
            .ToArray();
    }

    private static bool IsGraphBundleFile(string filePath) =>
        Path.GetExtension(filePath).Equals(".json", StringComparison.OrdinalIgnoreCase);
}

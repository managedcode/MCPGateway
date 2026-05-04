using ManagedCode.MarkdownLd.Kb.Pipeline;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    internal static async Task<McpGatewayMarkdownLdGraphExport> ExportMarkdownLdGraphAsync(
        IEnumerable<McpGatewayMarkdownLdGraphDocument> documents,
        CancellationToken cancellationToken
    )
    {
        var sourceDocuments = McpGatewayMarkdownLdGraphFile.ToMarkdownSourceDocuments(documents);
        var pipeline = CreateToolGraphPipeline();
        var result = await pipeline
            .BuildAsync(sourceDocuments, cancellationToken)
            .ConfigureAwait(false);
        var snapshot = result.Graph.ToSnapshot();

        return new McpGatewayMarkdownLdGraphExport(
            result.Graph.SerializeJsonLd(),
            result.Graph.SerializeTurtle(),
            result.Graph.SerializeMermaidFlowchart(),
            result.Graph.SerializeDotGraph(),
            snapshot.Nodes.Count,
            snapshot.Edges.Count
        );
    }

    private static MarkdownKnowledgePipeline CreateToolGraphPipeline() =>
        new(
            new Uri(GraphKnowledgeBaseUriText, UriKind.Absolute),
            extractionMode: MarkdownKnowledgeExtractionMode.Tiktoken,
            tiktokenOptions: new TiktokenKnowledgeGraphOptions
            {
                MaxRelatedSegments = GraphMaxRelatedTokenSegments,
            }
        );
}

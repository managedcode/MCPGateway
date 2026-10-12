using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayFullTextToolSet(IMcpGatewayGraphFullTextSearch search)
{
    public const string BuildToolName = "gateway_graph_fulltext_build";
    public const string SearchToolName = "gateway_graph_fulltext_search";
    private const string BuildDescription = "Build or inspect the Lucene index for the caller-authorized RDF graph.";
    private const string SearchDescription = "Search literals in the caller-authorized RDF graph with Lucene query syntax. Scores are Lucene relevance values.";

    public IReadOnlyList<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create(BuildAsync, new AIFunctionFactoryOptions { Name = BuildToolName, Description = BuildDescription }),
        AIFunctionFactory.Create(SearchAsync, new AIFunctionFactoryOptions { Name = SearchToolName, Description = SearchDescription }),
    ];

    private Task<McpGatewayFullTextIndexResult> BuildAsync(CancellationToken cancellationToken) => search.BuildFullTextIndexAsync(cancellationToken);
    private Task<IReadOnlyList<McpGatewayFullTextMatch>> SearchAsync(string query, int? maxResults = null,
        CancellationToken cancellationToken = default) => search.SearchFullTextAsync(query, maxResults, cancellationToken);
}

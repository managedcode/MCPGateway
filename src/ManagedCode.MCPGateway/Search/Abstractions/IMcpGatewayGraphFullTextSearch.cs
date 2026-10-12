namespace ManagedCode.MCPGateway.Abstractions;

/// <summary>Lucene retrieval over the current caller-authorized RDF graph.</summary>
public interface IMcpGatewayGraphFullTextSearch
{
    Task<McpGatewayFullTextIndexResult> BuildFullTextIndexAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<McpGatewayFullTextMatch>> SearchFullTextAsync(string query, int? maxResults = null,
        CancellationToken cancellationToken = default);
}

public sealed record McpGatewayFullTextIndexResult(int NodeCount, int TripleCount);
public sealed record McpGatewayFullTextMatch(string NodeId, string Label, double LuceneScore, string? ToolId);

namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayGraphSearch
{
    Task<McpGatewayGraphSchemaResult> DescribeGraphSchemaAsync(
        CancellationToken cancellationToken = default
    );

    Task<McpGatewayGraphSearchResult> SearchGraphAsync(
        McpGatewayGraphSearchRequest request,
        CancellationToken cancellationToken = default
    );

    Task<McpGatewayMarkdownLdGraphExport> ExportMarkdownLdGraphAsync(
        CancellationToken cancellationToken = default
    );
}

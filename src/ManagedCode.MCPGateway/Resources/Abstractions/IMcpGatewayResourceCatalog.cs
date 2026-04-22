namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayResourceCatalog
{
    Task<IReadOnlyList<McpGatewayResourceDescriptor>> ListResourcesAsync(
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<McpGatewayResourceTemplateDescriptor>> ListResourceTemplatesAsync(
        CancellationToken cancellationToken = default
    );

    Task<McpGatewayResourceResult?> ReadResourceAsync(
        McpGatewayResourceRequest request,
        CancellationToken cancellationToken = default
    );
}

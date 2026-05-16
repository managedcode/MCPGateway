using ManagedCode.MCPGateway.Abstractions;

namespace ManagedCode.MCPGateway.Tests;

internal sealed class StaticMcpGatewayResourceCatalog(
    IReadOnlyList<McpGatewayResourceDescriptor> resources,
    IReadOnlyList<McpGatewayResourceTemplateDescriptor>? templates = null
) : IMcpGatewayResourceCatalog
{
    public Task<IReadOnlyList<McpGatewayResourceDescriptor>> ListResourcesAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(resources);
    }

    public Task<IReadOnlyList<McpGatewayResourceTemplateDescriptor>> ListResourceTemplatesAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(templates ?? []);
    }

    public Task<McpGatewayResourceResult?> ReadResourceAsync(
        McpGatewayResourceRequest request,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<McpGatewayResourceResult?>(null);
}

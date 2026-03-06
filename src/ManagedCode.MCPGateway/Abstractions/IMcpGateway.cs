using Microsoft.Extensions.AI;

namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGateway : IAsyncDisposable
{
    Task<McpGatewayIndexBuildResult> BuildIndexAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<McpGatewayToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default);

    Task<McpGatewaySearchResult> SearchAsync(
        string? query,
        int? maxResults = null,
        CancellationToken cancellationToken = default);

    Task<McpGatewaySearchResult> SearchAsync(
        McpGatewaySearchRequest request,
        CancellationToken cancellationToken = default);

    Task<McpGatewayInvokeResult> InvokeAsync(
        McpGatewayInvokeRequest request,
        CancellationToken cancellationToken = default);

    IReadOnlyList<AITool> CreateMetaTools(
        string searchToolName = McpGatewayToolSet.DefaultSearchToolName,
        string invokeToolName = McpGatewayToolSet.DefaultInvokeToolName);
}

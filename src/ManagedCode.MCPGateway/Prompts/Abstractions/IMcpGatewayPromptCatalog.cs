namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayPromptCatalog
{
    Task<IReadOnlyList<McpGatewayPromptDescriptor>> ListPromptsAsync(
        CancellationToken cancellationToken = default
    );

    Task<McpGatewayPromptResult?> GetPromptAsync(
        McpGatewayPromptRequest request,
        CancellationToken cancellationToken = default
    );
}

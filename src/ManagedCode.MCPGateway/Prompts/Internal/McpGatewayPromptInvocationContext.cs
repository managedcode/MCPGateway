using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayPromptInvocationContext(
    IServiceProvider services,
    Func<McpGatewayPromptRequest, CancellationToken, ValueTask<GetPromptResult?>> renderPromptAsync
)
{
    public IServiceProvider Services { get; } = services;

    public ValueTask<GetPromptResult?> RenderPromptAsync(
        McpGatewayPromptRequest request,
        CancellationToken cancellationToken
    ) => renderPromptAsync(request, cancellationToken);
}

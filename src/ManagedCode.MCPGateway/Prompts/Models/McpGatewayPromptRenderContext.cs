using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayPromptRenderContext
{
    private readonly Func<McpGatewayPromptRequest, CancellationToken, ValueTask<GetPromptResult?>>
        _renderPromptAsync;

    internal McpGatewayPromptRenderContext(
        string sourceId,
        string promptName,
        IReadOnlyDictionary<string, object?> arguments,
        IServiceProvider services,
        Func<McpGatewayPromptRequest, CancellationToken, ValueTask<GetPromptResult?>> renderPromptAsync
    )
    {
        SourceId = sourceId;
        PromptName = promptName;
        Arguments = arguments;
        Services = services;
        _renderPromptAsync = renderPromptAsync;
    }

    public string SourceId { get; }

    public string PromptName { get; }

    public IReadOnlyDictionary<string, object?> Arguments { get; }

    public IServiceProvider Services { get; }

    public ValueTask<GetPromptResult?> GetPromptAsync(
        McpGatewayPromptRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        return _renderPromptAsync(request, cancellationToken);
    }

    public ValueTask<GetPromptResult?> GetPromptAsync(
        string sourceId,
        string promptName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default
    ) =>
        GetPromptAsync(
            new McpGatewayPromptRequest(sourceId, promptName, arguments),
            cancellationToken
        );
}

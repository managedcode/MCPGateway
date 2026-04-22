using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayPrompt
{
    public McpGatewayPrompt(
        string name,
        Func<McpGatewayPromptRenderContext, CancellationToken, ValueTask<GetPromptResult>> renderAsync
    )
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A prompt name is required.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(renderAsync);

        Name = name.Trim();
        RenderAsync = renderAsync;
    }

    public string Name { get; }

    public string? DisplayName { get; init; }

    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<McpGatewayPromptArgumentDescriptor> Arguments { get; init; } = [];

    public Func<McpGatewayPromptRenderContext, CancellationToken, ValueTask<GetPromptResult>> RenderAsync
    {
        get;
    }

    public Func<McpGatewayPromptCompletionContext, CancellationToken, ValueTask<CompleteResult?>>? CompleteAsync
    {
        get;
        init;
    }
}

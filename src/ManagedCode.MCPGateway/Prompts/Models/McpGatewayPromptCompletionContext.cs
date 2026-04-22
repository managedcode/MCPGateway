using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

public sealed class McpGatewayPromptCompletionContext(
    string sourceId,
    string promptName,
    string argumentName,
    string argumentValue,
    CompleteContext? context,
    IServiceProvider services
)
{
    public string SourceId { get; } = sourceId;

    public string PromptName { get; } = promptName;

    public string ArgumentName { get; } = argumentName;

    public string ArgumentValue { get; } = argumentValue;

    public CompleteContext? Context { get; } = context;

    public IServiceProvider Services { get; } = services;
}

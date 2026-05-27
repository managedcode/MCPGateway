using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

public sealed record McpGatewayPromptDescriptor(
    string PromptId,
    string SourceId,
    McpGatewaySourceKind SourceKind,
    Prompt ProtocolPrompt
)
{
    public string PromptName => ProtocolPrompt.Name;

    public string? DisplayName => ProtocolPrompt.Title;

    public string Description => ProtocolPrompt.Description ?? string.Empty;

    public IReadOnlyList<PromptArgument> Arguments =>
        ProtocolPrompt.Arguments as IReadOnlyList<PromptArgument>
        ?? ProtocolPrompt.Arguments?.ToArray()
        ?? [];

    public IReadOnlyList<string> RequiredArguments =>
        Arguments
            .Where(static argument => argument.Required == true)
            .Select(static argument => argument.Name)
            .ToArray();
}

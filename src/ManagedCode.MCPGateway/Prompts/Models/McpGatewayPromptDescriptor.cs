namespace ManagedCode.MCPGateway;

public sealed record McpGatewayPromptDescriptor(
    string PromptId,
    string SourceId,
    McpGatewaySourceKind SourceKind,
    string PromptName,
    string? DisplayName,
    string Description,
    IReadOnlyList<McpGatewayPromptArgumentDescriptor> Arguments
)
{
    public IReadOnlyList<string> RequiredArguments { get; init; } = [];
}

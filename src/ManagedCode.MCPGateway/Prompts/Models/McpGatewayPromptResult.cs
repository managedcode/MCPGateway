using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

public sealed record McpGatewayPromptResult(
    string PromptId,
    string SourceId,
    McpGatewaySourceKind SourceKind,
    string PromptName,
    GetPromptResult ProtocolResult
)
{
    public string Description => ProtocolResult.Description ?? string.Empty;

    public IReadOnlyList<PromptMessage> Messages =>
        ProtocolResult.Messages as IReadOnlyList<PromptMessage>
        ?? ProtocolResult.Messages.ToArray();
}

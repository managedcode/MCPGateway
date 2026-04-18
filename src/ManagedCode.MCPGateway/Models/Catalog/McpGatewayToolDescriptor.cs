namespace ManagedCode.MCPGateway;

public sealed record McpGatewayToolDescriptor(
    string ToolId,
    string SourceId,
    McpGatewaySourceKind SourceKind,
    string ToolName,
    string? DisplayName,
    string Description,
    IReadOnlyList<string> RequiredArguments,
    string? InputSchemaJson
)
{
    public IReadOnlyList<string> SearchAliases { get; init; } = [];

    public IReadOnlyList<string> SearchKeywords { get; init; } = [];
}

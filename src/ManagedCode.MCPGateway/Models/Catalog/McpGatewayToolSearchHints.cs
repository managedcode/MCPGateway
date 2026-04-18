namespace ManagedCode.MCPGateway;

public sealed record McpGatewayToolSearchHints(
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? Keywords = null
)
{
    public static McpGatewayToolSearchHints Empty { get; } = new([], []);
}

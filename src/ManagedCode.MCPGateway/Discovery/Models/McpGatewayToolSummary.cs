namespace ManagedCode.MCPGateway;

public sealed record McpGatewayToolSummary(
    string ToolId,
    string SourceId,
    string Purpose,
    double Score,
    string SchemaHash,
    string Revision
)
{
    public bool? IsReadOnly { get; init; }
    public bool? IsDestructive { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public string Role { get; init; } = McpGatewayDiscoveryService.PrimaryRole;
}

public sealed record McpGatewayDiscoveryResult(
    IReadOnlyList<McpGatewayToolSummary> Tools,
    string Revision,
    string? ContinuationToken,
    int SerializedBytes
);

public sealed record McpGatewayToolSchemaResult(McpGatewayToolDescriptor Tool, string SchemaHash, string Revision);

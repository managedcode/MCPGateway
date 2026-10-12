namespace ManagedCode.MCPGateway;

public sealed class McpGatewayGovernanceOptions
{
    /// <summary>Require explicit publication, trusted context and an access policy.</summary>
    public bool ManagedCatalog { get; set; }
    public int MaximumResponseBytes { get; set; } = McpGatewayResponseOptions.DefaultMaxBytes;
}

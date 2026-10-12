namespace ManagedCode.MCPGateway;

public sealed class McpGatewayCatalogStoreOptions
{
    public const int DefaultMaxDefinitions = 10_000;
    public const int DefaultMaxBytes = 16 * 1024 * 1024;
    public int MaxDefinitions { get; set; } = DefaultMaxDefinitions;
    public int MaxBytes { get; set; } = DefaultMaxBytes;
}

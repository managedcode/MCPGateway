namespace ManagedCode.MCPGateway;

public sealed class McpGatewaySchemaTooLargeException(int actualBytes, int maximumBytes, string schemaHash,
    string revision) : InvalidOperationException(MessageText)
{
    public const string ErrorCode = "schema_too_large";
    private const string MessageText = "The complete tool schema exceeds the configured lookup byte budget.";
    public int ActualBytes { get; } = actualBytes;
    public int MaximumBytes { get; } = maximumBytes;
    public string SchemaHash { get; } = schemaHash;
    public string Revision { get; } = revision;
}

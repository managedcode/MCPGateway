namespace ManagedCode.MCPGateway;

/// <summary>Gateway-owned delivery controls, never forwarded as business arguments.</summary>
public sealed record McpGatewayResponseOptions
{
    public const int DefaultMaxBytes = 64 * 1024;
    public const int MaximumSelectPaths = 128;
    public const int MaximumPointerDepth = 64;
    public const int MaximumPointerLength = 4096;
    public IReadOnlyList<string> SelectPaths { get; init; } = [];
    public int MaxBytes { get; init; } = DefaultMaxBytes;
    public bool StoreOversizedResult { get; init; }
}

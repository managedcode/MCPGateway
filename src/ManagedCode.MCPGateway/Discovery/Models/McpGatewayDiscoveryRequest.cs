namespace ManagedCode.MCPGateway;

/// <summary>Bounded discovery of summaries; schema inspection is a separate operation.</summary>
public sealed record McpGatewayDiscoveryRequest(string? Query)
{
    public const int DefaultMaxBytes = 16 * 1024;
    public const int MinimumMaxBytes = 1024;
    public int? MaxResults { get; init; }
    public int MaxBytes { get; init; } = DefaultMaxBytes;
    public string? SourceId { get; init; }
    public string? Category { get; init; }
    public string? ContinuationToken { get; init; }
}

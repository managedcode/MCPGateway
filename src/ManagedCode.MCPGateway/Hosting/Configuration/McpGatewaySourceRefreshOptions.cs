namespace ManagedCode.MCPGateway;

public sealed class McpGatewaySourceRefreshOptions
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultMaximumBackoff = TimeSpan.FromMinutes(1);
    public const int DefaultMaxConcurrency = 4;
    /// <summary>Only these explicitly configured source IDs are refreshed by the hosted worker.</summary>
    public IList<string> SourceIds { get; } = new List<string>();
    public TimeSpan Interval { get; set; } = DefaultInterval;
    public TimeSpan InitialBackoff { get; set; } = DefaultInitialBackoff;
    public TimeSpan MaximumBackoff { get; set; } = DefaultMaximumBackoff;
    public int MaxConcurrency { get; set; } = DefaultMaxConcurrency;
}

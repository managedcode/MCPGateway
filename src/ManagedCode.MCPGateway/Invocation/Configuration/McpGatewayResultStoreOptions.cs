namespace ManagedCode.MCPGateway;

public sealed class McpGatewayResultStoreOptions
{
    public const int DefaultMaxEntries = 256;
    public const long DefaultMaxStoredBytes = 16 * 1024 * 1024;
    public const int DefaultMaxResultBytes = 4 * 1024 * 1024;
    public static TimeSpan DefaultTimeToLive { get; } = TimeSpan.FromMinutes(15);
    public static TimeSpan DefaultCleanupInterval { get; } = TimeSpan.FromMinutes(1);
    public int MaxEntries { get; set; } = DefaultMaxEntries;
    public long MaxStoredBytes { get; set; } = DefaultMaxStoredBytes;
    public int MaxResultBytes { get; set; } = DefaultMaxResultBytes;
    public TimeSpan TimeToLive { get; set; } = DefaultTimeToLive;
    public TimeSpan CleanupInterval { get; set; } = DefaultCleanupInterval;
}

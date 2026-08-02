namespace ManagedCode.MCPGateway;

public sealed class McpGatewayMcpTaskStoreOptions
{
    public static TimeSpan DefaultTaskTimeToLive { get; } = TimeSpan.FromMinutes(30);

    public static TimeSpan DefaultPollInterval { get; } = TimeSpan.FromSeconds(1);

    public const int DefaultMaximumTasks = 10_000;

    public const int DefaultMaximumConsecutiveStuckPolls = 60;

    public TimeSpan TaskTimeToLive { get; set; } = DefaultTaskTimeToLive;

    public TimeSpan PollInterval { get; set; } = DefaultPollInterval;

    public int MaximumTasks { get; set; } = DefaultMaximumTasks;

    public int MaximumConsecutiveStuckPolls { get; set; } =
        DefaultMaximumConsecutiveStuckPolls;
}

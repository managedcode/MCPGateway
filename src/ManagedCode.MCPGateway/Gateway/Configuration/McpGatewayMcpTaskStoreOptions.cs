namespace ManagedCode.MCPGateway;

public sealed class McpGatewayMcpTaskStoreOptions
{
    public static TimeSpan DefaultTaskTimeToLive { get; } = TimeSpan.FromMinutes(30);

    public static TimeSpan DefaultMaximumTaskTimeToLive { get; } = TimeSpan.FromHours(2);

    public static TimeSpan DefaultPollInterval { get; } = TimeSpan.FromSeconds(1);

    public static TimeSpan DefaultCleanupInterval { get; } = TimeSpan.FromMinutes(1);

    public const int DefaultPageSize = 100;

    public const int DefaultMaximumTasks = 10_000;

    public const int DefaultMaximumTasksPerSession = 1_000;

    public TimeSpan? TaskTimeToLive { get; set; } = DefaultTaskTimeToLive;

    public TimeSpan? MaximumTaskTimeToLive { get; set; } = DefaultMaximumTaskTimeToLive;

    public TimeSpan PollInterval { get; set; } = DefaultPollInterval;

    public TimeSpan CleanupInterval { get; set; } = DefaultCleanupInterval;

    public int PageSize { get; set; } = DefaultPageSize;

    public int? MaximumTasks { get; set; } = DefaultMaximumTasks;

    public int? MaximumTasksPerSession { get; set; } = DefaultMaximumTasksPerSession;
}

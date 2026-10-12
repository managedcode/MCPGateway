namespace ManagedCode.MCPGateway;

public sealed class McpGatewayExecutionOptions
{
    public const int DefaultConcurrency = 32;
    public const int DefaultQueueCapacity = 64;
    public const int DefaultFailureThreshold = 5;
    public static readonly TimeSpan DefaultQueueTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultCircuitBreakDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultAuditTimeout = TimeSpan.FromSeconds(30);
    public TimeSpan AuditTimeout { get; set; } = DefaultAuditTimeout;
    public bool Enabled { get; set; }
    public bool RestrictForwardedContext { get; set; }
    public bool ForwardContextSummary { get; set; }
    public IReadOnlySet<string> ForwardedContextKeys { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    internal McpGatewayExecutionOptions Snapshot()
    {
        var copy = new McpGatewayExecutionOptions
        {
            Enabled = Enabled,
            AuditTimeout = AuditTimeout,
            RestrictForwardedContext = RestrictForwardedContext,
            ForwardContextSummary = ForwardContextSummary,
            ForwardedContextKeys = ForwardedContextKeys.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
        foreach (var (id, settings) in Sources)
        {
            copy.Sources.Add(id, new()
            {
                MaxConcurrency = settings.MaxConcurrency,
                QueueCapacity = settings.QueueCapacity,
                QueueTimeout = settings.QueueTimeout,
                ExecutionTimeout = settings.ExecutionTimeout,
                FailureThreshold = settings.FailureThreshold,
                CircuitBreakDuration = settings.CircuitBreakDuration
            });
        }
        foreach (var (id, binding) in RemoteBindings)
        { copy.RemoteBindings.Add(id, binding with { RequiredScopes = binding.RequiredScopes.ToArray() }); }
        return copy;
    }
    public Dictionary<string, McpGatewaySourceExecutionOptions> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, McpGatewayRemoteBinding> RemoteBindings { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class McpGatewaySourceExecutionOptions
{
    public int MaxConcurrency { get; set; } = McpGatewayExecutionOptions.DefaultConcurrency;
    public int QueueCapacity { get; set; } = McpGatewayExecutionOptions.DefaultQueueCapacity;
    public int FailureThreshold { get; set; } = McpGatewayExecutionOptions.DefaultFailureThreshold;
    public TimeSpan QueueTimeout { get; set; } = McpGatewayExecutionOptions.DefaultQueueTimeout;
    public TimeSpan ExecutionTimeout { get; set; } = McpGatewayExecutionOptions.DefaultExecutionTimeout;
    public TimeSpan CircuitBreakDuration { get; set; } = McpGatewayExecutionOptions.DefaultCircuitBreakDuration;
}

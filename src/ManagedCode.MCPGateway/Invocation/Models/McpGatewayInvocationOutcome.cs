namespace ManagedCode.MCPGateway;

public enum McpGatewayInvocationOutcome
{
    Succeeded,
    ToolError,
    Denied,
    InvalidArguments,
    RateLimited,
    TimedOut,
    Cancelled,
    UpstreamUnavailable,
    UnknownCompletion,
    ResultRejected,
}

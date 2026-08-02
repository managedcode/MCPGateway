namespace ManagedCode.MCPGateway;

public sealed class McpGatewayStdioServerOptions
{
    public string SourceId { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public IReadOnlyList<string>? Arguments { get; set; }

    public string? WorkingDirectory { get; set; }

    public bool InheritEnvironmentVariables { get; set; } = true;

    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; set; }

    public TimeSpan? ShutdownTimeout { get; set; }

    public Action<string>? StandardErrorLines { get; set; }

    public string? DisplayName { get; set; }

    internal McpGatewayStdioServerOptions CloneWithSourceId(string sourceId) =>
        new()
        {
            SourceId = sourceId,
            Command = Command.Trim(),
            Arguments = Arguments?.ToArray(),
            WorkingDirectory = WorkingDirectory,
            InheritEnvironmentVariables = InheritEnvironmentVariables,
            EnvironmentVariables = EnvironmentVariables is null
                ? null
                : new Dictionary<string, string?>(
                    EnvironmentVariables,
                    StringComparer.OrdinalIgnoreCase
                ),
            ShutdownTimeout = ShutdownTimeout,
            StandardErrorLines = StandardErrorLines,
            DisplayName = DisplayName,
        };
}

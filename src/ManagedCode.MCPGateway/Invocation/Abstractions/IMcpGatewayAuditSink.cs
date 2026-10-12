namespace ManagedCode.MCPGateway;

public sealed record McpGatewayAuditEvent(string Operation, string AssetId, string? SourceId,
    string? DefinitionHash, McpGatewayInvocationOutcome Outcome, string? ErrorCode, int DeliveredBytes,
    double DurationMilliseconds, DateTimeOffset RecordedAt);

/// <summary>Opt-in host audit storage. Events contain no arguments, output, credentials or model context.</summary>
public interface IMcpGatewayAuditSink
{
    ValueTask RecordAsync(McpGatewayExecutionContext context, McpGatewayAuditEvent auditEvent,
        CancellationToken cancellationToken = default);
}

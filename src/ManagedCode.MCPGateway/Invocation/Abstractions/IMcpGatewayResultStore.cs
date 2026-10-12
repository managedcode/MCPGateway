using System.Text.Json;

namespace ManagedCode.MCPGateway;

/// <summary>Stores approved results; opaque handles alone never confer read access.</summary>
public interface IMcpGatewayResultStore
{
    ValueTask<McpGatewayStoredResult?> StoreAsync(McpGatewayExecutionContext context, JsonElement result, CancellationToken cancellationToken = default);
    ValueTask<McpGatewayStoredResult?> StoreAsync(McpGatewayExecutionContext context, JsonElement result,
        McpGatewayToolDescriptor tool, CancellationToken cancellationToken = default) => tool.RequiresPublication
            ? ValueTask.FromResult<McpGatewayStoredResult?>(null) : StoreAsync(context, result, cancellationToken);
    ValueTask<McpGatewayStoredResult?> StoreAsync(McpGatewayExecutionContext context, JsonElement result,
        McpGatewayToolDescriptor tool, int maximumDeliveryBytes, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<McpGatewayStoredResult?>(null);
    ValueTask<McpGatewayStoredResult?> ReadAsync(McpGatewayExecutionContext context, string handle, CancellationToken cancellationToken = default);
    ValueTask<bool> RemoveAsync(McpGatewayExecutionContext context, string handle, CancellationToken cancellationToken = default);
}

public sealed record McpGatewayStoredResult(string Handle, JsonElement Result, DateTimeOffset ExpiresAt)
{
    public string? ToolId { get; init; }
    public string? SourceId { get; init; }
    public string? DefinitionHash { get; init; }
    public bool RequiresPublication { get; init; }
    public int? MaximumDeliveryBytes { get; init; }
}

public interface IMcpGatewayResultReader
{
    Task<McpGatewayStoredResult?> ReadAsync(string handle, McpGatewayResponseOptions? response = null, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(string handle, CancellationToken cancellationToken = default);
}

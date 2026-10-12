namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayCatalogRuntime
{
    /// <summary>Reload one source while retaining unchanged source connections and metadata.</summary>
    ValueTask RefreshSourceAsync(string sourceId, CancellationToken cancellationToken = default);

    ValueTask ClearAsync(CancellationToken cancellationToken = default);

    ValueTask ReconfigureAsync(
        McpGatewayOptions configuration,
        CancellationToken cancellationToken = default
    );
}

namespace ManagedCode.MCPGateway.Abstractions;

public interface IMcpGatewayCatalogRuntime
{
    ValueTask ClearAsync(CancellationToken cancellationToken = default);

    ValueTask ReconfigureAsync(
        McpGatewayOptions configuration,
        CancellationToken cancellationToken = default
    );
}

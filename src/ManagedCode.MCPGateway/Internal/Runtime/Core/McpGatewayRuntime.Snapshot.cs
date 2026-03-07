namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private async Task<ToolCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var registrySnapshot = _catalogSource.CreateSnapshot();
            lock (_stateGate)
            {
                if (_snapshotVersion == registrySnapshot.Version)
                {
                    return _snapshot;
                }
            }

            await BuildIndexAsync(cancellationToken);
        }
    }
}

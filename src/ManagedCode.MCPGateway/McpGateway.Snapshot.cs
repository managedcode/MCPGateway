namespace ManagedCode.MCPGateway;

public sealed partial class McpGateway
{
    private async Task<ToolCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var registrySnapshot = _registry.CreateSnapshot();
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

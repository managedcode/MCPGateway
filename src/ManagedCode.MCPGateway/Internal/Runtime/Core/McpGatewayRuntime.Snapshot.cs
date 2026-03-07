namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private async Task<ToolCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ThrowIfDisposed();
            var registrySnapshot = _catalogSource.CreateSnapshot();
            var state = Volatile.Read(ref _state);
            if (state.SnapshotVersion == registrySnapshot.Version)
            {
                return state.Snapshot;
            }

            await BuildIndexAsync(cancellationToken);
        }
    }
}

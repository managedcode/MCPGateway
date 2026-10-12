namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private async Task<McpGatewayCatalogSourceSnapshot> AcquireCatalogLeaseAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var lease = _catalogSource.CreateSnapshot();
            try
            {
                var snapshot = await GetSnapshotAsync(cancellationToken);
                if (snapshot.Version == lease.Version && ReferenceEquals(snapshot.SourceOwner, lease.Owner))
                {
                    return lease;
                }
            }
            catch
            {
                lease.Dispose();
                throw;
            }
            lease.Dispose();
        }
        throw new OperationCanceledException(cancellationToken);
    }

    private async Task<ToolCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (!cancellationToken.IsCancellationRequested)
        {
            ThrowIfDisposed();
            using var registrySnapshot = _catalogSource.CreateSnapshot();
            var state = Volatile.Read(ref _state);
            if (state.SnapshotVersion == registrySnapshot.Version && ReferenceEquals(state.Snapshot.SourceOwner, registrySnapshot.Owner))
            {
                return state.Snapshot;
            }

            await EnsureIndexAsync(cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }
}

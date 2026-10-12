namespace ManagedCode.MCPGateway;

internal interface IMcpGatewayCatalogStagingSource
{
    void SetCandidateValidator(Func<McpGatewayCatalogSourceSnapshot, CancellationToken, Task<McpGatewayStagedCatalog>> validator);
}

internal sealed class McpGatewayStagedCatalog(Action publish, Func<Task> discard) : IAsyncDisposable
{
    private int _published;
    public void Publish()
    {
        if (Interlocked.Exchange(ref _published, 1) == 0)
        {
            publish();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _published) == 0)
        {
            await discard();
        }
    }
}

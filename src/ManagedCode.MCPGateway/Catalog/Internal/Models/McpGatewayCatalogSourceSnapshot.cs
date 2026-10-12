namespace ManagedCode.MCPGateway;

internal sealed record McpGatewayCatalogSourceSnapshot(int Version,
    IReadOnlyList<McpGatewayToolSourceRegistration> Registrations) : IDisposable
{
    public string? RefreshSourceId { get; init; }
    public object? Owner { get; init; }
    internal Action? Release { get; init; }
    private int _disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Release?.Invoke();
        }
    }
}

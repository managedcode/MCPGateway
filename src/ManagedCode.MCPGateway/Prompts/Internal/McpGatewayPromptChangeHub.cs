namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayPromptChangeHub
{
    private event Action? Changed;

    public IDisposable Subscribe(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        Changed += handler;
        return new Subscription(this, handler);
    }

    public void NotifyChanged() => Changed?.Invoke();

    private sealed class Subscription(McpGatewayPromptChangeHub owner, Action handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            owner.Changed -= handler;
        }
    }
}

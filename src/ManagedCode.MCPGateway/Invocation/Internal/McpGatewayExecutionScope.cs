namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayExecutionScope
{
    private readonly AsyncLocal<Frame?> _current = new();
    public Frame? Current => _current.Value;
    public IDisposable Push(McpGatewayExecutionContext context, IServiceProvider services, string? assetId, string? definitionHash)
    {
        var previous = _current.Value;
        var frame = new Frame(context, services, assetId, definitionHash);
        _current.Value = frame;
        return new Pop(_current, previous, frame);
    }
    internal sealed record Frame(McpGatewayExecutionContext Context, IServiceProvider Services, string? AssetId, string? DefinitionHash)
    {
        private int _active = 1;
        public bool IsActive { get => Volatile.Read(ref _active) != 0; set => Volatile.Write(ref _active, value ? 1 : 0); }
    }
    private sealed class Pop(AsyncLocal<Frame?> current, Frame? previous, Frame frame) : IDisposable
    {
        public void Dispose()
        {
            frame.IsActive = false;
            current.Value = previous;
        }
    }
}

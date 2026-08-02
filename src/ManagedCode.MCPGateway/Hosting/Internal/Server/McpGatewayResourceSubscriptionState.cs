namespace ManagedCode.MCPGateway;

internal sealed record McpGatewayResourceSubscriptionKey(
    string ServerId,
    string ListenerId,
    string ExposedUri
)
{
    public static McpGatewayResourceSubscriptionKey Create(
        ModelContextProtocol.Server.McpServer server,
        string ownerId,
        string exposedUri
    ) => new(McpGatewayMcpServerIdentity.GetInstanceId(server), ownerId, exposedUri);
}

internal sealed class McpGatewayResourceSubscriptionState(
    ModelContextProtocol.Server.McpServer downstreamServer
)
{
    private const int RetiredMask = int.MinValue;
    private const int ReferenceMask = int.MaxValue;
    private int _lifecycle;

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public ModelContextProtocol.Server.McpServer DownstreamServer { get; set; } = downstreamServer;

    public IAsyncDisposable? Subscription { get; set; }

    public bool HasPinnedBinding { get; set; }

    public TaskCompletionSource? ActiveAttempt { get; set; }

    public bool IsInactive => Subscription is null && !HasPinnedBinding && ActiveAttempt is null;

    public bool IsRetired => Volatile.Read(ref _lifecycle) < 0;

    public bool TryRetain()
    {
        var lifecycle = Volatile.Read(ref _lifecycle);
        while (lifecycle >= 0)
        {
            if ((lifecycle & ReferenceMask) == ReferenceMask)
            {
                throw new InvalidOperationException(
                    "The MCP resource subscription reference limit was reached."
                );
            }

            var retained = Interlocked.CompareExchange(
                ref _lifecycle,
                lifecycle + 1,
                lifecycle
            );
            if (retained == lifecycle)
            {
                return true;
            }

            lifecycle = retained;
        }

        return false;
    }

    public bool ReleaseReference()
    {
        var lifecycle = Volatile.Read(ref _lifecycle);
        while ((lifecycle & ReferenceMask) > 0)
        {
            var references = lifecycle & ReferenceMask;
            var released = (lifecycle & RetiredMask) | (references - 1);
            var observed = Interlocked.CompareExchange(
                ref _lifecycle,
                released,
                lifecycle
            );
            if (observed == lifecycle)
            {
                return released == RetiredMask;
            }

            lifecycle = observed;
        }

        throw new InvalidOperationException(
            "The MCP resource subscription has no retained reference to release."
        );
    }

    public bool MarkRetired()
    {
        var lifecycle = Volatile.Read(ref _lifecycle);
        while (lifecycle >= 0)
        {
            var retired = lifecycle | RetiredMask;
            var observed = Interlocked.CompareExchange(
                ref _lifecycle,
                retired,
                lifecycle
            );
            if (observed == lifecycle)
            {
                return lifecycle == 0;
            }

            lifecycle = observed;
        }

        return false;
    }

    public void DisposeGate() => Gate.Dispose();
}

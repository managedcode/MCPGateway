using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using ManagedCode.MCPGateway.Abstractions;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway;

internal sealed record McpGatewayPromptNotificationKey(string ServerId, string ListenerId)
{
    public static McpGatewayPromptNotificationKey Create(
        ModelContextProtocol.Server.McpServer server,
        string ownerId
    ) => new(McpGatewayMcpServerIdentity.GetInstanceId(server), ownerId);
}

internal sealed class McpGatewayPromptNotificationState(
    McpGatewayPromptNotificationKey key,
    ModelContextProtocol.Server.McpServer downstreamServer,
    IMcpGatewayServerBinding binding,
    IDisposable promptChangeSubscription,
    RequestId subscriptionId,
    McpGatewaySubscriptionDeliveryGate deliveryGate
)
{
    private int _disposed;

    public McpGatewayPromptNotificationKey Key { get; } = key;

    public ModelContextProtocol.Server.McpServer DownstreamServer { get; set; } = downstreamServer;

    public IMcpGatewayServerBinding Binding { get; } = binding;

    public IDisposable PromptChangeSubscription { get; } = promptChangeSubscription;

    public RequestId SubscriptionId { get; } = subscriptionId;

    public McpGatewaySubscriptionDeliveryGate DeliveryGate { get; } = deliveryGate;

    public ConcurrentDictionary<
        string,
        McpGatewayPromptUpstreamSubscription
    > UpstreamSubscriptions
    { get; } = new(StringComparer.Ordinal);

    public bool TryBeginDispose() => Interlocked.Exchange(ref _disposed, 1) == 0;
}

internal sealed record McpGatewayPromptUpstreamSubscription(
    IMcpGatewayServerSource Source,
    IAsyncDisposable Subscription
);

internal sealed class McpGatewayPromptNotificationStore(
    McpGatewayMcpServerBindingManager bindingManager
) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<
        McpGatewayPromptNotificationKey,
        McpGatewayPromptNotificationState
    > _listeners = new();
    private int _disposed;

    public int Count => _listeners.Count;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public bool TryGet(
        McpGatewayPromptNotificationKey key,
        out McpGatewayPromptNotificationState state
    ) => _listeners.TryGetValue(key, out state!);

    public bool TryAdd(
        McpGatewayPromptNotificationKey key,
        McpGatewayPromptNotificationState state
    ) => _listeners.TryAdd(key, state);

    public bool TryRemove(
        McpGatewayPromptNotificationKey key,
        out McpGatewayPromptNotificationState state
    ) => _listeners.TryRemove(key, out state!);

    public bool TryRemove(
        McpGatewayPromptNotificationKey key,
        McpGatewayPromptNotificationState state
    ) =>
        _listeners.TryRemove(
            new KeyValuePair<
                McpGatewayPromptNotificationKey,
                McpGatewayPromptNotificationState
            >(key, state)
        );

    public bool IsCurrent(McpGatewayPromptNotificationState state) =>
        _listeners.TryGetValue(state.Key, out var activeListener)
        && ReferenceEquals(activeListener, state);

    public async ValueTask RemoveAsync(McpGatewayPromptNotificationKey key)
    {
        if (_listeners.TryRemove(key, out var state))
        {
            await DisposeStateAsync(state);
        }
    }

    public async ValueTask DisposeStateAsync(McpGatewayPromptNotificationState state)
    {
        var cleanupExceptions = new List<Exception>();
        await DisposeStateAsync(state, cleanupExceptions);
        ThrowIfCleanupFailed(cleanupExceptions);
    }

    public async ValueTask DisposeStateAsync(
        McpGatewayPromptNotificationState state,
        List<Exception> cleanupExceptions
    )
    {
        if (!state.TryBeginDispose())
        {
            return;
        }

        try
        {
            state.PromptChangeSubscription.Dispose();
        }
        catch (Exception exception)
        {
            cleanupExceptions.Add(exception);
        }

        foreach (var (_, subscription) in state.UpstreamSubscriptions)
        {
            try
            {
                await subscription.Subscription.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }
        }

        state.UpstreamSubscriptions.Clear();
        try
        {
            await bindingManager.ReleaseAsync(state.DownstreamServer);
        }
        catch (Exception exception)
        {
            cleanupExceptions.Add(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var states = _listeners.ToArray();
        _listeners.Clear();
        var cleanupExceptions = new List<Exception>();

        foreach (var (_, state) in states)
        {
            await DisposeStateAsync(state, cleanupExceptions);
        }

        ThrowIfCleanupFailed(cleanupExceptions);
    }

    public void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private static void ThrowIfCleanupFailed(List<Exception> cleanupExceptions)
    {
        switch (cleanupExceptions.Count)
        {
            case 0:
                return;
            case 1:
                ExceptionDispatchInfo.Capture(cleanupExceptions[0]).Throw();
                break;
            default:
                throw new AggregateException(cleanupExceptions);
        }
    }
}

using System.Collections.Concurrent;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayResourceSubscriptionRegistry
{
    private readonly ConcurrentDictionary<
        McpGatewayResourceSubscriptionKey,
        McpGatewayResourceSubscriptionState
    > _subscriptions = new();
    private int _disposed;

    public int Count => _subscriptions.Count;

    public McpGatewayResourceSubscriptionState GetOrAdd(
        McpGatewayResourceSubscriptionKey key,
        ModelContextProtocol.Server.McpServer downstreamServer
    )
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            if (_subscriptions.TryGetValue(key, out var existingState))
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    break;
                }

                if (existingState.TryRetain())
                {
                    return existingState;
                }

                _subscriptions.TryRemove(
                    new KeyValuePair<
                        McpGatewayResourceSubscriptionKey,
                        McpGatewayResourceSubscriptionState
                    >(key, existingState)
                );
                continue;
            }

            var createdState = new McpGatewayResourceSubscriptionState(downstreamServer);
            if (!_subscriptions.TryAdd(key, createdState))
            {
                createdState.DisposeGate();
                continue;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                RetireAndRemove(key, createdState);
                break;
            }

            if (createdState.TryRetain())
            {
                return createdState;
            }

            _subscriptions.TryRemove(
                new KeyValuePair<
                    McpGatewayResourceSubscriptionKey,
                    McpGatewayResourceSubscriptionState
                >(key, createdState)
            );
        }

        ThrowIfDisposed();
        throw new InvalidOperationException("The MCP resource subscription state could not be created.");
    }

    public bool TryGet(
        McpGatewayResourceSubscriptionKey key,
        out McpGatewayResourceSubscriptionState state
    )
    {
        while (_subscriptions.TryGetValue(key, out var existingState))
        {
            if (existingState.TryRetain())
            {
                state = existingState;
                return true;
            }

            _subscriptions.TryRemove(
                new KeyValuePair<
                    McpGatewayResourceSubscriptionKey,
                    McpGatewayResourceSubscriptionState
                >(key, existingState)
            );
        }

        state = null!;
        return false;
    }

    public bool RetireIfInactive(
        McpGatewayResourceSubscriptionKey key,
        McpGatewayResourceSubscriptionState state
    )
    {
        if (!state.IsInactive)
        {
            return false;
        }

        _ = state.MarkRetired();
        return _subscriptions.TryRemove(
            new KeyValuePair<
                McpGatewayResourceSubscriptionKey,
                McpGatewayResourceSubscriptionState
            >(key, state)
        );
    }

    public bool TryRemove(
        McpGatewayResourceSubscriptionKey key,
        McpGatewayResourceSubscriptionState state
    ) =>
        _subscriptions.TryRemove(
            new KeyValuePair<
                McpGatewayResourceSubscriptionKey,
                McpGatewayResourceSubscriptionState
            >(key, state)
        );

    public bool TryBeginDispose(
        out KeyValuePair<
            McpGatewayResourceSubscriptionKey,
            McpGatewayResourceSubscriptionState
        >[] subscriptions
    )
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            subscriptions = [];
            return false;
        }

        subscriptions = _subscriptions.ToArray();
        _subscriptions.Clear();
        return true;
    }

    public static void Release(McpGatewayResourceSubscriptionState state)
    {
        if (state.ReleaseReference())
        {
            state.DisposeGate();
        }
    }

    private void RetireAndRemove(
        McpGatewayResourceSubscriptionKey key,
        McpGatewayResourceSubscriptionState state
    )
    {
        var disposeGate = state.MarkRetired();
        _subscriptions.TryRemove(
            new KeyValuePair<
                McpGatewayResourceSubscriptionKey,
                McpGatewayResourceSubscriptionState
            >(key, state)
        );
        if (disposeGate)
        {
            state.DisposeGate();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

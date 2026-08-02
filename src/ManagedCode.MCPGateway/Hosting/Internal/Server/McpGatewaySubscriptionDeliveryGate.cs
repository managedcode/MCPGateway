using System.Collections.Concurrent;

namespace ManagedCode.MCPGateway;

internal sealed class McpGatewaySubscriptionDeliveryGate : IDisposable
{
    private readonly ConcurrentDictionary<
        string,
        Func<CancellationToken, ValueTask>
    > _pendingDeliveries = new(StringComparer.Ordinal);
    private int _state;

    public ValueTask DeliverAsync(
        string key,
        Func<CancellationToken, ValueTask> delivery,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(delivery);

        var state = Volatile.Read(ref _state);
        if (state == 1)
        {
            return delivery(cancellationToken);
        }

        if (state == 2)
        {
            return ValueTask.CompletedTask;
        }

        _pendingDeliveries[key] = delivery;
        if (
            Volatile.Read(ref _state) == 1
            && _pendingDeliveries.TryRemove(key, out var pendingDelivery)
        )
        {
            return pendingDelivery(cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            return;
        }

        foreach (var (key, _) in _pendingDeliveries.ToArray())
        {
            if (_pendingDeliveries.TryRemove(key, out var delivery))
            {
                await delivery(cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _state, 2);
        _pendingDeliveries.Clear();
    }
}

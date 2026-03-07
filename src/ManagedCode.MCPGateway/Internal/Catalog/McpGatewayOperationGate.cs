namespace ManagedCode.MCPGateway;

internal sealed class McpGatewayOperationGate
{
    private int _disposed;
    private int _activeOperations;
    private TaskCompletionSource<object?>? _operationsDrainedSignal;

    public void ThrowIfDisposed(object owner)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, owner);
    }

    public void Enter(object owner)
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            ThrowIfDisposed(owner);
            Interlocked.Increment(ref _activeOperations);
            if (Volatile.Read(ref _disposed) == 0)
            {
                return;
            }

            Exit();
        }

        ThrowIfDisposed(owner);
    }

    public void Exit()
    {
        if (Interlocked.Decrement(ref _activeOperations) == 0)
        {
            Volatile.Read(ref _operationsDrainedSignal)?.TrySetResult(null);
        }
    }

    public bool TryStartDispose(out ValueTask waitForDrain)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            waitForDrain = ValueTask.CompletedTask;
            return false;
        }

        var operationsDrainedSignal = EnsureOperationsDrainedSignal();
        waitForDrain = Volatile.Read(ref _activeOperations) > 0
            ? new ValueTask(operationsDrainedSignal.Task)
            : ValueTask.CompletedTask;
        return true;
    }

    private TaskCompletionSource<object?> EnsureOperationsDrainedSignal()
    {
        var operationsDrainedSignal = Volatile.Read(ref _operationsDrainedSignal);
        while (operationsDrainedSignal is null)
        {
            var created = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _operationsDrainedSignal, created, null) is null)
            {
                operationsDrainedSignal = created;
                break;
            }

            operationsDrainedSignal = Volatile.Read(ref _operationsDrainedSignal);
        }

        if (Volatile.Read(ref _activeOperations) == 0)
        {
            operationsDrainedSignal.TrySetResult(null);
        }

        return operationsDrainedSignal;
    }
}

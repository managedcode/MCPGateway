using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ManagedCode.MCPGateway;

internal sealed partial class McpGatewayRuntime
{
    private readonly ConcurrentDictionary<SnapshotOwnership, byte> _ownedSnapshots = new();

    private sealed class SnapshotOwnership(ToolGraphSearchIndex? index)
    {
        private readonly McpGatewayOperationGate _readers = new();
        private readonly TaskCompletionSource _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? TryAcquire()
        {
            try
            {
                _readers.Enter(this);
                return new Reader(_readers);
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
        public Task RetireAsync()
        {
            if (_readers.TryStartDispose(out var drain))
            {
                _ = DisposeAfterDrainAsync(drain);
            }
            return _retired.Task;
        }
        private async Task DisposeAfterDrainAsync(ValueTask drain)
        {
            try
            {
                await drain;
                try
                {
                    if (index?.FullText.IsValueCreated is true)
                    { (await index.FullText.Value).Dispose(); }
                }
                finally { index?.Graph.Dispose(); }
                _retired.SetResult();
            }
            catch (Exception exception)
            {
                _retired.SetException(exception);
            }
        }
        private sealed class Reader(McpGatewayOperationGate gate) : IDisposable
        {
            private int _released;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    gate.Exit();
                }
            }
        }
    }

    private async Task RetireSnapshotAsync(SnapshotOwnership owner)
    {
        try
        {
            await owner.RetireAsync();
        }
        catch (Exception exception) when (!McpGatewayRuntimeFailureClassifier.HasFatalFailure(exception))
        {
            _logger.LogError(exception, "Gateway snapshot disposal failed.");
        }
        finally
        {
            _ownedSnapshots.TryRemove(owner, out _);
        }
    }
}

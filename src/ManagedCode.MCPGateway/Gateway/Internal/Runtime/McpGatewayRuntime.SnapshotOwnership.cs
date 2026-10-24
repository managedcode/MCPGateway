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
        /// <summary>
        /// Releases the graph view after its readers drain. <paramref name="keepForReuse"/> lets the last release of
        /// a pooled graph stay idle for an identical catalog; a superseded graph is retired instead.
        /// </summary>
        public Task RetireAsync(bool keepForReuse)
        {
            if (_readers.TryStartDispose(out var drain))
            {
                _ = ReleaseAfterDrainAsync(drain, keepForReuse);
            }
            return _retired.Task;
        }
        private async Task ReleaseAfterDrainAsync(ValueTask drain, bool keepForReuse)
        {
            try
            {
                await drain;
                if (index is not null)
                {
                    await index.ReleaseAsync(keepForReuse);
                }
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

    private async Task RetireSnapshotAsync(SnapshotOwnership owner, bool keepForReuse)
    {
        try
        {
            await owner.RetireAsync(keepForReuse);
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

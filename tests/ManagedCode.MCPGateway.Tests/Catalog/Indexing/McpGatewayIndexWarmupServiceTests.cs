using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayIndexWarmupServiceTests
{
    [Test]
    public async Task StopAsync_DoesNothingWhenWarmupHasNotStarted()
    {
        var gateway = new ThrowingWarmupGateway(static _ => throw new InvalidOperationException("boom"));
        var service = new McpGatewayIndexWarmupService(
            gateway,
            NullLogger<McpGatewayIndexWarmupService>.Instance
        );

        await service.StopAsync(CancellationToken.None);

        await Assert.That(gateway.BuildIndexCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task StartAsync_AbsorbsGatewayFailuresAndCancellation()
    {
        var exceptionGateway = new ThrowingWarmupGateway(
            static _ => throw new InvalidOperationException("boom")
        );
        var exceptionService = new McpGatewayIndexWarmupService(
            exceptionGateway,
            NullLogger<McpGatewayIndexWarmupService>.Instance
        );
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var cancellationGateway = new ThrowingWarmupGateway(
            static token => token.ThrowIfCancellationRequested()
        );
        var cancellationService = new McpGatewayIndexWarmupService(
            cancellationGateway,
            NullLogger<McpGatewayIndexWarmupService>.Instance
        );

        await exceptionService.StartAsync(CancellationToken.None);
        await exceptionService.StopAsync(CancellationToken.None);
        await cancellationService.StartAsync(cancellationSource.Token);
        await cancellationService.StopAsync(cancellationSource.Token);

        await Assert.That(exceptionGateway.BuildIndexCallCount).IsEqualTo(1);
        await Assert.That(cancellationGateway.BuildIndexCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task StopAsync_CancelsRunningWarmup()
    {
        var gateway = new BlockingWarmupGateway();
        var service = new McpGatewayIndexWarmupService(
            gateway,
            NullLogger<McpGatewayIndexWarmupService>.Instance
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StartAsync(CancellationToken.None);
        await gateway.BuildStarted.WaitAsync(timeout.Token);
        await service.StopAsync(timeout.Token);
        await gateway.BuildCanceled.WaitAsync(timeout.Token);

        await Assert.That(gateway.BuildIndexCallCount).IsEqualTo(1);
    }

    private sealed class ThrowingWarmupGateway(Action<CancellationToken> onBuild) : IMcpGateway
    {
        private int _buildIndexCallCount;

        public int BuildIndexCallCount => Volatile.Read(ref _buildIndexCallCount);

        public Task<McpGatewayIndexBuildResult> BuildIndexAsync(
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _buildIndexCallCount);
            onBuild(cancellationToken);
            return Task.FromResult(new McpGatewayIndexBuildResult(0, 0, false, []));
        }

        public Task<IReadOnlyList<McpGatewayToolDescriptor>> ListToolsAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<McpGatewayToolDescriptor>>([]);

        public Task<McpGatewaySearchResult> SearchAsync(
            string? query,
            int? maxResults = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<McpGatewaySearchResult> SearchAsync(
            McpGatewaySearchRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<McpGatewayToolRouteResult> RouteToolsAsync(
            McpGatewayToolRouteRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<McpGatewayInvokeResult> InvokeAsync(
            McpGatewayInvokeRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public IReadOnlyList<Microsoft.Extensions.AI.AITool> CreateMetaTools(
            string searchToolName = McpGatewayToolSet.DefaultSearchToolName,
            string routeToolName = McpGatewayToolSet.DefaultRouteToolName,
            string invokeToolName = McpGatewayToolSet.DefaultInvokeToolName
        ) => [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingWarmupGateway : IMcpGateway
    {
        private readonly TaskCompletionSource<object?> _buildCanceled = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource<object?> _buildStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _buildIndexCallCount;

        public int BuildIndexCallCount => Volatile.Read(ref _buildIndexCallCount);

        public Task BuildCanceled => _buildCanceled.Task;

        public Task BuildStarted => _buildStarted.Task;

        public async Task<McpGatewayIndexBuildResult> BuildIndexAsync(
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref _buildIndexCallCount);
            _buildStarted.TrySetResult(null);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _buildCanceled.TrySetResult(null);
                throw;
            }

            return new McpGatewayIndexBuildResult(0, 0, false, []);
        }

        public Task<IReadOnlyList<McpGatewayToolDescriptor>> ListToolsAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<McpGatewayToolDescriptor>>([]);

        public Task<McpGatewaySearchResult> SearchAsync(
            string? query,
            int? maxResults = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<McpGatewaySearchResult> SearchAsync(
            McpGatewaySearchRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<McpGatewayToolRouteResult> RouteToolsAsync(
            McpGatewayToolRouteRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public Task<McpGatewayInvokeResult> InvokeAsync(
            McpGatewayInvokeRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public IReadOnlyList<Microsoft.Extensions.AI.AITool> CreateMetaTools(
            string searchToolName = McpGatewayToolSet.DefaultSearchToolName,
            string routeToolName = McpGatewayToolSet.DefaultRouteToolName,
            string invokeToolName = McpGatewayToolSet.DefaultInvokeToolName
        ) => [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

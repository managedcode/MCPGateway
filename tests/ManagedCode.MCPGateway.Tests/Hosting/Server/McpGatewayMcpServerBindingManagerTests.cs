using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerBindingManagerTests
{
    [Test]
    public async Task PinAsync_CancelledWaiterDoesNotDisposeSharedBindingOrLeakReference()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();

        var firstBinding = new TrackingBinding("first");
        var secondBinding = new TrackingBinding("second");
        var resolver = new SequencedBindingResolver();
        var firstResolution = resolver.Enqueue();
        var secondResolution = resolver.Enqueue();
        var manager = new McpGatewayMcpServerBindingManager(resolver);
        using var firstCancellation = new CancellationTokenSource();

        var firstWaiter = manager
            .PinAsync(
                requestServices: null,
                serviceProvider,
                gatewayServer.Server,
                firstCancellation.Token
            )
            .AsTask();
        await resolver.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondWaiter = manager
            .PinAsync(requestServices: null, serviceProvider, gatewayServer.Server, CancellationToken.None)
            .AsTask();

        firstCancellation.Cancel();
        var cancelled = await CaptureAsync(firstWaiter);
        firstResolution.SetResult(firstBinding);

        await using var secondLease = await secondWaiter.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cancelled).IsTypeOf<OperationCanceledException>();
        await Assert.That(firstBinding.DisposeCount).IsEqualTo(0);

        await manager.ReleaseAsync(gatewayServer.Server);
        await Assert.That(firstBinding.DisposeCount).IsEqualTo(1);

        secondResolution.SetResult(secondBinding);
        await using var thirdLease = await manager.AcquireAsync(
            requestServices: null,
            serviceProvider,
            gatewayServer.Server,
            CancellationToken.None
        );

        await Assert.That(ReferenceEquals(thirdLease.Binding, secondBinding)).IsTrue();
    }

    [Test]
    public async Task ReleaseAsync_PropagatesResolvedBindingDisposeFailure()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();

        var binding = new TrackingBinding(
            "throwing",
            new InvalidOperationException("binding dispose failure")
        );
        var resolver = new SequencedBindingResolver();
        resolver.Enqueue().SetResult(binding);
        var manager = new McpGatewayMcpServerBindingManager(resolver);

        await using var lease = await manager.PinAsync(
            requestServices: null,
            serviceProvider,
            gatewayServer.Server,
            CancellationToken.None
        );

        var exception = await CaptureAsync(manager.ReleaseAsync(gatewayServer.Server).AsTask());

        await Assert.That(exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains("binding dispose failure");
        await Assert.That(binding.DisposeCount).IsEqualTo(1);
    }

    private static async Task<Exception?> CaptureAsync(Task action)
    {
        try
        {
            await action;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class SequencedBindingResolver : IMcpGatewayServerBindingResolver
    {
        private readonly Queue<TaskCompletionSource<IMcpGatewayServerBinding>> _resolutions = [];

        public TaskCompletionSource<bool> FirstCallStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource<IMcpGatewayServerBinding> Enqueue()
        {
            var resolution = new TaskCompletionSource<IMcpGatewayServerBinding>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _resolutions.Enqueue(resolution);
            return resolution;
        }

        public async ValueTask<IMcpGatewayServerBinding> ResolveAsync(
            IServiceProvider? requestServices,
            IServiceProvider serverServices,
            ModelContextProtocol.Server.McpServer server,
            CancellationToken cancellationToken = default
        )
        {
            FirstCallStarted.TrySetResult(true);
            var resolution = _resolutions.Dequeue();
            return await resolution.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class TrackingBinding(string name, Exception? disposeException = null)
        : IMcpGatewayServerBinding
    {
        public int DisposeCount { get; private set; }

        public IMcpGateway Gateway => throw new NotSupportedException(name);

        public IMcpGatewayPromptCatalog PromptCatalog => throw new NotSupportedException(name);

        public IMcpGatewayResourceCatalog ResourceCatalog => throw new NotSupportedException(name);

        public IMcpGatewayRegistry Registry => throw new NotSupportedException(name);

        public IDisposable SubscribeToPromptListChanges(Action onChanged) => new NoOpDisposable();

        public ValueTask<IReadOnlyList<IMcpGatewayServerSource>> ListSourcesAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<IReadOnlyList<IMcpGatewayServerSource>>([]);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return disposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(disposeException);
        }

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

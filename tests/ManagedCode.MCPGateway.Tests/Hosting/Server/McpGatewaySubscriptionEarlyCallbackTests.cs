using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewaySubscriptionEarlyCallbackTests
{
    [Test]
    public async Task SubscribeToResourceAsync_DoesNotDeadlockWhenSourceCallbackFailsDuringSubscribe()
    {
        var source = new EarlyResourceUpdateSource("source-a");
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(
            static _ => { },
            services =>
                services.AddSingleton<IMcpGatewayServerBindingResolver>(
                    new SingleSourceServerBindingResolver(source, CreateResourceCatalog())
                )
        );

        await gatewayServer.Client.SubscribeToResourceAsync("docs://overview").WaitAsync(
            TimeSpan.FromSeconds(5)
        );

        await WaitUntilAsync(() => source.DisposedSubscriptionCount == 1);
    }

    [Test]
    public async Task ListPromptsAsync_DisposesPromptSubscriptionReturnedAfterSessionRemoval()
    {
        var source = new EarlyPromptListChangeSource("source-a");
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(
            static _ => { },
            services =>
                services.AddSingleton<IMcpGatewayServerBindingResolver>(
                    new SingleSourceServerBindingResolver(
                        source,
                        new StaticMcpGatewayResourceCatalog([])
                    )
                )
        );

        var listPromptsTask = gatewayServer.Client.ListPromptsAsync().AsTask();
        _ = await listPromptsTask.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => source.DisposedSubscriptionCount == 1);
    }

    [Test]
    public async Task DisposeAsync_ReleasesPromptBindingWhenLocalSubscriptionDisposeFails()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var bindingDisposeCount = 0;
        var resolver = new SingleSourceServerBindingResolver(
            new EmptySource("source-a"),
            new StaticMcpGatewayResourceCatalog([]),
            subscribeToPromptListChanges: static _ => new ThrowingDisposable(),
            onDisposed: () => Interlocked.Increment(ref bindingDisposeCount)
        );
        var manager = new McpGatewayPromptListNotificationManager(
            new McpGatewayMcpServerBindingManager(resolver),
            serviceProvider,
            NullLogger<McpGatewayPromptListNotificationManager>.Instance,
            NullLoggerFactory.Instance
        );

        await manager.RegisterDownstreamServerAsync(
            requestServices: null,
            gatewayServer.Server,
            CancellationToken.None
        );

        Exception? exception = null;
        try
        {
            await manager.DisposeAsync();
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains("prompt subscription dispose failure");
        await Assert.That(bindingDisposeCount).IsEqualTo(1);
    }

    private static StaticMcpGatewayResourceCatalog CreateResourceCatalog() =>
        new(
            [
                new McpGatewayResourceDescriptor(
                    "source-a",
                    McpGatewaySourceKind.Local,
                    "overview",
                    "overview",
                    "docs://overview",
                    "Reads overview.",
                    "text/plain",
                    null
                ),
            ]
        );

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= timeoutAt)
            {
                throw new TimeoutException("Condition was not satisfied within five seconds.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    private sealed class EarlyResourceUpdateSource(string sourceId)
        : TestMcpGatewayServerSource(sourceId)
    {
        private int _disposedSubscriptionCount;

        public int DisposedSubscriptionCount => Volatile.Read(ref _disposedSubscriptionCount);

        public override async Task<IAsyncDisposable?> SubscribeToResourceAsync(
            string resourceUri,
            Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        )
        {
            _ = loggerFactory;
            cancellationToken.ThrowIfCancellationRequested();

            using var cancellationSource = new CancellationTokenSource();
            await cancellationSource.CancelAsync();
            await onUpdated(
                new ResourceUpdatedNotificationParams { Uri = resourceUri },
                cancellationSource.Token
            );

            return new CountingAsyncDisposable(() =>
                Interlocked.Increment(ref _disposedSubscriptionCount)
            );
        }
    }

    private sealed class EarlyPromptListChangeSource(string sourceId)
        : TestMcpGatewayServerSource(sourceId)
    {
        private int _disposedSubscriptionCount;

        public int DisposedSubscriptionCount => Volatile.Read(ref _disposedSubscriptionCount);

        public override async Task<IAsyncDisposable?> SubscribeToPromptListChangesAsync(
            Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        )
        {
            _ = loggerFactory;
            cancellationToken.ThrowIfCancellationRequested();

            using var cancellationSource = new CancellationTokenSource();
            await cancellationSource.CancelAsync();
            await onChanged(new PromptListChangedNotificationParams(), cancellationSource.Token);

            return new CountingAsyncDisposable(() =>
                Interlocked.Increment(ref _disposedSubscriptionCount)
            );
        }
    }

    private sealed class CountingAsyncDisposable(Action onDispose) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptySource(string sourceId) : TestMcpGatewayServerSource(sourceId);

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose()
        {
            throw new InvalidOperationException("prompt subscription dispose failure");
        }
    }
}

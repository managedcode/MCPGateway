using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewaySubscriptionEarlyCallbackTests
{
    private const string PromptListenerId = "listen:string:prompt-manager-test";
    private const string PromptSubscriptionId = "prompt-manager-test";

    [Test]
    public async Task ResourceListener_DoesNotDeadlockAndDisposesAfterCancellation()
    {
        var source = new EarlyResourceUpdateSource("source-a");
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(
            static _ => { },
            services =>
                services.AddSingleton<IMcpGatewayServerBindingResolver>(
                    new SingleSourceServerBindingResolver(source, CreateResourceCatalog())
                )
        );

        var listener = await McpTestSubscriptionListener.ListenAsync(
            gatewayServer.Client,
            new SubscriptionsListenNotifications
            {
                ResourceSubscriptions = ["docs://overview"],
            }
        );

        await listener.DisposeAsync();
        await WaitUntilAsync(() => source.DisposedSubscriptionCount == 1);
    }

    [Test]
    public async Task PromptListener_DisposesEarlySubscriptionAfterCancellation()
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

        var listener = await McpTestSubscriptionListener.ListenAsync(
            gatewayServer.Client,
            new SubscriptionsListenNotifications { PromptsListChanged = true }
        );

        await listener.DisposeAsync();
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
        var manager = CreatePromptManager(resolver, serviceProvider);

        await RegisterPromptListenerAsync(manager, gatewayServer.Server);

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

    [Test]
    public async Task RemoveAsync_ReleasesPromptSubscriptionAndBinding()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var promptSubscriptionDisposeCount = 0;
        var bindingDisposeCount = 0;
        var resolver = new SingleSourceServerBindingResolver(
            new EmptySource("source-a"),
            new StaticMcpGatewayResourceCatalog([]),
            subscribeToPromptListChanges: _ =>
                new CountingDisposable(() => Interlocked.Increment(ref promptSubscriptionDisposeCount)),
            onDisposed: () => Interlocked.Increment(ref bindingDisposeCount)
        );
        var manager = CreatePromptManager(resolver, serviceProvider);

        await RegisterPromptListenerAsync(manager, gatewayServer.Server);

        await manager.RemoveAsync(gatewayServer.Server, PromptListenerId);

        await Assert.That(promptSubscriptionDisposeCount).IsEqualTo(1);
        await Assert.That(bindingDisposeCount).IsEqualTo(1);
        await Assert.That(manager.ListenerStateCount).IsEqualTo(0);
    }

    [Test]
    public async Task RegisterAsync_ReleasesBindingWhenInitialPromptSubscriptionFails()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var bindingDisposeCount = 0;
        var resolver = new SingleSourceServerBindingResolver(
            new ThrowingPromptSubscriptionSource("source-a"),
            new StaticMcpGatewayResourceCatalog([]),
            onDisposed: () => Interlocked.Increment(ref bindingDisposeCount)
        );
        var manager = CreatePromptManager(resolver, serviceProvider);

        Exception? exception = null;
        try
        {
            await RegisterPromptListenerAsync(manager, gatewayServer.Server);
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains("prompt upstream subscribe failure");
        await Assert.That(bindingDisposeCount).IsEqualTo(1);
        await Assert.That(manager.ListenerStateCount).IsEqualTo(0);
    }

    [Test]
    public async Task RegisterAsync_ThrowsAfterManagerIsDisposed()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var resolver = new SingleSourceServerBindingResolver(
            new EmptySource("source-a"),
            new StaticMcpGatewayResourceCatalog([])
        );
        var manager = CreatePromptManager(resolver, serviceProvider);

        await manager.DisposeAsync();
        var exception = await CaptureAsync(
            RegisterPromptListenerAsync(manager, gatewayServer.Server)
        );

        await Assert.That(exception).IsTypeOf<ObjectDisposedException>();
        await Assert.That(manager.ListenerStateCount).IsEqualTo(0);
    }

    private static StaticMcpGatewayResourceCatalog CreateResourceCatalog() =>
        new(
            [
                new McpGatewayResourceDescriptor(
                    "source-a",
                    McpGatewaySourceKind.Local,
                    new Resource
                    {
                        Name = "overview",
                        Title = "overview",
                        Uri = "docs://overview",
                        Description = "Reads overview.",
                        MimeType = "text/plain",
                    }
                ),
            ]
        );

    private static McpGatewayPromptListNotificationManager CreatePromptManager(
        IMcpGatewayServerBindingResolver resolver,
        IServiceProvider serviceProvider
    )
    {
        var bindingManager = new McpGatewayMcpServerBindingManager(resolver);
        return new McpGatewayPromptListNotificationManager(
            bindingManager,
            new McpGatewayPromptNotificationStore(bindingManager),
            serviceProvider,
            NullLogger<McpGatewayPromptListNotificationManager>.Instance,
            NullLoggerFactory.Instance
        );
    }

    private static async Task RegisterPromptListenerAsync(
        McpGatewayPromptListNotificationManager manager,
        ModelContextProtocol.Server.McpServer server
    )
    {
        var deliveryGate = new McpGatewaySubscriptionDeliveryGate();
        await manager.RegisterAsync(
            requestServices: null,
            server,
            PromptListenerId,
            new RequestId(PromptSubscriptionId),
            deliveryGate,
            CancellationToken.None
        );
        await deliveryGate.OpenAsync(CancellationToken.None);
    }

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

    private sealed class EarlyResourceUpdateSource(string sourceId)
        : TestMcpGatewayServerSource(sourceId)
    {
        private int _disposedSubscriptionCount;

        public int DisposedSubscriptionCount => Volatile.Read(ref _disposedSubscriptionCount);

        public override async Task<IAsyncDisposable?> ListenForResourceUpdatesAsync(
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

        public override async Task<IAsyncDisposable?> ListenForPromptListChangesAsync(
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

    private sealed class CountingDisposable(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }

    private sealed class EmptySource(string sourceId) : TestMcpGatewayServerSource(sourceId);

    private sealed class ThrowingPromptSubscriptionSource(string sourceId)
        : TestMcpGatewayServerSource(sourceId)
    {
        public override Task<IAsyncDisposable?> ListenForPromptListChangesAsync(
            Func<PromptListChangedNotificationParams, CancellationToken, ValueTask> onChanged,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        )
        {
            _ = onChanged;
            _ = loggerFactory;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("prompt upstream subscribe failure");
        }
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose()
        {
            throw new InvalidOperationException("prompt subscription dispose failure");
        }
    }
}

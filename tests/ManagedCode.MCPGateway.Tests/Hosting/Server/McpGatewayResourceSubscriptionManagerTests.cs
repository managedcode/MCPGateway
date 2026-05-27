using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayResourceSubscriptionManagerTests
{
    [Test]
    public async Task SubscribeAsync_ReplacesExistingSubscriptionAndForwardsNotifications()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        var registration = new TrackingResourceRegistration("source-a");
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var manager = CreateManager(serviceProvider, registration);
        var notificationReceived = new TaskCompletionSource<ResourceUpdatedNotificationParams>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        await using var handler = gatewayServer.Client.RegisterNotificationHandler(
            NotificationMethods.ResourceUpdatedNotification,
            (notification, _) =>
            {
                var payload = notification.Params is null
                    ? null
                    : JsonSerializer.Deserialize<ResourceUpdatedNotificationParams>(
                        notification.Params.ToJsonString(),
                        McpGatewayJsonSerializer.Options
                    );
                if (payload is not null)
                {
                    notificationReceived.TrySetResult(payload);
                }

                return ValueTask.CompletedTask;
            }
        );

        var exposedUri = McpGatewayResourceUriCodec.ToGatewayUri("source-a", "docs://overview");

        await manager.SubscribeAsync(
            requestServices: null,
            gatewayServer.Server,
            exposedUri,
            CancellationToken.None
        );
        await manager.SubscribeAsync(
            requestServices: null,
            gatewayServer.Server,
            exposedUri,
            CancellationToken.None
        );
        await registration.EmitAsync(
            new ResourceUpdatedNotificationParams
            {
                Uri = "docs://overview",
                Meta = new System.Text.Json.Nodes.JsonObject { ["region"] = "eu" },
            },
            CancellationToken.None
        );
        var payload = await notificationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await manager.UnsubscribeAsync(gatewayServer.Server, exposedUri, CancellationToken.None);
        await manager.DisposeAsync();

        await Assert.That(registration.SubscriptionCount).IsEqualTo(2);
        await Assert.That(registration.DisposedSubscriptionCount).IsEqualTo(2);
        await Assert.That(payload.Uri).IsEqualTo(exposedUri);
        await Assert.That(manager.SubscriptionStateCount).IsEqualTo(0);
    }

    [Test]
    public async Task SubscribeAsync_RemovesSubscriptionWhenForwardingThrows()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        var registration = new TrackingResourceRegistration("source-a");
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var manager = CreateManager(serviceProvider, registration);
        using var cancellationSource = new CancellationTokenSource();

        await manager.SubscribeAsync(
            requestServices: null,
            gatewayServer.Server,
            "docs://overview",
            CancellationToken.None
        );
        cancellationSource.Cancel();
        await registration.EmitAsync(
            new ResourceUpdatedNotificationParams { Uri = "docs://overview" },
            cancellationSource.Token
        );

        await WaitUntilAsync(() => manager.SubscriptionStateCount == 0);
        await Assert.That(registration.DisposedSubscriptionCount).IsEqualTo(1);
    }

    [Test]
    public async Task SubscribeAsync_RemovesStateWhenSourceDoesNotSupportSubscriptions()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var manager = CreateManager(
            serviceProvider,
            new UnsupportedSubscriptionResourceSource("source-a")
        );

        Exception? exception = null;
        try
        {
            await manager.SubscribeAsync(
                requestServices: null,
                gatewayServer.Server,
                "docs://overview",
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsTypeOf<McpException>();
        await Assert.That(manager.SubscriptionStateCount).IsEqualTo(0);
    }

    [Test]
    public async Task UnsubscribeAsync_ReleasesPinnedBindingWhenSubscriptionDisposeFails()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        var source = new ThrowingDisposeResourceSource("source-a");
        var bindingDisposeCount = 0;
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var manager = CreateManager(
            serviceProvider,
            source,
            () => Interlocked.Increment(ref bindingDisposeCount)
        );

        await manager.SubscribeAsync(
            requestServices: null,
            gatewayServer.Server,
            "docs://overview",
            CancellationToken.None
        );

        Exception? exception = null;
        try
        {
            await manager.UnsubscribeAsync(
                gatewayServer.Server,
                "docs://overview",
                CancellationToken.None
            );
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsTypeOf<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains("subscription dispose failure");
        await Assert.That(bindingDisposeCount).IsEqualTo(1);
        await Assert.That(manager.SubscriptionStateCount).IsEqualTo(0);
    }

    [Test]
    public async Task RemoveSessionAsync_ReleasesSubscriptionAndPinnedBinding()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        var registration = new TrackingResourceRegistration("source-a");
        var bindingDisposeCount = 0;
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var manager = CreateManager(
            serviceProvider,
            new McpGatewayRegistrationBoundServerSource(registration),
            () => Interlocked.Increment(ref bindingDisposeCount)
        );

        await manager.SubscribeAsync(
            requestServices: null,
            gatewayServer.Server,
            "docs://overview",
            CancellationToken.None
        );

        await manager.RemoveSessionAsync(gatewayServer.Server.SessionId ?? string.Empty);

        await Assert.That(registration.DisposedSubscriptionCount).IsEqualTo(1);
        await Assert.That(bindingDisposeCount).IsEqualTo(1);
        await Assert.That(manager.SubscriptionStateCount).IsEqualTo(0);
    }

    [Test]
    public async Task SubscribeAsync_ThrowsAfterManagerIsDisposed()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var manager = CreateManager(serviceProvider, new TrackingResourceRegistration("source-a"));

        await manager.DisposeAsync();
        var exception = await CaptureAsync(
            manager.SubscribeAsync(
                requestServices: null,
                gatewayServer.Server,
                "docs://overview",
                CancellationToken.None
            )
        );

        await Assert.That(exception).IsTypeOf<ObjectDisposedException>();
        await Assert.That(manager.SubscriptionStateCount).IsEqualTo(0);
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

    private static McpGatewayResourceSubscriptionManager CreateManager(
        ServiceProvider serviceProvider,
        TrackingResourceRegistration registration
    ) => CreateManager(serviceProvider, new McpGatewayRegistrationBoundServerSource(registration));

    private static McpGatewayResourceSubscriptionManager CreateManager(
        ServiceProvider serviceProvider,
        IMcpGatewayServerSource source,
        Action? onBindingDisposed = null
    )
    {
        var resolver = new SingleSourceServerBindingResolver(
            source,
            new StaticMcpGatewayResourceCatalog(
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
            ),
            onDisposed: onBindingDisposed
        );

        return new McpGatewayResourceSubscriptionManager(
            new McpGatewayMcpServerBindingManager(resolver),
            serviceProvider,
            NullLogger<McpGatewayResourceSubscriptionManager>.Instance,
            NullLoggerFactory.Instance
        );
    }

    private sealed class TrackingResourceRegistration(string sourceId)
        : McpGatewayToolSourceRegistration(sourceId, null)
    {
        private Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask>? _onUpdated;

        public int SubscriptionCount { get; private set; }

        public int DisposedSubscriptionCount { get; private set; }

        public override McpGatewaySourceRegistrationKind Kind => McpGatewaySourceRegistrationKind.Local;

        public override ValueTask<IReadOnlyList<McpGatewayLoadedTool>> LoadToolsAsync(
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<IReadOnlyList<McpGatewayLoadedTool>>([]);

        public override Task<IAsyncDisposable?> SubscribeToResourceAsync(
            string resourceUri,
            Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken
        )
        {
            _onUpdated = onUpdated;
            SubscriptionCount++;
            return Task.FromResult<IAsyncDisposable?>(new TrackingSubscription(this));
        }

        public ValueTask EmitAsync(
            ResourceUpdatedNotificationParams notification,
            CancellationToken cancellationToken
        ) =>
            _onUpdated is null
                ? ValueTask.CompletedTask
                : _onUpdated(notification, cancellationToken);

        private sealed class TrackingSubscription(TrackingResourceRegistration owner)
            : IAsyncDisposable
        {
            private int _disposed;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.DisposedSubscriptionCount++;
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ThrowingDisposeResourceSource(string sourceId)
        : TestMcpGatewayServerSource(sourceId)
    {
        public override Task<IAsyncDisposable?> SubscribeToResourceAsync(
            string resourceUri,
            Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        )
        {
            _ = resourceUri;
            _ = onUpdated;
            _ = loggerFactory;
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<IAsyncDisposable?>(new ThrowingAsyncDisposable());
        }

        private sealed class ThrowingAsyncDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() =>
                ValueTask.FromException(
                    new InvalidOperationException("subscription dispose failure")
                );
        }
    }

    private sealed class UnsupportedSubscriptionResourceSource(string sourceId)
        : TestMcpGatewayServerSource(sourceId)
    {
        public override Task<IAsyncDisposable?> SubscribeToResourceAsync(
            string resourceUri,
            Func<ResourceUpdatedNotificationParams, CancellationToken, ValueTask> onUpdated,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            CancellationToken cancellationToken = default
        )
        {
            _ = resourceUri;
            _ = onUpdated;
            _ = loggerFactory;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable?>(null);
        }
    }
}

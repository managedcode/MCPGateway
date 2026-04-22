using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayResourceSubscriptionManagerTests
{
    [Test]
    public async Task SubscribeAsync_ReplacesExistingSubscriptionAndForwardsNotifications()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        var manager = new McpGatewayResourceSubscriptionManager(
            NullLogger<McpGatewayResourceSubscriptionManager>.Instance,
            NullLoggerFactory.Instance
        );
        var registration = new TrackingResourceRegistration("source-a");
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

        var request = new McpGatewayResolvedResourceRequest(
            "source-a",
            "docs://overview",
            McpGatewayResourceUriCodec.ToGatewayUri("source-a", "docs://overview"),
            UseGatewayUri: true,
            Registration: registration
        );

        await manager.SubscribeAsync(gatewayServer.Server, request, CancellationToken.None);
        await manager.SubscribeAsync(gatewayServer.Server, request, CancellationToken.None);
        await registration.EmitAsync(
            new ResourceUpdatedNotificationParams
            {
                Uri = "docs://overview",
                Meta = new System.Text.Json.Nodes.JsonObject { ["region"] = "eu" },
            },
            CancellationToken.None
        );
        var payload = await notificationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await manager.UnsubscribeAsync(gatewayServer.Server, request.ExposedUri, CancellationToken.None);
        await manager.DisposeAsync();

        await Assert.That(registration.SubscriptionCount).IsEqualTo(2);
        await Assert.That(registration.DisposedSubscriptionCount).IsEqualTo(2);
        await Assert.That(payload.Uri).IsEqualTo(request.ExposedUri);
    }

    [Test]
    public async Task SubscribeAsync_RemovesSubscriptionWhenForwardingThrows()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });
        var manager = new McpGatewayResourceSubscriptionManager(
            NullLogger<McpGatewayResourceSubscriptionManager>.Instance,
            NullLoggerFactory.Instance
        );
        var registration = new TrackingResourceRegistration("source-a");
        var request = new McpGatewayResolvedResourceRequest(
            "source-a",
            "docs://overview",
            "docs://overview",
            UseGatewayUri: false,
            Registration: registration
        );
        using var cancellationSource = new CancellationTokenSource();

        await manager.SubscribeAsync(gatewayServer.Server, request, CancellationToken.None);
        cancellationSource.Cancel();
        await registration.EmitAsync(
            new ResourceUpdatedNotificationParams { Uri = "docs://overview" },
            cancellationSource.Token
        );

        await Assert.That(registration.DisposedSubscriptionCount).IsEqualTo(1);
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
}

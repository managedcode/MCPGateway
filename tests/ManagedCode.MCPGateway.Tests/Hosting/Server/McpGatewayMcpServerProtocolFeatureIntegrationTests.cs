using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayMcpServerProtocolFeatureIntegrationTests
{
    [Test]
    public async Task CompleteAsync_CompletesPromptArgumentFromAggregatedUpstreamServer()
    {
        await using var upstreamServer = await TestMcpProtocolFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
        });

        var completion = await gatewayServer.Client.CompleteAsync(
            new PromptReference { Name = $"source-a:{TestMcpProtocolFeatureServerHost.PromptName}" },
            TestMcpProtocolFeatureServerHost.PromptArgumentName,
            "Managed"
        );

        await Assert.That(gatewayServer.Client.ServerCapabilities.Completions).IsNotNull();
        await Assert.That(completion.Completion.Values).IsEquivalentTo(
            ["ManagedCode/MCPGateway", "ManagedCode/AIBase"]
        );
        await Assert.That(completion.Completion.Total).IsEqualTo(2);
    }

    [Test]
    public async Task CompleteAsync_CompletesResourceTemplateArgumentFromAggregatedUpstreamServer()
    {
        await using var upstreamServer = await TestMcpProtocolFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
        });

        var template = (await gatewayServer.Client.ListResourceTemplatesAsync()).Single(
            static candidate =>
                candidate.Name == $"source-a:{TestMcpProtocolFeatureServerHost.ResourceTemplateName}"
        );
        var completion = await gatewayServer.Client.CompleteAsync(
            new ResourceTemplateReference { Uri = template.UriTemplate },
            TestMcpProtocolFeatureServerHost.ResourceTemplateArgumentName,
            "model"
        );

        await Assert.That(completion.Completion.Values).IsEquivalentTo(["modelcontextprotocol"]);
        await Assert.That(completion.Completion.Total).IsEqualTo(1);
    }

    [Test]
    public async Task SubscribeToResourceAsync_ForwardsUpstreamResourceUpdatedNotification()
    {
        await using var upstreamServer = await TestMcpProtocolFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
        });

        var updatedResource = new TaskCompletionSource<ResourceUpdatedNotificationParams>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var notificationRegistration = gatewayServer.Client.RegisterNotificationHandler(
            NotificationMethods.ResourceUpdatedNotification,
            (notification, _) =>
            {
                var payload = notification.Params?.Deserialize<ResourceUpdatedNotificationParams>();
                if (payload is not null)
                {
                    updatedResource.TrySetResult(payload);
                }

                return ValueTask.CompletedTask;
            }
        );

        var resource = (await gatewayServer.Client.ListResourcesAsync()).Single(static candidate =>
            candidate.Name == $"source-a:{TestMcpProtocolFeatureServerHost.ResourceName}"
        );

        await gatewayServer.Client.SubscribeToResourceAsync(resource.Uri);
        await upstreamServer.EmitResourceUpdatedAsync();

        var payload = await updatedResource.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(gatewayServer.Client.ServerCapabilities.Resources?.Subscribe).IsTrue();
        await Assert.That(payload.Uri).IsEqualTo(resource.Uri);
    }

    [Test]
    public async Task UnsubscribeFromResourceAsync_StopsForwardingUpstreamNotifications()
    {
        await using var upstreamServer = await TestMcpProtocolFeatureServerHost.StartAsync();
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(options =>
        {
            options.AddMcpClient("source-a", upstreamServer.Client, disposeClient: false);
        });

        var notificationCount = 0;
        var firstNotification = new TaskCompletionSource<ResourceUpdatedNotificationParams>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var secondNotification = new TaskCompletionSource<ResourceUpdatedNotificationParams>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        await using var notificationRegistration = gatewayServer.Client.RegisterNotificationHandler(
            NotificationMethods.ResourceUpdatedNotification,
            (notification, _) =>
            {
                var payload = notification.Params?.Deserialize<ResourceUpdatedNotificationParams>();
                if (payload is null)
                {
                    return ValueTask.CompletedTask;
                }

                notificationCount++;
                if (notificationCount == 1)
                {
                    firstNotification.TrySetResult(payload);
                }
                else
                {
                    secondNotification.TrySetResult(payload);
                }

                return ValueTask.CompletedTask;
            }
        );

        var resource = (await gatewayServer.Client.ListResourcesAsync()).Single(static candidate =>
            candidate.Name == $"source-a:{TestMcpProtocolFeatureServerHost.ResourceName}"
        );

        await gatewayServer.Client.SubscribeToResourceAsync(resource.Uri);
        await upstreamServer.EmitResourceUpdatedAsync();
        _ = await firstNotification.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await gatewayServer.Client.UnsubscribeFromResourceAsync(resource.Uri);
        await upstreamServer.EmitResourceUpdatedAsync();

        var completedTask = await Task.WhenAny(
            secondNotification.Task,
            Task.Delay(TimeSpan.FromMilliseconds(300))
        );

        await Assert.That(ReferenceEquals(completedTask, secondNotification.Task)).IsFalse();
        await Assert.That(notificationCount).IsEqualTo(1);
    }

    [Test]
    public async Task SetLoggingLevelAsync_AdvertisesLoggingCapabilityAndUpdatesServerLevel()
    {
        await using var gatewayServer = await GatewayMcpServerHost.StartAsync(static _ => { });

        await gatewayServer.Client.SetLoggingLevelAsync(ModelContextProtocol.Protocol.LoggingLevel.Debug);

        await Assert.That(gatewayServer.Client.ServerCapabilities.Logging).IsNotNull();
        await Assert.That(gatewayServer.Server.LoggingLevel).IsEqualTo(
            ModelContextProtocol.Protocol.LoggingLevel.Debug
        );
    }
}

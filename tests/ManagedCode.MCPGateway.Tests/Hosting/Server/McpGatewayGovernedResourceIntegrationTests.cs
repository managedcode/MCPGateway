using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayGovernedResourceIntegrationTests
{
    [Test]
    public async Task SDK_resource_updates_strip_private_meta_and_stop_delivery_after_live_revocation()
    {
        await using var upstream = await TestMcpProtocolFeatureServerHost.StartAsync();
        var policy = new Policy();
        await using var host = await GatewayMcpServerHost.StartAsync(options => options.AddMcpClient("docs", upstream.Client), services =>
        { services.AddSingleton<IMcpGatewayExecutionContextProvider, Actor>(); services.AddSingleton<IMcpGatewayAccessPolicy>(policy); });
        var first = new TaskCompletionSource<ResourceUpdatedNotificationParams>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = 0;
        await using var handler = host.Client.RegisterNotificationHandler(NotificationMethods.ResourceUpdatedNotification, (message, _) =>
        {
            Interlocked.Increment(ref deliveries);
            first.TrySetResult(message.Params!.Deserialize<ResourceUpdatedNotificationParams>()!);
            return ValueTask.CompletedTask;
        });
        var resource = (await host.Client.ListResourcesAsync()).Single();
        await using var listener = await McpTestSubscriptionListener.ListenAsync(host.Client,
            new SubscriptionsListenNotifications { ResourceSubscriptions = [resource.Uri] });
        await upstream.EmitResourceUpdatedAsync(meta: new() { ["private"] = "SEEDED_PRIVATE_META" });
        var received = await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(JsonSerializer.Serialize(received, McpGatewayJsonSerializer.Options)).DoesNotContain("SEEDED_");
        await Assert.That(received.Meta?[MetaKeys.SubscriptionId]).IsNotNull();
        policy.AllowUpdates = false;
        await upstream.EmitResourceUpdatedAsync();
        await policy.Denied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.Client.ListResourcesAsync();
        await Assert.That(deliveries).IsEqualTo(1);
    }
    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpGatewayExecutionContext("alice", "tenant", "docs", "v1"));
    }
    private sealed class Policy : IMcpGatewayAccessPolicy
    {
        public bool AllowUpdates = true;
        public TaskCompletionSource Denied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            var allowed = request.Operation != McpGatewayOperation.ReceiveResourceUpdate || AllowUpdates;
            if (!allowed)
            { Denied.TrySetResult(); }
            return ValueTask.FromResult(new McpGatewayAccessDecision(allowed));
        }
    }
}

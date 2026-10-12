using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayGovernedPromptIntegrationTests
{
    [Test]
    public async Task SDK_prompt_change_delivery_rechecks_the_captured_actor_after_live_revocation()
    {
        await using var upstream = await TestMcpPromptListFeatureServerHost.StartAsync();
        var policy = new Policy();
        await using var host = await GatewayMcpServerHost.StartAsync(options => options.AddMcpClient("prompts", upstream.Client), services =>
        { services.AddSingleton<IMcpGatewayExecutionContextProvider, Actor>(); services.AddSingleton<IMcpGatewayAccessPolicy>(policy); });
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = 0;
        await using var handler = host.Client.RegisterNotificationHandler(NotificationMethods.PromptListChangedNotification, (_, _) =>
        { Interlocked.Increment(ref deliveries); received.TrySetResult(); return ValueTask.CompletedTask; });
        await using var listener = await McpTestSubscriptionListener.ListenAsync(host.Client,
            new SubscriptionsListenNotifications { PromptsListChanged = true });
        await upstream.AddPromptAsync("first_prompt");
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        policy.AllowUpdates = false;
        await upstream.AddPromptAsync("second_prompt");
        await policy.Denied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.Client.ListPromptsAsync();
        await Assert.That(deliveries).IsEqualTo(1);
        await Assert.That(policy.LastSubject).IsEqualTo("alice");
    }

    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpGatewayExecutionContext("alice", "tenant", "prompts", "v1"));
    }
    private sealed class Policy : IMcpGatewayAccessPolicy
    {
        public bool AllowUpdates = true;
        public string? LastSubject;
        public TaskCompletionSource Denied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Operation != McpGatewayOperation.ReceivePromptChanges)
            { return ValueTask.FromResult(new McpGatewayAccessDecision(true)); }
            LastSubject = context.SubjectId;
            if (!AllowUpdates)
            { Denied.TrySetResult(); }
            return ValueTask.FromResult(new McpGatewayAccessDecision(AllowUpdates));
        }
    }
}

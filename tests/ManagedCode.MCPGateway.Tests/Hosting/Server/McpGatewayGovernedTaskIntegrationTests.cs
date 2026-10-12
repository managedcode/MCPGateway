using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayGovernedTaskIntegrationTests
{
    [TUnit.Core.Test]
    public async Task SDK_task_reads_and_cancellation_are_bound_to_actor_and_live_access_policy()
    {
        var actor = new Actor();
        var policy = new Policy();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await GatewayMcpServerHost.StartAsync(options => options.AddTool("tasks",
            AIFunctionFactory.Create(async (CancellationToken ct) => { await release.Task.WaitAsync(ct); return "SECRETSEED_RESULT"; },
                new AIFunctionFactoryOptions { Name = "long_operation" })), services =>
            {
                services.AddSingleton<IMcpGatewayExecutionContextProvider>(actor);
                services.AddSingleton<IMcpGatewayAccessPolicy>(policy);
            });
        try
        {
            var created = await host.Client.CallToolAsTaskAsync(new CallToolRequestParams { Name = "long_operation" });
            await Assert.That(created.IsTask).IsTrue();
            var id = created.TaskCreated!.TaskId;
            await Assert.That(await host.Client.GetTaskAsync(id)).IsNotNull();
            actor.Current = actor.Current with { SubjectId = "bob" };
            await AssertDeniedAsync(async () => { await host.Client.GetTaskAsync(id); });
            await AssertDeniedAsync(async () => { await host.Client.CancelTaskAsync(id); });
            actor.Current = actor.Current with { SubjectId = "alice" };
            policy.AllowTaskAccess = false;
            await AssertDeniedAsync(async () => { await host.Client.GetTaskAsync(id); });
            policy.AllowTaskAccess = true;
            release.TrySetResult();
            var completed = await host.Client.CallToolWithPollingAsync(new CallToolRequestParams { Name = "long_operation" });
            await Assert.That(((TextContentBlock)completed.Content[0]).Text).IsEqualTo("SECRETSEED_RESULT");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task AssertDeniedAsync(Func<Task> operation)
    {
        try
        { await operation(); Assert.Fail("A different or revoked caller must not access the task."); }
        catch (ModelContextProtocol.McpException exception)
        {
            await Assert.That(exception.Message).DoesNotContain("SECRETSEED_RESULT");
        }
    }
    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        public McpGatewayExecutionContext Current { get; set; } = new("alice", "tenant", "partition", "v1");
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
    }
    private sealed class Policy : IMcpGatewayAccessPolicy
    {
        public bool AllowTaskAccess { get; set; } = true;
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new McpGatewayAccessDecision(
                request.Operation != McpGatewayOperation.AccessTask || AllowTaskAccess));
    }
}

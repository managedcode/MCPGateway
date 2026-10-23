using System.Text.Json;
using ManagedCode.MCPGateway.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.MCPGateway.Tests;

public sealed class McpGatewayExecutionRevocationTests
{
    [Test]
    public async Task Queued_call_rechecks_live_policy_before_dispatch_and_does_not_block_another_source()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var policy = new Policy();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options =>
        {
            options.Execution.Enabled = true;
            options.Execution.Sources.Add("blocked", new() { MaxConcurrency = 1, QueueCapacity = 1 });
            options.AddTool("blocked", AIFunctionFactory.Create(async () =>
            { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; return "done"; }, new AIFunctionFactoryOptions { Name = "blocked_call" }));
            options.AddTool("healthy", AIFunctionFactory.Create(static () => "healthy", new AIFunctionFactoryOptions { Name = "healthy_call" }));
        });
        services.AddSingleton<IMcpGatewayAccessPolicy>(policy);
        services.AddSingleton<IMcpGatewayExecutionContextProvider, Actor>();
        await using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IMcpGateway>();
        var first = gateway.InvokeAsync(new(ToolId: "blocked_call"));
        Task<McpGatewayInvokeResult>? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = gateway.InvokeAsync(new(ToolId: "blocked_call"));
            await policy.SecondRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That((await gateway.InvokeAsync(new(ToolId: "healthy_call"))).IsSuccess).IsTrue();
            policy.AllowBlocked = false;
        }
        finally { release.TrySetResult(); await first; }
        await Assert.That((await second!).Outcome).IsEqualTo(McpGatewayInvocationOutcome.Denied);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Caller_cancellation_of_dispatched_read_only_work_is_cancelled_instead_of_timed_out()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options =>
        {
            options.Execution.Enabled = true;
            options.AddTool("read", AIFunctionFactory.Create(async (CancellationToken token) =>
            { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return "unreachable"; }, new AIFunctionFactoryOptions { Name = "read_call" }));
        });
        services.AddSingleton<IMcpGatewayAccessPolicy>(new Policy());
        services.AddSingleton<IMcpGatewayExecutionContextProvider, Actor>();
        await using var provider = services.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var call = provider.GetRequiredService<IMcpGateway>().InvokeAsync(new(ToolId: "read_call"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That((await call.WaitAsync(TimeSpan.FromSeconds(5))).Outcome).IsEqualTo(McpGatewayInvocationOutcome.Cancelled);
    }

    [Test]
    public async Task Trusted_deadline_is_enforced_with_admission_disabled_and_model_context_cannot_extend_it()
    {
        var actor = new Actor();
        var calls = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpGateway(options => options.AddTool("read", AIFunctionFactory.Create(async (CancellationToken token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            { return "warm"; }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        }, new AIFunctionFactoryOptions { Name = "deadline_read" })));
        services.AddSingleton<IMcpGatewayExecutionContextProvider>(actor);
        services.AddSingleton<IMcpGatewayAccessPolicy>(new Policy());
        await using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IMcpGateway>();
        await Assert.That((await gateway.InvokeAsync(new(ToolId: "deadline_read"))).IsSuccess).IsTrue();
        actor.Deadline = DateTimeOffset.UtcNow.AddMilliseconds(300);
        using var guard = new CancellationTokenSource();
        try
        {
            var call = gateway.InvokeAsync(new(ToolId: "deadline_read", Context: new Dictionary<string, JsonElement> { ["deadline"] = JsonSerializer.SerializeToElement("2099-01-01", McpGatewayJsonSerializer.Options) }), guard.Token);
            await Assert.That((await call.WaitAsync(TimeSpan.FromSeconds(5))).Outcome).IsEqualTo(McpGatewayInvocationOutcome.TimedOut);
        }
        finally { await guard.CancelAsync(); }
    }

    private sealed class Actor : IMcpGatewayExecutionContextProvider
    {
        public DateTimeOffset? Deadline { get; set; }
        public ValueTask<McpGatewayExecutionContext> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new McpGatewayExecutionContext("alice", "tenant", "all", "v1") { Deadline = Deadline });
    }
    private sealed class Policy : IMcpGatewayAccessPolicy
    {
        private int _blockedRequests;
        public volatile bool AllowBlocked = true;
        public TaskCompletionSource SecondRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<McpGatewayAccessDecision> AuthorizeAsync(McpGatewayExecutionContext context, McpGatewayAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Operation == McpGatewayOperation.Invoke && request.SourceId == "blocked" && Interlocked.Increment(ref _blockedRequests) == 3)
            { SecondRequest.TrySetResult(); }
            return ValueTask.FromResult(new McpGatewayAccessDecision(request.SourceId != "blocked" || AllowBlocked) { IsReadOnly = true });
        }
    }
}
